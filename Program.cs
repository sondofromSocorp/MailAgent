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
        await RunOnceAsync(reader, classifier, notifier, cts.Token);
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
    CancellationToken ct)
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Lecture des mails non lus...");
    var emails = await reader.GetUnreadAsync(ct);
    Console.WriteLine($"  {emails.Count} mail(s) non lu(s) a traiter.");

    var handledUids = new List<MailKit.UniqueId>(emails.Count);
    foreach (var email in emails)
    {
        var result = await classifier.ClassifyAsync(email, ct);
        var tag = result.Important ? "IMPORTANT" : "ignore  ";
        Console.WriteLine($"  [{tag}] {email.Subject}  ({result.Category}) - {result.Reason}");

        if (result.Important)
        {
            await notifier.NotifyAsync(email, result, ct);
            Console.WriteLine("    -> notification WhatsApp envoyee.");
        }

        handledUids.Add(email.Uid);
    }

    // Anti-doublon : on marque d'un libelle Gmail tout ce qui vient d'etre traite
    // (important ou non) pour ne pas le reanalyser a la prochaine passe.
    await reader.AddNotifiedLabelsAsync(handledUids, ct);
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
