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
        // L'endpoint compatible OpenAI de Gemini enveloppe parfois la reponse -- et surtout les
        // ERREURS, quota 429 compris -- dans un tableau JSON renvoye avec HTTP 200. On deballe,
        // et un champ "error" est traite comme une vraie erreur HTTP.
        var root = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0
            ? doc.RootElement[0]
            : doc.RootElement;
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
            var msg = error.TryGetProperty("message", out var m) ? m.GetString() ?? body : body;
            throw new LlmException($"{label} {code} : {Truncate(msg)}",
                fatal: code is 400 or 401 or 403 or 404,
                quota: code == 429);
        }

        return root
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }

    /// <summary>
    /// Envoie la requete avec retry sur les pannes serveur (5xx) et sur les 429 de limite par
    /// MINUTE (le fournisseur annonce alors un delai court : on attend et on retente). Un 429
    /// sans delai court = quota JOURNALIER : on laisse remonter pour que la cascade bascule.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(object payload, CancellationToken ct)
    {
        const int maxAttempts = 3;
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

            if (resp.IsSuccessStatusCode || attempt >= maxAttempts)
                return resp;

            var status = (int)resp.StatusCode;
            if (status == 429)
            {
                var wait = ParseRetryDelay(resp, await resp.Content.ReadAsStringAsync(ct));
                if (wait is null || wait > TimeSpan.FromSeconds(35)) return resp;
                resp.Dispose();
                Console.WriteLine($"    {label} 429 (limite par minute) : attente {wait.Value.TotalSeconds:0}s puis nouvel essai.");
                await Task.Delay(wait.Value, ct);
                continue;
            }

            if (status < 500) return resp;
            resp.Dispose();
            Console.WriteLine($"    {label} {status} : nouvelle tentative dans 500ms.");
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }

    /// <summary>
    /// Delai de reattente annonce par un 429 : en-tete Retry-After, ou mention
    /// "try again in 12.3s" dans le corps (format Groq). Null si aucun delai annonce.
    /// </summary>
    private static TimeSpan? ParseRetryDelay(HttpResponseMessage resp, string body)
    {
        if (resp.Headers.RetryAfter?.Delta is { } delta) return delta;
        var m = System.Text.RegularExpressions.Regex.Match(body, @"try again in ([0-9.]+)s",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success && double.TryParse(m.Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture, out var s)
            ? TimeSpan.FromSeconds(s + 1)
            : null;
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] : s;

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
