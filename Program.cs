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
config.TwilioAccountSid = configuration["TWILIO_ACCOUNT_SID"] ?? config.TwilioAccountSid;
config.TwilioAuthToken = configuration["TWILIO_AUTH_TOKEN"] ?? config.TwilioAuthToken;

Validate(config);

// --- Composition des services ---
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

var reader = new EmailReader(config);
var classifier = new EmailClassifier(config, http);
INotifier notifier = new WhatsAppNotifier(config);

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
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Lecture des mails non lus..."
        + (dryRun ? "  [MODE TEST : aucune action]" : ""));
    var emails = await reader.GetUnreadAsync(ct);
    Console.WriteLine($"  {emails.Count} mail(s) a traiter.");

    var toKeep = new List<MailKit.UniqueId>(emails.Count);   // restent dans la boite (marquage anti-doublon)
    var toMove = new List<MailKit.UniqueId>(emails.Count);   // ranges dans le dossier dedie

    foreach (var email in emails)
    {
        var result = await classifier.ClassifyAsync(email, ct);
        var tag = result.Important ? "IMPORTANT" : result.Declutter ? "RANGER   " : "garder   ";
        Console.WriteLine($"  [{tag}] {email.Subject}  ({result.Category}) - {result.Reason}");

        if (dryRun)
        {
            if (result.Important) Console.WriteLine("    -> (test) notification WhatsApp");
            if (result.Declutter) Console.WriteLine($"    -> (test) deplacement vers '{config.Imap.SortFolder}'");
            continue;
        }

        if (result.Important)
        {
            await notifier.NotifyAsync(email, result, ct);
            Console.WriteLine("    -> notification WhatsApp envoyee.");
        }

        if (result.Declutter) toMove.Add(email.Uid);
        else toKeep.Add(email.Uid);
    }

    if (dryRun)
    {
        Console.WriteLine("Mode test : rien n'a ete modifie dans la boite.");
        return;
    }

    // Range les inutiles, puis marque le reste pour ne pas le reanalyser.
    await reader.MoveToFolderAsync(toMove, config.Imap.SortFolder, ct);
    await reader.MarkNotifiedAsync(toKeep, ct);
    if (toMove.Count > 0)
        Console.WriteLine($"  {toMove.Count} mail(s) range(s) dans '{config.Imap.SortFolder}'.");
}

static void Validate(AgentConfig c)
{
    var missing = new List<string>();
    if (string.IsNullOrWhiteSpace(c.ImapUser)) missing.Add("IMAP_USER");
    if (string.IsNullOrWhiteSpace(c.ImapPassword)) missing.Add("IMAP_PASS");
    if (string.IsNullOrWhiteSpace(c.AnthropicApiKey)) missing.Add("ANTHROPIC_API_KEY");
    if (string.IsNullOrWhiteSpace(c.TwilioAccountSid)) missing.Add("TWILIO_ACCOUNT_SID");
    if (string.IsNullOrWhiteSpace(c.TwilioAuthToken)) missing.Add("TWILIO_AUTH_TOKEN");
    if (string.IsNullOrWhiteSpace(c.WhatsApp.ToNumber)) missing.Add("WhatsApp:ToNumber (appsettings.json)");

    if (missing.Count == 0) return;

    Console.WriteLine("Configuration incomplete. Elements manquants :");
    foreach (var m in missing) Console.WriteLine($"  - {m}");
    Console.WriteLine("\nVoir le README pour le detail du parametrage.");
    Environment.Exit(1);
}
