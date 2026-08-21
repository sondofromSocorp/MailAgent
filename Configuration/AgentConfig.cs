namespace MailAgent.Configuration;

/// <summary>
/// Configuration de l'agent. La partie non-sensible vient de appsettings.json,
/// les secrets viennent des variables d'environnement / user-secrets (voir Program.cs).
/// </summary>
public sealed class AgentConfig
{
    /// <summary>
    /// Boites mail surveillees, chacune avec SES identifiants et SES criteres de tri.
    /// Liste vide = compte historique unique construit depuis les sections globales
    /// Imap/Classifier et les secrets IMAP_USER/IMAP_PASS.
    /// </summary>
    public AccountConfig[] Accounts { get; init; } = [];

    public ImapConfig Imap { get; init; } = new();
    public LlmConfig Llm { get; init; } = new();
    public ClaudeConfig Claude { get; init; } = new();
    public OllamaConfig Ollama { get; init; } = new();
    public FreeLlmConfig Free { get; init; } = new();
    public TelegramConfig Telegram { get; init; } = new();
    public SmtpConfig Smtp { get; init; } = new();
    public GoogleCalendarConfig Calendar { get; init; } = new();
    public RuntimeConfig Agent { get; init; } = new();
    public ClassifierConfig Classifier { get; init; } = new();

    // --- Secrets (jamais dans appsettings.json) ---
    public string ImapUser { get; set; } = "";
    public string ImapPassword { get; set; } = "";
    public string AnthropicApiKey { get; set; } = "";
}

/// <summary>
/// Une boite mail surveillee : identifiants (via variables d'environnement dediees) + criteres
/// de tri propres (dossiers, priorites, expediteurs bloques, consignes libres). Une boite
/// declaree sans secrets est simplement sautee : on peut ajouter la config d'avance et creer
/// les secrets plus tard.
/// </summary>
public sealed class AccountConfig
{
    /// <summary>Nom court affiche dans les logs et en prefixe des notifs (ex. "perso", "pro").</summary>
    public string Name { get; init; } = "";

    /// <summary>Variable d'environnement contenant l'adresse mail (ex. "IMAP_USER_PRO").</summary>
    public string UserEnv { get; init; } = "";

    /// <summary>Variable d'environnement contenant le mot de passe d'application.</summary>
    public string PassEnv { get; init; } = "";

    /// <summary>Serveur et criteres de classement de CETTE boite (valeurs standard par defaut).</summary>
    public ImapConfig Imap { get; init; } = new();

    /// <summary>Priorites / expediteurs bloques / consignes propres a CETTE boite.</summary>
    public ClassifierConfig Classifier { get; init; } = new();

    // --- Injectes depuis l'environnement au demarrage (jamais dans appsettings.json) ---
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class ImapConfig
{
    public string Host { get; init; } = "imap.gmail.com";
    public int Port { get; init; } = 993;

    /// <summary>Nombre max de mails traites par passe (cout / duree).</summary>
    public int MaxPerPass { get; init; } = 50;

    /// <summary>Ne traite que les mails des N derniers jours (0 = pas de limite).</summary>
    public int MaxAgeDays { get; init; } = 30;

    /// <summary>
    /// Marqueur IMAP (keyword standard) pose sur les mails deja traites (anti-doublon).
    /// Portable sur tout serveur IMAP, pas seulement Gmail.
    /// </summary>
    public string NotifiedKeyword { get; init; } = "MailAgentNotified";

    /// <summary>
    /// Dossier IMAP (boite principale) ou est persistee la liste des expediteurs bloques
    /// depuis Telegram (« bloque X »). Voir BlockListStore.
    /// </summary>
    public string BlocklistFolder { get; init; } = "MailAgentBlocklist";

    /// <summary>Dossiers (natures) de classement autorises (le modele choisit parmi eux ; sinon le mail reste en boite).</summary>
    public string[] Folders { get; init; } =
        ["Factures", "Banque", "Immobilier", "ReseauxSociaux", "Pub", "Communication", "ASupprimer"];

    /// <summary>
    /// Natures pour lesquelles on cree un sous-dossier par emetteur (ex. Factures/Bouygues).
    /// Les autres natures sont rangees a plat. Permet de retrouver une facture par emetteur
    /// sans multiplier les dossiers partout.
    /// </summary>
    public string[] SubfolderBySource { get; init; } = ["Factures", "Banque"];

    /// <summary>
    /// Marque comme LU les mails ranges dans un dossier (reduit le compteur de non-lus).
    /// N'affecte pas les mails gardes en boite (perso / action requise), qui restent non-lus.
    /// </summary>
    public bool MarkMovedAsRead { get; init; } = true;
}

public sealed class ClassifierConfig
{
    /// <summary>
    /// Personnes / sujets toujours prioritaires (ex. prenom d'un enfant). Un mail les mentionnant
    /// est garde en boite et notifie, meme purement informatif.
    /// </summary>
    public string[] PriorityTopics { get; init; } = [];

    /// <summary>
    /// Expediteurs toujours prioritaires (adresses email ou fragments, ex. l'ecole, un proche).
    /// Meme traitement que les sujets prioritaires.
    /// </summary>
    public string[] PrioritySenders { get; init; } = [];

    /// <summary>
    /// Expediteurs auto-supprimes (fragments d'adresse, ex. "news.saint-maclou.com"). Tout mail
    /// dont l'expediteur correspond part DIRECTEMENT a la corbeille, sans analyse ni notification.
    /// La corbeille Gmail est recuperable 30 jours.
    /// </summary>
    public string[] BlockedSenders { get; init; } = [];

    /// <summary>
    /// Consignes de tri LIBRES propres a la boite, ajoutees telles quelles au prompt du modele
    /// (ex. "Boite professionnelle : tout mail d'un client est prioritaire."). Permet des
    /// criteres differents par boite sans toucher au code.
    /// </summary>
    public string ExtraInstructions { get; init; } = "";
}

public sealed class LlmConfig
{
    /// <summary>
    /// Fournisseur du modele : "claude" (API Anthropic, cloud, payant), "ollama" (modele local
    /// via Ollama, gratuit, sans cle API) ou "free" (cascade de tiers gratuits : GitHub Models,
    /// Groq, Gemini -- bascule automatique quand un quota est epuise). Voir ILlmClient.
    /// </summary>
    public string Provider { get; init; } = "claude";
}

/// <summary>Cascade de fournisseurs gratuits (Llm:Provider = "free"), essayes dans l'ordre.</summary>
public sealed class FreeLlmConfig
{
    public FreeLlmProviderConfig[] Providers { get; init; } = [];
}

/// <summary>
/// Un fournisseur gratuit parlant l'API "chat completions" d'OpenAI (GitHub Models, Groq,
/// Gemini via son endpoint compatible...). Sans cle dans l'environnement (variable KeyEnv),
/// le fournisseur est simplement saute : on peut declarer la cascade complete d'avance et
/// creer les cles au fur et a mesure.
/// </summary>
public sealed class FreeLlmProviderConfig
{
    /// <summary>Nom court affiche dans les logs (ex. "github", "groq", "gemini").</summary>
    public string Name { get; init; } = "";

    /// <summary>Racine de l'API, SANS le /chat/completions final.</summary>
    public string BaseUrl { get; init; } = "";

    /// <summary>Identifiant du modele chez ce fournisseur.</summary>
    public string Model { get; init; } = "";

    /// <summary>Nom de la variable d'environnement contenant la cle API.</summary>
    public string KeyEnv { get; init; } = "";

    /// <summary>Cle API, injectee depuis l'environnement au demarrage (jamais dans appsettings.json).</summary>
    public string ApiKey { get; set; } = "";
}

public sealed class OllamaConfig
{
    /// <summary>URL du serveur Ollama (en general local a la machine : pas d'authentification).</summary>
    public string BaseUrl { get; init; } = "http://localhost:11434";

    /// <summary>Modele a utiliser (doit avoir ete installe via "ollama pull &lt;model&gt;").</summary>
    public string Model { get; init; } = "qwen2.5:7b";
}

public sealed class ClaudeConfig
{
    public string Model { get; init; } = "claude-haiku-4-5-20251001";
    public string ApiBaseUrl { get; init; } = "https://api.anthropic.com/v1/messages";
    public string AnthropicVersion { get; init; } = "2023-06-01";
}

public sealed class TelegramConfig
{
    /// <summary>Token du bot, fourni par @BotFather. Injecte via le secret TELEGRAM_BOT_TOKEN.</summary>
    public string BotToken { get; set; } = "";

    /// <summary>Identifiant du chat destinataire (ton compte). Injecte via le secret TELEGRAM_CHAT_ID.</summary>
    public string ChatId { get; set; } = "";
}

public sealed class GoogleCalendarConfig
{
    /// <summary>Active l'ajout automatique d'evenements a l'agenda. Inactif tant que les secrets manquent.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Fuseau des evenements crees.</summary>
    public string TimeZone { get; init; } = "Europe/Paris";

    // --- Secrets OAuth Google (injectes via variables d'environnement) ---
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RefreshToken { get; set; } = "";
}

public sealed class SmtpConfig
{
    /// <summary>Serveur SMTP d'envoi (Gmail par defaut). STARTTLS sur le port 587.</summary>
    public string Host { get; init; } = "smtp.gmail.com";
    public int Port { get; init; } = 587;

    /// <summary>Dossier IMAP ou est stocke le brouillon en attente de validation (un seul a la fois).</summary>
    public string PendingFolder { get; init; } = "MailAgentPending";
}

public sealed class RuntimeConfig
{
    public int PollIntervalSeconds { get; init; } = 120;

    /// <summary>true = une seule passe puis sortie ; false = boucle continue.</summary>
    public bool RunOnce { get; init; } = true;

    /// <summary>
    /// En mode une passe (RunOnce=true), enchaine les fournees de MaxPerPass tant qu'il reste
    /// des mails a traiter, jusqu'a ce budget de temps (secondes). Vide le backlog plus vite tout
    /// en restant sous le timeout du CI (10 min sur GitHub Actions). 0 = une seule fournee.
    /// </summary>
    public int MaxRunSeconds { get; init; } = 480;

    /// <summary>
    /// true = mode test : l'agent affiche ce qu'il ferait (notifier / ranger) sans rien modifier.
    /// false = l'agent agit reellement (notifications + rangement des mails inutiles).
    /// </summary>
    public bool DryRun { get; init; } = true;

    /// <summary>Heures silencieuses : aucune notification durant cette plage. Le rangement n'est pas affecte.</summary>
    public bool QuietHoursEnabled { get; init; } = true;

    /// <summary>Fuseau pour les heures silencieuses (IANA ou Windows). Defaut : heure de Paris.</summary>
    public string TimeZone { get; init; } = "Europe/Paris";

    /// <summary>Debut de la plage silencieuse, format HH:mm.</summary>
    public string QuietStart { get; init; } = "22:00";

    /// <summary>Fin de la plage silencieuse, format HH:mm.</summary>
    public string QuietEnd { get; init; } = "06:30";
}
