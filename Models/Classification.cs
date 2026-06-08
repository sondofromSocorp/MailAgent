namespace MailAgent.Models;

/// <summary>Resultat du tri d'un email par le modele.</summary>
public sealed record Classification(
    bool ActionRequired,
    string Folder,
    string Reason);
