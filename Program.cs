using System.Diagnostics;
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
config.Calendar.ClientId = configuration["GOOGLE_CLIENT_ID"] ?? config.Calendar.ClientId;
config.Calendar.ClientSecret = configuration["GOOGLE_CLIENT_SECRET"] ?? config.Calendar.ClientSecret;
config.Calendar.RefreshToken = configuration["GOOGLE_REFRESH_TOKEN"] ?? config.Calendar.RefreshToken;

Validate(config);

// --- Composition des services ---
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

var reader = new EmailReader(config);
var classifier = new EmailClassifier(config, http);
INotifier notifier = new TelegramNotifier(config, http);
var sender = new EmailSender(config);
var calendar = new GoogleCalendar(config, http);
var conversation = new TelegramConversation(config, http, reader, sender);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine($"MailAgent demarre. Mode : {(config.Agent.RunOnce ? "une passe" : "boucle continue")}.");

var runClock = Stopwatch.StartNew();
do
{
    var processed = 0;
    try
    {
        processed = await RunOnceAsync(reader, classifier, notifier, conversation, calendar, config, cts.Token);
    }
    catch (OperationCanceledException) { break; }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERREUR] {ex.Message}");
    }

    if (config.Agent.RunOnce)
    {
        // Vide le backlog : tant qu'une fournee PLEINE a ete traitee (donc il reste probablement
        // des mails) et que le budget temps n'est pas atteint, on enchaine une autre fournee.
        var budgetReached = config.Agent.MaxRunSeconds > 0
            && runClock.Elapsed.TotalSeconds >= config.Agent.MaxRunSeconds;
        if (processed >= config.Imap.MaxPerPass && !budgetReached) continue;
        break;
    }

    Console.WriteLine($"Prochaine verification dans {config.Agent.PollIntervalSeconds}s... (Ctrl+C pour arreter)");
    try { await Task.Delay(TimeSpan.FromSeconds(config.Agent.PollIntervalSeconds), cts.Token); }
    catch (OperationCanceledException) { break; }
}
while (!cts.IsCancellationRequested);

Console.WriteLine("Agent arrete.");


static async Task<int> RunOnceAsync(
    EmailReader reader, EmailClassifier classifier, INotifier notifier,
    TelegramConversation conversation, GoogleCalendar calendar, AgentConfig config, CancellationToken ct)
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
    var toTrash = new List<MailKit.UniqueId>();                         // expediteurs auto-supprimes -> corbeille
    var importantEmails = new List<MailAgent.Models.EmailItem>();       // contexte pour l'assistant Telegram

    foreach (var email in emails)
    {
        // Un mail en erreur (API, IMAP) ne doit pas arreter toute la passe : on le
        // journalise et on continue. N'etant ni marque ni deplace, il sera retente plus tard.
        try
        {
            // Expediteurs auto-supprimes : direct corbeille, sans analyse ni notification.
            if (IsBlocked(email.From, config.Classifier.BlockedSenders))
            {
                Console.WriteLine($"  [CORBEILLE    ] {(email.Seen ? "lu   " : "nonlu")} {email.Subject}  - expediteur auto-supprime");
                if (!dryRun) toTrash.Add(email.Uid);
                continue;
            }

            var result = await classifier.ClassifyAsync(email, ct);
            // Important = action a faire OU sujet/personne prioritaire (ex. les enfants). Dans les
            // deux cas on notifie (si pas deja repondu) et le mail reste TOUJOURS en boite, pour ne
            // pas le faire disparaitre. Sinon : on classe vers une nature autorisee uniquement.
            var important = result.ActionRequired || result.Priority;
            var notify = important && !email.Answered;
            if (important) importantEmails.Add(email);
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
                if (notify) Console.WriteLine("    -> (test) notification Telegram");
                if (folder.Length > 0) Console.WriteLine($"    -> (test) classement dans '{folder}'");
                continue;
            }

            if (notify)
            {
                await notifier.NotifyAsync(email, result, ct);
                Console.WriteLine("    -> notification Telegram envoyee.");
            }

            // Evenement date detecte -> ajout a l'agenda + notification (si l'agenda est configure).
            // Le mail est ensuite marque/traite normalement : pas de re-creation aux passes suivantes.
            if (result.Event is not null && calendar.IsConfigured)
            {
                try
                {
                    await calendar.CreateEventAsync(result.Event,
                        $"Ajoute automatiquement depuis le mail \"{email.Subject}\" (de {email.From}).", ct);
                    await notifier.SendTextAsync(
                        $"📅 Ajoute a ton agenda : {result.Event.Title} ({result.Event.Start}).", ct);
                    Console.WriteLine($"      Agenda : evenement cree ({result.Event.Title}).");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"      [Agenda] echec (non bloquant) : {ex.Message}");
                }
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
        return emails.Count;
    }

    // Classe chaque mail dans son dossier, puis marque ceux gardes en boite (anti-doublon).
    foreach (var (folder, uids) in toMove)
    {
        await reader.MoveToFolderAsync(uids, folder, ct);
        Console.WriteLine($"  {uids.Count} mail(s) classe(s) dans '{folder}'.");
    }
    if (toTrash.Count > 0)
    {
        await reader.MoveToTrashAsync(toTrash, ct);
        Console.WriteLine($"  {toTrash.Count} mail(s) mis a la corbeille (expediteurs auto-supprimes).");
    }
    await reader.MarkNotifiedAsync(toKeep, ct);

    // Volet conversationnel : lit les messages Telegram en attente et y repond.
    await conversation.RunAsync(importantEmails, ct);

    return emails.Count;
}

// Vrai si l'expediteur correspond a un fragment de la liste des expediteurs auto-supprimes.
static bool IsBlocked(string from, string[] blocked)
{
    if (blocked is null || blocked.Length == 0 || string.IsNullOrEmpty(from)) return false;
    foreach (var b in blocked)
        if (!string.IsNullOrWhiteSpace(b) && from.Contains(b, StringComparison.OrdinalIgnoreCase))
            return true;
    return false;
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
