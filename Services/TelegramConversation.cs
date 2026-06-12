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
        Reponds en francais, clairement et sans bla-bla, comme un message de chat (pas un mail).

        Tu as ACCES au contenu des derniers mails recus (fournis dans le contexte). Tu peux donc :
        - RESUMER les mails du jour / recents (qui ecrit, de quoi il s'agit, ce qui demande une action) ;
        - REPONDRE a une question sur un mail PRECIS (par expediteur ou sujet) en t'appuyant sur son
          contenu (ex. "que dit le mail de la banque ?", "resume le mail du syndic").
        Si le mail demande n'est PAS dans la liste fournie (probablement trop ancien), dis-le clairement
        et propose de preciser l'expediteur ou la date.

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
            foreach (var e in recentImportant.Take(10))
                sb.AppendLine($"- De {e.From} | {e.Subject}");
            sb.AppendLine();
        }

        // Derniers mails AVEC apercu du contenu, pour pouvoir resumer ou repondre sur un mail precis.
        var recent = await reader.GetRecentInboxWithBodyAsync(max: 15, ct);
        if (recent.Count > 0)
        {
            sb.AppendLine("Derniers mails recus en boite (avec apercu du contenu) :");
            foreach (var e in recent)
            {
                sb.AppendLine($"--- De: {e.From} | Objet: {e.Subject} | {(e.Seen ? "lu" : "non-lu")} | {e.Date:yyyy-MM-dd} ---");
                if (e.BodyPreview.Length > 0) sb.AppendLine(e.BodyPreview);
                sb.AppendLine();
            }
        }

        return sb.Length > 0 ? sb.ToString() : "(aucun mail recent en contexte)";
    }

    private async Task<string> AskAssistantAsync(string userMessage, string context, CancellationToken ct)
    {
        var payload = new
        {
            model = config.Claude.Model,
            max_tokens = 800,
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
