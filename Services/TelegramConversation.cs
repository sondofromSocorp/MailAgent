using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MailAgent.Configuration;
using MailAgent.Models;

namespace MailAgent.Services;

/// <summary>
/// Rend le bot Telegram conversationnel : lit les messages entrants (getUpdates), genere
/// une reponse via Claude avec en contexte les mails recents, puis repond (sendMessage).
/// Sans etat local : les updates traites sont confirmes (purges) cote Telegram via l'offset.
/// </summary>
public sealed class TelegramConversation(AgentConfig config, HttpClient http, EmailReader reader)
{
    private const string SystemPrompt =
        """
        Tu es l'assistant personnel de messagerie de l'utilisateur, accessible via Telegram.
        Reponds en francais, de facon BREVE et claire, comme un message de chat (pas un mail).
        Sers-toi du contexte fourni (mails recents et mails importants detectes) pour repondre
        aux questions du type "qu'est-ce que j'ai a traiter ?", "resume mes mails", "ai-je une
        facture ?". Si l'info n'est pas dans le contexte, dis-le simplement.
        Tu ne peux pas encore AGIR sur la boite (ranger, supprimer, repondre a un mail) :
        si on te le demande, reponds que cette fonction arrive bientot.
        """;

    /// <summary>Lit les messages Telegram en attente et y repond. Ne fait rien s'il n'y en a pas.</summary>
    public async Task RunAsync(IReadOnlyList<EmailItem> recentImportant, CancellationToken ct = default)
    {
        var updates = await GetUpdatesAsync(ct);
        if (updates.Count == 0) return;

        var context = await BuildContextAsync(recentImportant, ct);

        long maxUpdateId = 0;
        foreach (var (updateId, chatId, text) in updates)
        {
            maxUpdateId = Math.Max(maxUpdateId, updateId);

            // Ne repond qu'au proprietaire (chat configure) et aux messages texte non vides.
            if (chatId.ToString() != config.Telegram.ChatId || string.IsNullOrWhiteSpace(text)) continue;

            Console.WriteLine($"  [TELEGRAM] recu : {text}");
            try
            {
                var reply = await AskAssistantAsync(text, context, ct);
                await SendAsync(reply, ct);
                Console.WriteLine("    -> reponse envoyee.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    [ERREUR] reponse Telegram : {ex.Message}");
            }
        }

        // Confirme les updates traites : Telegram ne les renverra plus.
        await ConfirmUpdatesAsync(maxUpdateId + 1, ct);
    }

    private async Task<List<(long updateId, long chatId, string text)>> GetUpdatesAsync(CancellationToken ct)
    {
        var url = $"https://api.telegram.org/bot{config.Telegram.BotToken}/getUpdates?timeout=0";
        using var resp = await http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var list = new List<(long, long, string)>();
        foreach (var u in doc.RootElement.GetProperty("result").EnumerateArray())
        {
            var updateId = u.GetProperty("update_id").GetInt64();
            if (!u.TryGetProperty("message", out var msg)) continue;
            var chatId = msg.GetProperty("chat").GetProperty("id").GetInt64();
            var text = msg.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            list.Add((updateId, chatId, text));
        }
        return list;
    }

    /// <summary>Confirme (purge) les updates jusqu'a l'offset donne. Best effort.</summary>
    private async Task ConfirmUpdatesAsync(long offset, CancellationToken ct)
    {
        var url = $"https://api.telegram.org/bot{config.Telegram.BotToken}/getUpdates?offset={offset}";
        try { using var _ = await http.GetAsync(url, ct); } catch { /* best effort */ }
    }

    private async Task SendAsync(string text, CancellationToken ct)
    {
        var url = $"https://api.telegram.org/bot{config.Telegram.BotToken}/sendMessage";
        var payload = new { chat_id = config.Telegram.ChatId, text, disable_web_page_preview = true };
        using var resp = await http.PostAsJsonAsync(url, payload, ct);
        resp.EnsureSuccessStatusCode();
    }

    private async Task<string> BuildContextAsync(IReadOnlyList<EmailItem> recentImportant, CancellationToken ct)
    {
        var sb = new StringBuilder();

        if (recentImportant.Count > 0)
        {
            sb.AppendLine("Mails importants detectes a la derniere passe :");
            foreach (var e in recentImportant.Take(15))
                sb.AppendLine($"- De {e.From} | {e.Subject}");
            sb.AppendLine();
        }

        var recent = await reader.GetInboxOverviewAsync(maxAgeDays: 7, max: 25, ct);
        if (recent.Count > 0)
        {
            sb.AppendLine("Apercu des mails recents en boite de reception :");
            foreach (var e in recent)
                sb.AppendLine($"- {(e.Seen ? "lu   " : "nonlu")} | De {e.From} | {e.Subject}");
        }

        return sb.Length > 0 ? sb.ToString() : "(aucun mail recent en contexte)";
    }

    private async Task<string> AskAssistantAsync(string userMessage, string context, CancellationToken ct)
    {
        var payload = new
        {
            model = config.Claude.Model,
            max_tokens = 500,
            system = SystemPrompt,
            messages = new[]
            {
                new { role = "user", content = $"Contexte:\n{context}\n\nMessage de l'utilisateur:\n{userMessage}" }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, config.Claude.ApiBaseUrl);
        req.Headers.Add("x-api-key", config.AnthropicApiKey);
        req.Headers.Add("anthropic-version", config.Claude.AnthropicVersion);
        req.Content = JsonContent.Create(payload);

        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "(pas de reponse)";
    }
}
