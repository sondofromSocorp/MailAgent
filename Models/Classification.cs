namespace MailAgent.Models;

/// <summary>Resultat du tri d'un email par le modele.</summary>
/// <param name="ActionRequired">L'utilisateur doit faire quelque chose (repondre, payer, confirmer...).</param>
/// <param name="Action">Si ActionRequired : resume de l'action concrete a faire et son echeance. Sinon "".</param>
/// <param name="Priority">Le mail concerne une personne/sujet prioritaire : a garder en boite et notifier meme si informatif.</param>
/// <param name="Folder">Nature du mail / dossier de 1er niveau (ex. Factures, Pub, ASupprimer) ou "" pour garder en boite.</param>
/// <param name="Source">Emetteur normalise (ex. Bouygues, EDF, SeLoger) ou "" si non pertinent. Sert de sous-dossier.</param>
/// <param name="Reason">Phrase courte expliquant le choix.</param>
/// <param name="Notif">Message de notification en langage naturel (1-2 phrases) si le mail est important ; sinon "".</param>
public sealed record Classification(
    bool ActionRequired,
    string Action,
    bool Priority,
    string Folder,
    string Source,
    string Reason,
    string Notif);
