namespace MailAgent.Configuration;

/// <summary>
/// Configuration de l'agent. La partie non-sensible vient de appsettings.json,
/// les secrets viennent des variables d'environnement / user-secrets (voir Program.cs).
/// </summary>
public sealed class AgentConfig
{
    public ImapConfig Imap { get; init; } = new();
    public ClaudeConfig Claude { get; init; } = new();
    public WhatsAppConfig WhatsApp { get; init; } = new();
    public TelegramConfig Telegram { get; init; } = new();
    public RuntimeConfig Agent { get; init; } = new();
    public ClassifierConfig Classifier { get; init; } = new();

    // --- Secrets (jamais dans appsettings.json) ---
    public string ImapUser { get; set; } = "";
    public string ImapPassword { get; set; } = "";
    public string AnthropicApiKey { get; set; } = "";
    public string TwilioAccountSid { get; set; } = "";
    public string TwilioAuthToken { get; set; } = "";
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
}

public sealed class ClaudeConfig
{
    public string Model { get; init; } = "claude-haiku-4-5-20251001";
    public string ApiBaseUrl { get; init; } = "https://api.anthropic.com/v1/messages";
    public string AnthropicVersion { get; init; } = "2023-06-01";
}

public sealed class WhatsAppConfig
{
    /// <summary>Numero Twilio (sandbox par defaut). Format : whatsapp:+14155238886</summary>
    public string FromNumber { get; init; } = "whatsapp:+14155238886";

    /// <summary>Ton numero WhatsApp. Format : whatsapp:+33XXXXXXXXX</summary>
    public string ToNumber { get; init; } = "";
}

public sealed class TelegramConfig
{
    /// <summary>Token du bot, fourni par @BotFather. Injecte via le secret TELEGRAM_BOT_TOKEN.</summary>
    public string BotToken { get; set; } = "";

    /// <summary>Identifiant du chat destinataire (ton compte). Injecte via le secret TELEGRAM_CHAT_ID.</summary>
    public string ChatId { get; set; } = "";
}

public sealed class RuntimeConfig
{
    public int PollIntervalSeconds { get; init; } = 120;

    /// <summary>true = une seule passe puis sortie ; false = boucle continue.</summary>
    public bool RunOnce { get; init; } = true;

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
