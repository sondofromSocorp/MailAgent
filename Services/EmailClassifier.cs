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
        Tu es un assistant qui trie les emails entrants.
        Pour chaque email, decide s'il est IMPORTANT (necessite une attention rapide de l'utilisateur) ou non.

        Sont IMPORTANTS : messages personnels directs, urgences, factures/paiements a echeance,
        rendez-vous, demandes professionnelles necessitant une reponse, alertes securite/connexion suspecte.

        Ne sont PAS importants : newsletters, promotions, notifications automatiques, spam, marketing no-reply.

        Reponds UNIQUEMENT avec un objet JSON valide, sans aucun texte ni balise autour, au format exact :
        {"important": true|false, "category": "string courte", "reason": "phrase courte en francais"}
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
                dto?.Important ?? false,
                string.IsNullOrWhiteSpace(dto?.Category) ? "inconnu" : dto!.Category!,
                dto?.Reason ?? "");
        }
        catch (JsonException)
        {
            // Reponse non parsable : on traite comme non important par securite.
            return new Classification(false, "parse_error", "Reponse du modele non parsable.");
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

    private sealed record ClassificationDto(bool Important, string? Category, string? Reason);
}
