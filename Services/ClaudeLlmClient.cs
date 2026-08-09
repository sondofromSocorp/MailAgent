using System.Net.Http.Json;
using System.Text.Json;
using MailAgent.Configuration;

namespace MailAgent.Services;

/// <summary>
/// Fournisseur LLM Claude (Messages API Anthropic). Necessite une cle API et des credits.
/// Retry + backoff sur les erreurs transitoires (429/5xx) ; les erreurs de COMPTE
/// (credits epuisses, cle invalide) sont remontees comme fatales.
/// </summary>
public sealed class ClaudeLlmClient(AgentConfig config, HttpClient http) : ILlmClient
{
    public async Task<string> CompleteAsync(string system, string userContent, int maxTokens, CancellationToken ct = default)
    {
        var payload = new
        {
            model = config.Claude.Model,
            max_tokens = maxTokens,
            system,
            messages = new[]
            {
                new { role = "user", content = userContent }
            }
        };

        using var resp = await SendWithRetryAsync(payload, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var status = (int)resp.StatusCode;
            var detail = ExtractApiError(await resp.Content.ReadAsStringAsync(ct));
            // 400 (credits epuisses / quota / requete invalide) et 401/403 (cle invalide) sont des
            // erreurs de COMPTE, pas par-mail : inutile de retenter les autres mails de la passe.
            var fatal = status is 400 or 401 or 403;
            throw new LlmException($"API Claude {status} : {detail}", fatal);
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? "";
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

    /// <summary>Erreur transitoire meritant un retry : limite de debit (429) ou panne serveur (5xx).</summary>
    private static bool IsTransient(System.Net.HttpStatusCode code) =>
        (int)code == 429 || (int)code >= 500;

    /// <summary>Extrait le message d'erreur lisible du corps JSON de l'API (champ error.message).</summary>
    private static string ExtractApiError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err)
                && err.TryGetProperty("message", out var msg))
                return msg.GetString() ?? body;
        }
        catch (JsonException) { /* corps non JSON : on renvoie le brut (tronque) */ }
        return body.Length > 300 ? body[..300] : body;
    }
}
