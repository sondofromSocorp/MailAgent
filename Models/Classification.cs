namespace MailAgent.Models;

/// <summary>Resultat du tri d'un email par le modele.</summary>
public sealed record Classification(
    bool Important,
    bool Declutter,
    bool NeedsReply,
    string Category,
    string Reason);
