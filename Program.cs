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
// Cles des fournisseurs gratuits (Provider=free) : chaque entree de la cascade declare sa
// variable d'environnement (KeyEnv). Cle absente = fournisseur simplement saute.
foreach (var p in config.Free.Providers)
    p.ApiKey = configuration[p.KeyEnv] ?? "";

Validate(config);

// --- Composition des services ---
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

// Le LLM a son propre HttpClient : un modele local (Ollama sur petit VPS) peut mettre bien
// plus de 60s a generer une reponse, la ou les API cloud repondent en quelques secondes.
var llmProvider = config.Llm.Provider.Trim().ToLowerInvariant();
using var llmHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(llmProvider == "ollama" ? 300 : 60) };
var freeChain = config.Free.Providers.Where(p => !string.IsNullOrWhiteSpace(p.ApiKey)).ToArray();
ILlmClient llm = llmProvider switch
{
    "ollama" => new OllamaLlmClient(config, llmHttp),
    "free" => new FallbackLlmClient(
        [.. freeChain.Select(p => (p.Name, (ILlmClient)new OpenAiCompatLlmClient(p.Name, p.BaseUrl, p.ApiKey, p.Model, llmHttp)))]),
    _ => new ClaudeLlmClient(config, llmHttp),
};

// --- Boites mail : liste "Accounts" (multi-boites, criteres propres) ou compte historique ---
foreach (var a in config.Accounts)
{
    a.User = configuration[a.UserEnv] ?? "";
    a.Password = configuration[a.PassEnv] ?? "";
}
AccountConfig[] accounts;
if (config.Accounts.Length > 0)
{
    // Une boite declaree sans secrets est sautee (on peut preparer la config avant les cles).
    foreach (var a in config.Accounts.Where(x => x.User.Length == 0 || x.Password.Length == 0))
        Console.WriteLine($"[ATTENTION] Boite '{a.Name}' ignoree : secrets {a.UserEnv}/{a.PassEnv} absents.");
    accounts = config.Accounts.Where(a => a.User.Length > 0 && a.Password.Length > 0).ToArray();
    if (accounts.Length == 0)
    {
        Console.WriteLine("Aucune boite utilisable : tous les secrets IMAP declares sont absents.");
        Environment.Exit(1);
    }
}
else
{
    accounts = [new AccountConfig
    {
        Name = "", User = config.ImapUser, Password = config.ImapPassword,
        Imap = config.Imap, Classifier = config.Classifier,
    }];
}

// Un jeu de services par boite. Le prefixe [nom] sur les notifs n'apparait qu'en multi-boites.
var boxes = accounts.Select(a => new Mailbox(
    a,
    new EmailReader(a),
    new EmailClassifier(a, llm),
    new TelegramNotifier(config, http, accounts.Length > 1 && a.Name.Length > 0 ? $"[{a.Name}] " : ""))).ToArray();

var calendar = new GoogleCalendar(config, http);
// L'assistant conversationnel (lecture Telegram + reponses aux mails) reste lie a la boite
// PRINCIPALE (la premiere) : c'est elle qui sert d'expediteur SMTP et de contexte.
var sender = new EmailSender(config, accounts[0]);
var conversation = new TelegramConversation(config, accounts[0], http, llm, boxes[0].Reader, sender);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var llmLabel = llmProvider switch
{
    "ollama" => $"Ollama ({config.Ollama.Model} sur {config.Ollama.BaseUrl})",
    "free" => "cascade gratuite " + string.Join(" -> ", freeChain.Select(p => $"{p.Name}:{p.Model}")),
    _ => $"Claude ({config.Claude.Model})",
};
Console.WriteLine($"MailAgent demarre. Mode : {(config.Agent.RunOnce ? "une passe" : "boucle continue")}. LLM : {llmLabel}. "
    + $"Boite(s) : {string.Join(", ", accounts.Select(a => a.Name.Length > 0 ? a.Name : a.User))}.");

var runClock = Stopwatch.StartNew();
do
{
    var fullBatch = false;
    try
    {
        fullBatch = await RunOnceAsync(boxes, conversation, calendar, config, cts.Token);
    }
    catch (OperationCanceledException) { break; }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERREUR] {ex.Message}");
    }

    if (config.Agent.RunOnce)
    {
        // Vide le backlog : tant qu'une boite a rendu une fournee PLEINE (donc il reste
        // probablement des mails) et que le budget temps n'est pas atteint, on enchaine.
        var budgetReached = config.Agent.MaxRunSeconds > 0
            && runClock.Elapsed.TotalSeconds >= config.Agent.MaxRunSeconds;
        if (fullBatch && !budgetReached) continue;
        break;
    }

    Console.WriteLine($"Prochaine verification dans {config.Agent.PollIntervalSeconds}s... (Ctrl+C pour arreter)");
    try { await Task.Delay(TimeSpan.FromSeconds(config.Agent.PollIntervalSeconds), cts.Token); }
    catch (OperationCanceledException) { break; }
}
while (!cts.IsCancellationRequested);

Console.WriteLine("Agent arrete.");


// Traite toutes les boites, puis le volet conversationnel. Renvoie true si AU MOINS une boite
// a rendu une fournee pleine (il reste probablement du backlog a vider).
static async Task<bool> RunOnceAsync(
    IReadOnlyList<Mailbox> boxes, TelegramConversation conversation, GoogleCalendar calendar,
    AgentConfig config, CancellationToken ct)
{
    var dryRun = config.Agent.DryRun;

    // Heures silencieuses : pendant la plage de nuit, les notifications sont reportees.
    var quiet = IsQuietNow(config.Agent);
    if (quiet) Console.WriteLine("Heures silencieuses : notifications suspendues (rangement maintenu).");

    var fullBatch = false;
    var importantEmails = new List<MailAgent.Models.EmailItem>();       // contexte pour l'assistant Telegram

    foreach (var box in boxes)
    {
        var (account, reader, classifier, notifier) = box;
        var label = account.Name.Length > 0 ? $" [{account.Name}]" : "";
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}]{label} Lecture des mails a traiter..."
            + (dryRun ? "  [MODE TEST : aucune action]" : ""));
        var emails = await reader.GetToProcessAsync(ct);
        Console.WriteLine($"  {emails.Count} mail(s) a traiter.");
        fullBatch |= emails.Count >= account.Imap.MaxPerPass;

        var toKeep = new List<MailKit.UniqueId>(emails.Count);          // restent en boite (marquage anti-doublon)
        var toMove = new Dictionary<string, List<MailKit.UniqueId>>();  // dossier -> mails a classer
        var toTrash = new List<MailKit.UniqueId>();                     // expediteurs auto-supprimes -> corbeille

        foreach (var email in emails)
        {
        // Un mail en erreur (API, IMAP) ne doit pas arreter toute la passe : on le
        // journalise et on continue. N'etant ni marque ni deplace, il sera retente plus tard.
        try
        {
            // Expediteurs auto-supprimes : direct corbeille, sans analyse ni notification.
            if (IsBlocked(email.From, account.Classifier.BlockedSenders))
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
                : Array.IndexOf(account.Imap.Folders, result.Folder) >= 0 ? result.Folder : "";

            // Sous-dossier par emetteur seulement pour certaines natures (ex. Factures/Bouygues),
            // afin de retrouver une facture par emetteur sans multiplier les dossiers ailleurs.
            var folder = nature.Length > 0
                && result.Source.Length > 0
                && Array.IndexOf(account.Imap.SubfolderBySource, nature) >= 0
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
        catch (LlmException ex) when (ex.Fatal)
        {
            // Erreur d'infrastructure LLM (credits/cle Claude, serveur Ollama down ou modele
            // absent) : on previent UNE fois et on arrete la passe. Les mails non traites
            // seront repris a la prochaine execution. Exception : quotas des tiers gratuits
            // epuisses (Quota) -- ils se reinitialisent seuls, alerter toutes les 5 min serait
            // du spam ; on attend en silence.
            Console.WriteLine($"  [LLM BLOQUE   ] {ex.Message}");
            if (!dryRun && !ex.Quota)
            {
                try
                {
                    await notifier.SendTextAsync(
                        "⚠️ Tri des mails en pause : le modele a refuse la requete.\n"
                        + ex.Message
                        + "\nClaude : verifie les credits (console.anthropic.com) ou la cle. "
                        + "Ollama : verifie que le serveur tourne et que le modele est installe. "
                        + "Cascade gratuite : verifie les cles (github/groq/gemini) et les noms de modeles.", ct);
                }
                catch (Exception nex) { Console.WriteLine($"    (alerte Telegram non envoyee : {nex.Message})"); }
            }
            throw; // stoppe la passe en cours : inutile de tenter les autres mails
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [ERREUR       ] {email.Subject}  - {ex.Message}");
        }
    }

        if (dryRun) continue;

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
    }

    if (dryRun)
    {
        Console.WriteLine("Mode test : rien n'a ete modifie dans les boites.");
        return fullBatch;
    }

    // Volet conversationnel : lit les messages Telegram en attente et y repond (boite principale).
    await conversation.RunAsync(importantEmails, ct);

    return fullBatch;
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
    // Compte historique unique : requis seulement si aucune boite n'est declaree dans "Accounts"
    // (les boites declarees sont validees a la resolution : sans secrets, elles sont sautees).
    if (c.Accounts.Length == 0)
    {
        if (string.IsNullOrWhiteSpace(c.ImapUser)) missing.Add("IMAP_USER");
        if (string.IsNullOrWhiteSpace(c.ImapPassword)) missing.Add("IMAP_PASS");
    }
    // Cle(s) LLM selon le fournisseur : Ollama = aucune ; "free" = au moins une cle de la
    // cascade (les fournisseurs sans cle sont sautes) ; sinon Claude = cle Anthropic.
    var provider = c.Llm.Provider.Trim().ToLowerInvariant();
    if (provider == "free")
    {
        if (!c.Free.Providers.Any(p => !string.IsNullOrWhiteSpace(p.ApiKey)))
            missing.Add("au moins une cle de la cascade gratuite (GITHUB_MODELS_TOKEN, GROQ_API_KEY ou GEMINI_API_KEY)");
    }
    else if (provider != "ollama" && string.IsNullOrWhiteSpace(c.AnthropicApiKey)) missing.Add("ANTHROPIC_API_KEY");
    if (string.IsNullOrWhiteSpace(c.Telegram.BotToken)) missing.Add("TELEGRAM_BOT_TOKEN");
    if (string.IsNullOrWhiteSpace(c.Telegram.ChatId)) missing.Add("TELEGRAM_CHAT_ID");

    if (missing.Count == 0) return;

    Console.WriteLine("Configuration incomplete. Elements manquants :");
    foreach (var m in missing) Console.WriteLine($"  - {m}");
    Console.WriteLine("\nVoir le README pour le detail du parametrage.");
    Environment.Exit(1);
}

/// <summary>Jeu de services lie a une boite mail (comptes multiples, criteres propres).</summary>
sealed record Mailbox(AccountConfig Account, EmailReader Reader, EmailClassifier Classifier, TelegramNotifier Notifier);
