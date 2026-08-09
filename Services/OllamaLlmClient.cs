using System.Net.Http.Json;
using System.Text.Json;
using MailAgent.Configuration;

namespace MailAgent.Services;

/// <summary>
/// Fournisseur LLM local via Ollama (endpoint /api/chat) : gratuit, sans cle API. Concu pour
/// tourner sur la meme machine que l'agent (VPS) -- BaseUrl http://localhost:11434 par defaut.
/// Serveur injoignable ou modele absent (pull manquant) = erreur fatale : tous les mails de la
/// passe echoueraient de la meme facon.
/// </summary>
public sealed class OllamaLlmClient(AgentConfig config, HttpClient http) : ILlmClient
{
    public async Task<string> CompleteAsync(string system, string userContent, int maxTokens, CancellationToken ct = default)
    {
        var payload = new
        {
            model = config.Ollama.Model,
            stream = false,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = userContent }
            },
            options = new { num_predict = maxTokens }
        };

        var url = $"{config.Ollama.BaseUrl.TrimEnd('/')}/api/chat";
        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsJsonAsync(url, payload, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException($"Serveur Ollama injoignable ({url}) : {ex.Message}", fatal: true);
        }

        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                // 404 = modele non installe ("ollama pull <model>" manquant), 400 = requete invalide.
                var fatal = (int)resp.StatusCode is 400 or 404;
                throw new LlmException($"Ollama {(int)resp.StatusCode} : {Truncate(body)}", fatal);
            }

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";
        }
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] : s;
}
