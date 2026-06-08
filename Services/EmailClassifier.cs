using System.Net.Http.Json;
using System.Text.Json;
using MailAgent.Configuration;
using MailAgent.Models;

namespace MailAgent.Services;

/// <summary>Trie un email via l'API Claude (Messages API, modele Haiku par defaut).</summary>
public sealed class EmailClassifier(AgentConfig config, HttpClient http)
{
    private const string SystemPrompt =
        """
        Tu es un assistant qui trie les emails entrants. Pour chaque email, decide DEUX choses :

        1. action_required : true UNIQUEMENT si l'utilisateur doit FAIRE quelque chose :
           repondre a un message, confirmer une presence/disponibilite, payer une facture a
           echeance, agir avant une date limite, reagir a une alerte de securite/connexion suspecte.
           false pour les emails purement informatifs (confirmations/recus a conserver,
           recapitulatifs, accuses de reception, notifications automatiques).

        2. folder : le dossier de classement. Choisis EXACTEMENT une de ces valeurs :
           - "Pub" : publicite, marketing, newsletter commerciale, promotion/reduction, jeu-concours,
             applications de rencontre, reseaux sociaux, alertes immobilieres commerciales, no-reply marketing.
           - "Factures" : factures, recus de paiement, documents comptables ou contractuels avec un montant.
           - "Communication" : communications de service non commerciales -- operateurs (ex. Bouygues,
             telecom), abonnements, confirmations administratives, recapitulatifs, notifications de compte.
           - "" (chaine vide) : a GARDER dans la boite de reception -- messages personnels,
             mails demandant une action ou une reponse, rendez-vous, et tout cas ambigu.

           En cas de doute, prefere "" (garder en boite). Ne mets JAMAIS un mail personnel
           ou demandant une reponse dans "Pub".

        Reponds UNIQUEMENT avec un objet JSON valide, sans aucun texte ni balise autour, au format exact :
        {"action_required": true|false, "folder": "Pub|Factures|Communication|", "reason": "phrase courte en francais"}
        """;

    public async Task<Classification> ClassifyAsync(EmailItem email, CancellationToken ct = default)
    {
        var userContent =
            $"De : {email.From}\nObjet : {email.Subject}\n\nContenu :\n{email.BodyPreview}";

        var payload = new
        {
            model = config.Claude.Model,
            max_tokens = 200,
            system = SystemPrompt,
            messages = new[]
            {
                new { role = "user", content = userContent }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, config.Claude.ApiBaseUrl);
        req.Headers.Add("x-api-key", config.AnthropicApiKey);
        req.Headers.Add("anthropic-version", config.Claude.AnthropicVersion);
        req.Content = JsonContent.Create(payload);

        using var resp = await http.SendAsync(req, ct);
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
                dto?.Folder?.Trim() ?? "",
                dto?.Reason ?? "");
        }
        catch (JsonException)
        {
            // Reponse non parsable : on garde le mail en boite, sans action, par securite.
            return new Classification(false, "", "Reponse du modele non parsable.");
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

    private sealed record ClassificationDto(bool ActionRequired, string? Folder, string? Reason);
}
