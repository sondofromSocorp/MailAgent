using System.Globalization;
using MailAgent.Configuration;
using MailAgent.Services;
using Microsoft.Extensions.Configuration;

// --- Chargement de la configuration ---
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

var config = new AgentConfig();
configuration.Bind(config);

// Secrets : variables d'environnement prioritaires (simples a injecter en local / CI / Azure).
config.ImapUser = configuration["IMAP_USER"] ?? config.ImapUser;
config.ImapPassword = configuration["IMAP_PASS"] ?? config.ImapPassword;
config.AnthropicApiKey = configuration["ANTHROPIC_API_KEY"] ?? config.AnthropicApiKey;
config.Telegram.BotToken = configuration["TELEGRAM_BOT_TOKEN"] ?? config.Telegram.BotToken;
config.Telegram.ChatId = configuration["TELEGRAM_CHAT_ID"] ?? config.Telegram.ChatId;

Validate(config);

// --- Composition des services ---
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

var reader = new EmailReader(config);
var classifier = new EmailClassifier(config, http);
INotifier notifier = new TelegramNotifier(config, http);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine($"MailAgent demarre. Mode : {(config.Agent.RunOnce ? "une passe" : "boucle continue")}.");

do
{
    try
    {
        await RunOnceAsync(reader, classifier, notifier, config, cts.Token);
    }
    catch (OperationCanceledException) { break; }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERREUR] {ex.Message}");
    }

    if (config.Agent.RunOnce) break;

    Console.WriteLine($"Prochaine verification dans {config.Agent.PollIntervalSeconds}s... (Ctrl+C pour arreter)");
    try { await Task.Delay(TimeSpan.FromSeconds(config.Agent.PollIntervalSeconds), cts.Token); }
    catch (OperationCanceledException) { break; }
}
while (!cts.IsCancellationRequested);

Console.WriteLine("Agent arrete.");


static async Task RunOnceAsync(
    EmailReader reader, EmailClassifier classifier, INotifier notifier,
    AgentConfig config, CancellationToken ct)
{
    var dryRun = config.Agent.DryRun;
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Lecture des mails a traiter..."
        + (dryRun ? "  [MODE TEST : aucune action]" : ""));
    var emails = await reader.GetToProcessAsync(ct);
    Console.WriteLine($"  {emails.Count} mail(s) a traiter.");

    // Heures silencieuses : pendant la plage de nuit, les notifications sont reportees.
    var quiet = IsQuietNow(config.Agent);
    if (quiet) Console.WriteLine("  Heures silencieuses : notifications suspendues (rangement maintenu).");

    var toKeep = new List<MailKit.UniqueId>(emails.Count);              // restent en boite (marquage anti-doublon)
    var toMove = new Dictionary<string, List<MailKit.UniqueId>>();      // dossier -> mails a classer

    foreach (var email in emails)
    {
        // Un mail en erreur (API, Twilio, IMAP) ne doit pas arreter toute la passe : on le
        // journalise et on continue. N'etant ni marque ni deplace, il sera retente plus tard.
        try
        {
            var result = await classifier.ClassifyAsync(email, ct);
            // Important = action a faire OU sujet/personne prioritaire (ex. les enfants). Dans les
            // deux cas on notifie (si pas deja repondu) et le mail reste TOUJOURS en boite, pour ne
            // pas le faire disparaitre. Sinon : on classe vers une nature autorisee uniquement.
            var important = result.ActionRequired || result.Priority;
            var notify = important && !email.Answered;
            var nature = important
                ? ""
                : Array.IndexOf(config.Imap.Folders, result.Folder) >= 0 ? result.Folder : "";

            // Sous-dossier par emetteur seulement pour certaines natures (ex. Factures/Bouygues),
            // afin de retrouver une facture par emetteur sans multiplier les dossiers ailleurs.
            var folder = nature.Length > 0
                && result.Source.Length > 0
                && Array.IndexOf(config.Imap.SubfolderBySource, nature) >= 0
                    ? $"{nature}/{result.Source}"
                    : nature;

            // Heures silencieuses : une notif attendue est reportee. On laisse le mail en
            // boite SANS le marquer, pour qu'il soit repris et notifie apres la plage silencieuse.
            if (notify && quiet)
            {
                Console.WriteLine($"  [DIFFERE      ] {(email.Seen ? "lu   " : "nonlu")} {email.Subject}  - notif reportee (heures silencieuses)");
                continue;
            }

            var tag = notify ? (result.ActionRequired ? "ACTION" : "PRIORITAIRE") : folder.Length > 0 ? folder : "garder";
            Console.WriteLine($"  [{tag,-13}] {(email.Seen ? "lu   " : "nonlu")} {email.Subject}  - {result.Reason}");
            if (notify && result.Action.Length > 0)
                Console.WriteLine($"      Action : {result.Action}");

            if (dryRun)
            {
                if (notify) Console.WriteLine("    -> (test) notification WhatsApp");
                if (folder.Length > 0) Console.WriteLine($"    -> (test) classement dans '{folder}'");
                continue;
            }

            if (notify)
            {
                await notifier.NotifyAsync(email, result, ct);
                Console.WriteLine("    -> notification WhatsApp envoyee.");
            }

            if (folder.Length > 0)
            {
                if (!toMove.TryGetValue(folder, out var list)) toMove[folder] = list = [];
                list.Add(email.Uid);
            }
            else toKeep.Add(email.Uid);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.WriteLine($"  [ERREUR       ] {email.Subject}  - {ex.Message}");
        }
    }

    if (dryRun)
    {
        Console.WriteLine("Mode test : rien n'a ete modifie dans la boite.");
        return;
    }

    // Classe chaque mail dans son dossier, puis marque ceux gardes en boite (anti-doublon).
    foreach (var (folder, uids) in toMove)
    {
        await reader.MoveToFolderAsync(uids, folder, ct);
        Console.WriteLine($"  {uids.Count} mail(s) classe(s) dans '{folder}'.");
    }
    await reader.MarkNotifiedAsync(toKeep, ct);
}

// Indique si l'heure courante (dans le fuseau configure) tombe dans la plage silencieuse.
// La plage peut traverser minuit (ex. 22:00 -> 06:30).
static bool IsQuietNow(RuntimeConfig agent)
{
    if (!agent.QuietHoursEnabled) return false;

    TimeZoneInfo tz;
    try { tz = TimeZoneInfo.FindSystemTimeZoneById(agent.TimeZone); }
    catch (TimeZoneNotFoundException) { tz = TimeZoneInfo.Local; }
    catch (InvalidTimeZoneException) { tz = TimeZoneInfo.Local; }

    var now = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz).DateTime);
    var start = TimeOnly.Parse(agent.QuietStart, CultureInfo.InvariantCulture);
    var end = TimeOnly.Parse(agent.QuietEnd, CultureInfo.InvariantCulture);

    return start <= end
        ? now >= start && now < end          // plage dans la meme journee
        : now >= start || now < end;         // plage traversant minuit
}

static void Validate(AgentConfig c)
{
    var missing = new List<string>();
    if (string.IsNullOrWhiteSpace(c.ImapUser)) missing.Add("IMAP_USER");
    if (string.IsNullOrWhiteSpace(c.ImapPassword)) missing.Add("IMAP_PASS");
    if (string.IsNullOrWhiteSpace(c.AnthropicApiKey)) missing.Add("ANTHROPIC_API_KEY");
    if (string.IsNullOrWhiteSpace(c.Telegram.BotToken)) missing.Add("TELEGRAM_BOT_TOKEN");
    if (string.IsNullOrWhiteSpace(c.Telegram.ChatId)) missing.Add("TELEGRAM_CHAT_ID");

    if (missing.Count == 0) return;

    Console.WriteLine("Configuration incomplete. Elements manquants :");
    foreach (var m in missing) Console.WriteLine($"  - {m}");
    Console.WriteLine("\nVoir le README pour le detail du parametrage.");
    Environment.Exit(1);
}
