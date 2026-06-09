using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MailAgent.Configuration;
using MailAgent.Models;

namespace MailAgent.Services;

/// <summary>Trie un email via l'API Claude (Messages API, modele Haiku par defaut).</summary>
public sealed class EmailClassifier(AgentConfig config, HttpClient http)
{
    // Prompt systeme = base + liste des priorites personnelles (Nayeli, etc.), figee a la construction.
    private readonly string _systemPrompt = SystemPrompt + BuildPrioritySection(config);

    private const string SystemPrompt =
        """
        Tu es un assistant qui trie les emails entrants. Pour chaque email, decide TROIS choses :

        1. action_required : true UNIQUEMENT si l'utilisateur doit FAIRE quelque chose :
           repondre a un message, confirmer une presence/disponibilite, payer une facture a
           echeance, agir avant une date limite, reagir a une alerte de securite/connexion suspecte.
           false pour les emails purement informatifs (confirmations/recus a conserver,
           recapitulatifs, accuses de reception, notifications automatiques).

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

        Reponds UNIQUEMENT avec un objet JSON valide, sans aucun texte ni balise autour, au format exact :
        {"action_required": true|false, "action": "phrase ou ''", "priority": true|false, "folder": "Factures|Banque|Immobilier|ReseauxSociaux|Pub|Communication|ASupprimer|", "source": "Bouygues|...|", "reason": "phrase courte en francais"}
        """;

    public async Task<Classification> ClassifyAsync(EmailItem email, CancellationToken ct = default)
    {
        var userContent =
            $"De : {email.From}\nObjet : {email.Subject}\n\nContenu :\n{email.BodyPreview}";

        var payload = new
        {
            model = config.Claude.Model,
            max_tokens = 300,
            system = _systemPrompt,
            messages = new[]
            {
                new { role = "user", content = userContent }
            }
        };

        using var resp = await SendWithRetryAsync(payload, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var text = doc.RootElement
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? "{}";

        text = ExtractJson(text);

        try
        {
            var dto = JsonSerializer.Deserialize<ClassificationDto>(text,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return new Classification(
                dto?.ActionRequired ?? false,
                dto?.Action?.Trim() ?? "",
                dto?.Priority ?? false,
                dto?.Folder?.Trim() ?? "",
                NormalizeSource(dto?.Source),
                dto?.Reason ?? "");
        }
        catch (JsonException)
        {
            // Reponse non parsable : on garde le mail en boite, sans action, par securite.
            return new Classification(false, "", false, "", "", "Reponse du modele non parsable.");
        }
    }

    /// <summary>
    /// Envoie la requete avec retry + backoff exponentiel sur les erreurs transitoires
    /// (429 / 5xx). Recree la requete a chaque tentative (HttpRequestMessage non reutilisable).
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(object payload, CancellationToken ct)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, config.Claude.ApiBaseUrl);
            req.Headers.Add("x-api-key", config.AnthropicApiKey);
            req.Headers.Add("anthropic-version", config.Claude.AnthropicVersion);
            req.Content = JsonContent.Create(payload);

            var resp = await http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode || attempt >= maxAttempts || !IsTransient(resp.StatusCode))
                return resp;

            var status = (int)resp.StatusCode;
            resp.Dispose();
            var delay = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1)); // 500ms, 1s
            Console.WriteLine($"    API Claude {status} : nouvelle tentative {attempt + 1}/{maxAttempts} dans {delay.TotalMilliseconds:0}ms.");
            await Task.Delay(delay, ct);
        }
    }

    /// <summary>Erreur transitoire merita un retry : limite de debit (429) ou panne serveur (5xx).</summary>
    private static bool IsTransient(System.Net.HttpStatusCode code) =>
        (int)code == 429 || (int)code >= 500;

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

    /// <summary>Construit la section "priorites personnelles" du prompt a partir de la config.</summary>
    private static string BuildPrioritySection(AgentConfig config)
    {
        var topics = config.Classifier.PriorityTopics;
        var senders = config.Classifier.PrioritySenders;
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

    private sealed record ClassificationDto(bool ActionRequired, string? Action, bool Priority, string? Folder, string? Source, string? Reason);
}
