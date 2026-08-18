using System.Net.Http.Json;
using System.Text.Json;

namespace MailAgent.Services;

/// <summary>
/// Fournisseur LLM generique parlant l'API "chat completions" d'OpenAI, exposee telle quelle
/// par les tiers GRATUITS : GitHub Models (GPT), Groq (Llama) et Gemini (endpoint compatible).
/// 429 = quota du tier gratuit atteint (il se reinitialise seul) : erreur non fatale marquee
/// Quota, que <see cref="FallbackLlmClient"/> intercepte pour passer au fournisseur suivant.
/// 401/403 (cle invalide) et 404 (modele inconnu) = fatals pour CE fournisseur.
/// </summary>
public sealed class OpenAiCompatLlmClient(string label, string baseUrl, string apiKey, string model, HttpClient http) : ILlmClient
{
    public async Task<string> CompleteAsync(string system, string userContent, int maxTokens, CancellationToken ct = default)
    {
        var payload = new
        {
            model,
            max_tokens = maxTokens,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = userContent }
            }
        };

        using var resp = await SendWithRetryAsync(payload, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var status = (int)resp.StatusCode;
            throw new LlmException($"{label} {status} : {ExtractApiError(body)}",
                fatal: status is 400 or 401 or 403 or 404,
                quota: status == 429);
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }

    /// <summary>
    /// Envoie la requete avec un retry court sur les pannes serveur (5xx) uniquement. Pas de
    /// retry sur 429 : sur un tier gratuit c'est souvent le quota JOURNALIER, autant basculer
    /// tout de suite sur le fournisseur suivant de la cascade.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(object payload, CancellationToken ct)
    {
        const int maxAttempts = 2;
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/chat/completions");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            req.Content = JsonContent.Create(payload);

            HttpResponseMessage resp;
            try
            {
                resp = await http.SendAsync(req, ct);
            }
            catch (HttpRequestException ex)
            {
                // Injoignable : non fatal, la cascade tentera le fournisseur suivant.
                throw new LlmException($"{label} injoignable : {ex.Message}", fatal: false);
            }

            if (resp.IsSuccessStatusCode || attempt >= maxAttempts || (int)resp.StatusCode < 500)
                return resp;

            var status = (int)resp.StatusCode;
            resp.Dispose();
            Console.WriteLine($"    {label} {status} : nouvelle tentative dans 500ms.");
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }

    /// <summary>Extrait le message d'erreur lisible du corps JSON (champ error.message), sinon le brut tronque.</summary>
    private static string ExtractApiError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var msg))
                    return msg.GetString() ?? body;
                if (err.ValueKind == JsonValueKind.String)
                    return err.GetString() ?? body;
            }
        }
        catch (JsonException) { /* corps non JSON : on renvoie le brut (tronque) */ }
        return body.Length > 300 ? body[..300] : body;
    }
}
