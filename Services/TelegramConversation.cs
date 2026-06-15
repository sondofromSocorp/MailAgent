using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MailAgent.Configuration;
using MailAgent.Models;
using MimeKit;

namespace MailAgent.Services;

/// <summary>
/// Bot Telegram conversationnel : lit les messages entrants (getUpdates), determine l'intention
/// via Claude (chat / repondre a un mail / valider / annuler) et agit. Repondre a un mail passe
/// TOUJOURS par une validation explicite. Sans etat local : l'offset Telegram est confirme cote
/// serveur, et le brouillon en attente vit dans un dossier IMAP (cf. EmailSender).
/// </summary>
public sealed class TelegramConversation(AgentConfig config, HttpClient http, EmailReader reader, EmailSender sender)
{
    private const string RouterPrompt =
        """
        Tu es le routeur d'un assistant mail personnel accessible sur Telegram. A partir du message
        de l'utilisateur et de la liste NUMEROTEE de ses derniers mails, determine l'INTENTION et
        reponds en JSON STRICT : {"intent":"...","target":N,"reply":"...","answer":"..."}

        intent vaut EXACTEMENT l'une de ces valeurs :
        - "reply"  : l'utilisateur veut REPONDRE a un mail (ex. "reponds au syndic que je serai present").
                     target = le NUMERO du mail concerne dans la liste. reply = le texte COMPLET et poli
                     de la reponse a envoyer en son nom (salutation, message, formule de politesse),
                     en francais. answer = "".
        - "send"   : l'utilisateur VALIDE l'envoi en attente (ex. "oui", "envoie", "valide",
                     "ok envoie", "c'est bon"). target=0, reply="", answer="".
        - "cancel" : l'utilisateur ANNULE (ex. "annule", "non laisse tomber"). target=0, reply="", answer="".
        - "chat"   : tout le reste (question, resume, demande d'info). answer = ta reponse en francais
                     (resume / reponse), en t'appuyant sur le contenu des mails. target=0, reply="".

        En cas de DOUTE, choisis "chat" : ne declenche JAMAIS un envoi par erreur.
        Reponds UNIQUEMENT le JSON, sans texte ni balise autour.
        """;

    /// <summary>Lit les messages Telegram en attente et agit. Ne fait rien s'il n'y en a pas.</summary>
    public async Task RunAsync(IReadOnlyList<EmailItem> recentImportant, CancellationToken ct = default)
    {
        var updates = await GetUpdatesAsync(ct);
        if (updates.Count == 0) return;

        var recent = await reader.GetRecentInboxWithBodyAsync(max: 15, ct);
        var context = BuildContext(recentImportant, recent);

        long maxUpdateId = 0;
        foreach (var (updateId, chatId, text) in updates)
        {
            maxUpdateId = Math.Max(maxUpdateId, updateId);
            if (chatId.ToString() != config.Telegram.ChatId || string.IsNullOrWhiteSpace(text)) continue;

            Console.WriteLine($"  [TELEGRAM] recu : {text}");
            try
            {
                await HandleAsync(text, context, recent, ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    [ERREUR] {ex.Message}");
                try { await SendTextAsync($"Desole, une erreur est survenue : {ex.Message}", ct); } catch { }
            }
        }

        await ConfirmUpdatesAsync(maxUpdateId + 1, ct);
    }

    private async Task HandleAsync(string text, string context, IReadOnlyList<EmailItem> recent, CancellationToken ct)
    {
        var route = await RouteAsync(text, context, ct);
        switch (route.Intent)
        {
            case "send":
                await HandleSendAsync(ct);
                break;
            case "cancel":
                await sender.DeletePendingAsync(ct);
                await SendTextAsync("Ok, j'annule : rien n'a ete envoye.", ct);
                Console.WriteLine("    -> brouillon annule.");
                break;
            case "reply":
                await HandleReplyAsync(route, recent, ct);
                break;
            default:
                await SendTextAsync(route.Answer.Length > 0 ? route.Answer : "(pas de reponse)", ct);
                Console.WriteLine("    -> reponse chat envoyee.");
                break;
        }
    }

    private async Task HandleReplyAsync(Route route, IReadOnlyList<EmailItem> recent, CancellationToken ct)
    {
        if (route.Target < 1 || route.Target > recent.Count || route.Reply.Length == 0)
        {
            await SendTextAsync("Je n'ai pas reussi a identifier le mail auquel repondre (il n'est peut-etre pas dans les 15 derniers). Precise l'expediteur ?", ct);
            return;
        }

        var original = recent[route.Target - 1];
        var msg = BuildReply(original, route.Reply, out var toAddress);
        if (toAddress is null)
        {
            await SendTextAsync($"Impossible de determiner l'adresse de reponse pour \"{original.Subject}\".", ct);
            return;
        }

        await sender.SavePendingAsync(msg, ct);

        var noReplyWarn =
            toAddress.Contains("no-reply", StringComparison.OrdinalIgnoreCase) ||
            toAddress.Contains("noreply", StringComparison.OrdinalIgnoreCase)
                ? "\n\n⚠️ L'adresse ressemble a un no-reply : la reponse sera peut-etre ignoree."
                : "";

        await SendTextAsync(
            $"✉️ Proposition de reponse a {toAddress}\nObjet : {msg.Subject}\n\n{route.Reply}\n\n" +
            $"Reponds OUI pour envoyer, ou dis-moi quoi changer.{noReplyWarn}", ct);
        Console.WriteLine($"    -> proposition de reponse stockee (a {toAddress}).");
    }

    private async Task HandleSendAsync(CancellationToken ct)
    {
        var pending = await sender.GetPendingAsync(ct);
        if (pending is null)
        {
            await SendTextAsync("Il n'y a aucune reponse en attente a envoyer.", ct);
            return;
        }

        await sender.SendAsync(pending, ct);
        await sender.DeletePendingAsync(ct);
        var to = pending.To.ToString();
        await SendTextAsync($"✅ Envoye a {to}.", ct);
        Console.WriteLine($"    -> mail envoye a {to}.");
    }

    /// <summary>Construit la reponse (MimeMessage) au mail d'origine, avec threading (Re: + In-Reply-To).</summary>
    private MimeMessage BuildReply(EmailItem original, string replyText, out string? toAddress)
    {
        toAddress = null;
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(config.ImapUser));

        foreach (var mb in InternetAddressList.Parse(original.From).Mailboxes)
        {
            msg.To.Add(mb);
            toAddress = mb.Address;
            break;
        }

        msg.Subject = original.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
            ? original.Subject
            : "Re: " + original.Subject;

        if (original.MessageId.Contains('@'))
        {
            msg.InReplyTo = original.MessageId;
            msg.References.Add(original.MessageId);
        }

        msg.Body = new TextPart("plain") { Text = replyText };
        return msg;
    }

    private async Task<Route> RouteAsync(string userMessage, string context, CancellationToken ct)
    {
        var payload = new
        {
            model = config.Claude.Model,
            max_tokens = 1000,
            system = RouterPrompt,
            messages = new[]
            {
                new { role = "user", content = $"Mails recents (numerotes) :\n{context}\n\nMessage de l'utilisateur :\n{userMessage}" }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, config.Claude.ApiBaseUrl);
        req.Headers.Add("x-api-key", config.AnthropicApiKey);
        req.Headers.Add("anthropic-version", config.Claude.AnthropicVersion);
        req.Content = JsonContent.Create(payload);

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var detail = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Claude a refuse la requete (HTTP {(int)resp.StatusCode}) : {detail}");
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var raw = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "{}";
        raw = ExtractJson(raw);

        try
        {
            var dto = JsonSerializer.Deserialize<RouteDto>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var intent = dto?.Intent?.Trim().ToLowerInvariant() ?? "chat";
            return new Route(intent, dto?.Target ?? 0, dto?.Reply?.Trim() ?? "", dto?.Answer?.Trim() ?? "");
        }
        catch (JsonException)
        {
            // En cas de doute, on ne fait jamais d'action sensible : on retombe sur "chat".
            return new Route("chat", 0, "", "Je n'ai pas bien compris, peux-tu reformuler ?");
        }
    }

    private static string BuildContext(IReadOnlyList<EmailItem> recentImportant, IReadOnlyList<EmailItem> recent)
    {
        var sb = new StringBuilder();

        if (recentImportant.Count > 0)
        {
            sb.AppendLine("Mails importants detectes a la derniere passe :");
            foreach (var e in recentImportant.Take(10))
                sb.AppendLine($"- De {e.From} | {e.Subject}");
            sb.AppendLine();
        }

        if (recent.Count > 0)
        {
            sb.AppendLine("Derniers mails recus (numerotes, avec apercu du contenu) :");
            for (var i = 0; i < recent.Count; i++)
            {
                var e = recent[i];
                sb.AppendLine($"--- Mail #{i + 1} | De: {e.From} | Objet: {e.Subject} | {(e.Seen ? "lu" : "non-lu")} | {e.Date:yyyy-MM-dd} ---");
                if (e.BodyPreview.Length > 0) sb.AppendLine(e.BodyPreview);
                sb.AppendLine();
            }
        }

        return sb.Length > 0 ? sb.ToString() : "(aucun mail recent en contexte)";
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

    private async Task ConfirmUpdatesAsync(long offset, CancellationToken ct)
    {
        var url = $"https://api.telegram.org/bot{config.Telegram.BotToken}/getUpdates?offset={offset}";
        try { using var _ = await http.GetAsync(url, ct); } catch { /* best effort */ }
    }

    private async Task SendTextAsync(string text, CancellationToken ct)
    {
        // Telegram refuse (HTTP 400) un message vide ou de plus de 4096 caracteres : on garantit
        // un texte non vide et on decoupe les longues reponses (ex. un resume) en plusieurs envois.
        if (string.IsNullOrWhiteSpace(text)) text = "(vide)";

        var url = $"https://api.telegram.org/bot{config.Telegram.BotToken}/sendMessage";
        foreach (var chunk in SplitForTelegram(text))
        {
            var payload = new { chat_id = config.Telegram.ChatId, text = chunk, disable_web_page_preview = true };
            using var resp = await http.PostAsJsonAsync(url, payload, ct);
            if (!resp.IsSuccessStatusCode)
            {
                // Le corps Telegram explique le refus (ex. message trop long, chat introuvable) : utile pour diagnostiquer.
                var detail = await resp.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"Telegram a refuse l'envoi (HTTP {(int)resp.StatusCode}) : {detail}");
            }
        }
    }

    /// <summary>Decoupe un texte en morceaux sous la limite Telegram (4096 car.), de preference sur un saut de ligne.</summary>
    private static IEnumerable<string> SplitForTelegram(string text)
    {
        const int max = 4000; // marge sous la limite stricte de 4096
        while (text.Length > max)
        {
            var cut = text.LastIndexOf('\n', max - 1);
            if (cut <= 0) cut = max; // pas de saut de ligne exploitable : on coupe net
            yield return text[..cut];
            text = text[cut..].TrimStart('\n');
        }
        yield return text;
    }

    /// <summary>Retire d'eventuels backticks autour du JSON.</summary>
    private static string ExtractJson(string s)
    {
        s = s.Trim();
        var first = s.IndexOf('{');
        var last = s.LastIndexOf('}');
        return first >= 0 && last > first ? s[first..(last + 1)] : s;
    }

    private sealed record Route(string Intent, int Target, string Reply, string Answer);

    private sealed record RouteDto(string? Intent, int Target, string? Reply, string? Answer);
}
