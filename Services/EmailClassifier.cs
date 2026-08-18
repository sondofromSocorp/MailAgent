using System.Globalization;
using System.Text;
using System.Text.Json;
using MailAgent.Configuration;
using MailAgent.Models;

namespace MailAgent.Services;

/// <summary>Trie un email via le LLM configure (voir ILlmClient), selon les criteres de SA boite.</summary>
public sealed class EmailClassifier(AccountConfig account, ILlmClient llm)
{
    // Prompt systeme = base + criteres de la boite (dossiers autorises, consignes libres,
    // priorites personnelles), fige a la construction.
    private readonly string _systemPrompt = SystemPrompt + BuildAccountSection(account) + BuildPrioritySection(account.Classifier);

    private const string SystemPrompt =
        """
        Tu es un assistant qui trie les emails entrants. Pour chaque email, decide TROIS choses :

        1. action_required : true DES QUE l'utilisateur doit FAIRE quelque chose. Exemples :
           - repondre a un message personnel ou professionnel ;
           - confirmer une presence/disponibilite, donner suite a une convocation, une reunion
             ou une assemblee generale (AG) ;
           - payer une facture a echeance, agir avant une date limite A VENIR ;
           - reagir a une alerte de securite/connexion suspecte ;
           - remplir un formulaire demande, finaliser une reservation/commande en cours ;
           - toute demande emanant de l'ECOLE ou concernant un ENFANT (inscription, sortie, reunion).
           Mets false pour le purement informatif : confirmations/recus a conserver, recapitulatifs,
           accuses de reception, suivis de colis, rappels d'agenda automatiques, publicite/marketing,
           et les echeances DEJA PASSEES.
           En cas de DOUTE sur la presence d'une action a venir, prefere true : mieux vaut notifier
           a tort qu'ignorer un mail important. (Mais ne notifie jamais une simple publicite.)

        2. action : si action_required=true, resume en une phrase courte l'action CONCRETE a faire
           et son echeance s'il y en a une (ex. "Voter avant le 25 juin ou donner pouvoir avant le 28",
           "Payer la facture avant le 15", "Repondre a Marie"). Mets "" si action_required=false.
           S'il y a une date/heure d'evenement (rendez-vous, convocation, AG), mentionne-la dans l'action.

        3. folder : la NATURE du mail. Choisis EXACTEMENT une de ces valeurs :
           - "Factures" : factures, recus de paiement, documents comptables ou contractuels avec un montant.
           - "Banque" : releves, operations, communications d'une banque ou d'un service de paiement.
           - "Immobilier" : annonces immobilieres et alertes de recherche (SeLoger, Leboncoin immo,
             agences), visites, locations -- hors transactions personnelles en cours.
           - "ReseauxSociaux" : notifications de reseaux sociaux (Facebook, LinkedIn, X, Instagram...).
           - "Pub" : publicite, marketing, newsletter commerciale, promotion/reduction, jeu-concours, no-reply marketing.
           - "Communication" : communications de service non commerciales -- operateurs, abonnements,
             confirmations administratives, recapitulatifs, notifications de compte.
           - "ASupprimer" : indesirables manifestes -- sites/applications de rencontre, spam evident,
             arnaques. JAMAIS un mail personnel, une facture, ou un mail demandant une reponse.
           - "" (chaine vide) : a GARDER dans la boite de reception -- messages personnels,
             mails demandant une action ou une reponse, rendez-vous, et tout cas ambigu.

           REGLE IMPORTANTE : distingue la NATURE de l'EMETTEUR. Un meme expediteur peut envoyer
           des mails de natures differentes. Exemple : une FACTURE Bouygues va dans "Factures"
           (source "Bouygues"), mais une PROMOTION Bouygues va dans "Pub". Ne te fie pas qu'au
           nom de l'expediteur : regarde le contenu.

           En cas de doute, prefere "" (garder en boite). Ne mets JAMAIS un mail personnel
           ou demandant une reponse dans "Pub" ou "ASupprimer".

        4. source : le nom court et normalise de l'emetteur/marque (ex. "Bouygues", "EDF", "SeLoger",
           "Free", "Amazon"), SANS accents ni espaces. Sert a creer un sous-dossier de classement.
           Mets "" si l'emetteur n'est pas identifiable ou non pertinent.

        5. priority : true si le mail concerne une PERSONNE ou un SUJET prioritaire, ou vient d'un
           EXPEDITEUR prioritaire (listes ci-dessous). Un mail prioritaire est TOUJOURS important :
           il reste en boite (folder="") et l'utilisateur veut etre notifie MEME s'il est purement
           informatif (ex. une absence scolaire). Sinon false.

        6. notif : UNIQUEMENT si action_required=true OU priority=true, redige un message de
           notification en LANGAGE NATUREL (1 a 2 phrases courtes), comme un assistant qui previent
           l'utilisateur : qui ecrit, de quoi il s'agit, et ce qu'il faut faire / la date clef.
           Exemple : "Le Conseil Syndical t'envoie la convocation a l'AG de copropriete : c'est le
           30 juin a 18h, pense a voter avant le 25." Pas de "De:/Objet:", un vrai message humain.
           Mets "" si le mail n'est ni action_required ni priority.

        7. event : si le mail contient un EVENEMENT DATE concret a noter dans un agenda (rendez-vous,
           reunion, convocation, assemblee generale, visite, rendez-vous medical, reservation avec
           date et heure precises), extrais-le en objet :
           {"title": "intitule court", "start": "AAAA-MM-JJTHH:MM:SS", "end": "... ou ''", "location": "... ou ''"}.
           start au format ISO 8601 avec l'heure si elle est connue, sinon juste "AAAA-MM-JJ".
           N'INVENTE RIEN : uniquement si une date explicite figure dans le mail. Ne cree PAS
           d'evenement pour une simple date marketing/promo ("offre jusqu'au ..."). Sinon : event = null.

        Reponds UNIQUEMENT avec un objet JSON valide, sans aucun texte ni balise autour, au format exact :
        {"action_required": true|false, "action": "phrase ou ''", "priority": true|false, "folder": "Factures|Banque|Immobilier|ReseauxSociaux|Pub|Communication|ASupprimer|", "source": "Bouygues|...|", "reason": "phrase courte en francais", "notif": "message naturel ou ''", "event": {"title":"...","start":"...","end":"...","location":"..."} ou null}
        """;

    public async Task<Classification> ClassifyAsync(EmailItem email, CancellationToken ct = default)
    {
        var userContent =
            $"De : {email.From}\nObjet : {email.Subject}\n\nContenu :\n{email.BodyPreview}";

        // 1000 tokens : les modeles "a reflexion" (gpt-oss, gemini flash) consomment le budget
        // en raisonnement interne AVANT d'emettre le JSON ; 300 suffisait a Claude mais tronquait.
        var text = ExtractJson(await llm.CompleteAsync(_systemPrompt, userContent, maxTokens: 1000, ct));

        try
        {
            var dto = JsonSerializer.Deserialize<ClassificationDto>(text,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            EventInfo? evt = null;
            if (dto?.Event is { } e && !string.IsNullOrWhiteSpace(e.Title) && !string.IsNullOrWhiteSpace(e.Start))
                evt = new EventInfo(e.Title!.Trim(), e.Start!.Trim(), e.End?.Trim() ?? "", e.Location?.Trim() ?? "");

            return new Classification(
                dto?.ActionRequired ?? false,
                dto?.Action?.Trim() ?? "",
                dto?.Priority ?? false,
                dto?.Folder?.Trim() ?? "",
                NormalizeSource(dto?.Source),
                dto?.Reason ?? "",
                dto?.Notif?.Trim() ?? "",
                evt);
        }
        catch (JsonException)
        {
            // Reponse non parsable : on garde le mail en boite, sans action, par securite.
            return new Classification(false, "", false, "", "", "Reponse du modele non parsable.", "", null);
        }
    }

    /// <summary>Retire d'eventuels backticks ``` autour du JSON.</summary>
    private static string ExtractJson(string s)
    {
        s = s.Trim();
        var first = s.IndexOf('{');
        var last = s.LastIndexOf('}');
        return first >= 0 && last > first ? s[first..(last + 1)] : s;
    }

    /// <summary>
    /// Normalise le nom de source pour servir de nom de sous-dossier IMAP sur :
    /// retire accents, espaces et caracteres reserves, garde lettres/chiffres/-/_ (max 40 car.).
    /// </summary>
    private static string NormalizeSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "";

        var decomposed = source.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue; // accents
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c is '-' or '_') sb.Append(c);
            // tout le reste (espace, '/', '.', etc.) est ignore
        }

        var cleaned = sb.ToString();
        return cleaned.Length > 40 ? cleaned[..40] : cleaned;
    }

    /// <summary>
    /// Section du prompt propre a la boite : dossiers de classement AUTORISES (la taxonomie de
    /// base du prompt decrit les dossiers standard ; cette liste fait foi) et consignes libres.
    /// </summary>
    private static string BuildAccountSection(AccountConfig account)
    {
        var sb = new StringBuilder();
        sb.Append("\n\nPour CETTE boite, les valeurs folder AUTORISEES sont exactement : ")
          .Append(string.Join(", ", account.Imap.Folders.Select(f => $"\"{f}\"")))
          .Append(" ou \"\" (garder en boite). N'utilise jamais une autre valeur.");
        if (!string.IsNullOrWhiteSpace(account.Classifier.ExtraInstructions))
            sb.Append("\n\n--- Consignes specifiques a cette boite ---\n")
              .Append(account.Classifier.ExtraInstructions.Trim());
        return sb.ToString();
    }

    /// <summary>Construit la section "priorites personnelles" du prompt a partir des criteres de la boite.</summary>
    private static string BuildPrioritySection(ClassifierConfig classifier)
    {
        var topics = classifier.PriorityTopics;
        var senders = classifier.PrioritySenders;
        var hasTopics = topics is { Length: > 0 };
        var hasSenders = senders is { Length: > 0 };

        if (!hasTopics && !hasSenders)
            return "\n\nAucune personne/sujet prioritaire defini : priority=false pour tous les mails.";

        var sb = new StringBuilder("\n\n--- Priorites personnelles ---");
        if (hasTopics)
            sb.Append("\nPersonnes/sujets prioritaires : ").Append(string.Join(", ", topics)).Append('.');
        if (hasSenders)
            sb.Append("\nExpediteurs prioritaires : ").Append(string.Join(", ", senders)).Append('.');
        sb.Append("\nTout mail concernant ces personnes/sujets, ou provenant de ces expediteurs, doit avoir priority=true.");
        return sb.ToString();
    }

    // "action_required" : PropertyNameCaseInsensitive ne gere PAS les underscores, sans cet
    // attribut la cle n'est jamais lue (et action_required vaudrait toujours false).
    private sealed record ClassificationDto(
        [property: System.Text.Json.Serialization.JsonPropertyName("action_required")] bool ActionRequired,
        string? Action, bool Priority, string? Folder, string? Source, string? Reason, string? Notif, EventDto? Event);

    private sealed record EventDto(string? Title, string? Start, string? End, string? Location);
}
