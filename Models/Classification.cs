namespace MailAgent.Models;

/// <summary>Resultat du tri d'un email par le modele.</summary>
public sealed record Classification(
    bool Important,
    bool Declutter,
    string Category,
    string Reason);
