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
    public RuntimeConfig Agent { get; init; } = new();

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
    public int MaxUnreadToProcess { get; init; } = 15;

    /// <summary>
    /// Marqueur IMAP (keyword standard) pose sur les mails deja traites (anti-doublon).
    /// Portable sur tout serveur IMAP, pas seulement Gmail.
    /// </summary>
    public string NotifiedKeyword { get; init; } = "MailAgentNotified";

    /// <summary>Dossier/libelle ou sont deplaces les mails juges inutiles (menage).</summary>
    public string SortFolder { get; init; } = "MailAgent";
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
}
