using System.Net.Http.Json;
using MailAgent.Configuration;
using MailAgent.Models;

namespace MailAgent.Services;

/// <summary>
/// Envoie la notification via l'API Bot Telegram (sendMessage).
/// Fiable et gratuit, sans expiration de session (contrairement au sandbox WhatsApp).
/// </summary>
public sealed class TelegramNotifier(AgentConfig config, HttpClient http) : INotifier
{
    public async Task NotifyAsync(EmailItem email, Classification classification, CancellationToken ct = default)
    {
        var text =
            $"📧 Mail important\n" +
            $"De : {email.From}\n" +
            $"Objet : {email.Subject}\n" +
            (classification.Action.Length > 0 ? $"➡️ A faire : {classification.Action}\n" : "") +
            $"Raison : {classification.Reason}";

        var url = $"https://api.telegram.org/bot{config.Telegram.BotToken}/sendMessage";
        var payload = new { chat_id = config.Telegram.ChatId, text, disable_web_page_preview = true };

        using var resp = await http.PostAsJsonAsync(url, payload, ct);
        if (!resp.IsSuccessStatusCode)
        {
            // Le corps de la reponse Telegram explique l'echec (ex. chat introuvable, token invalide).
            var detail = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Telegram a refuse l'envoi (HTTP {(int)resp.StatusCode}) : {detail}");
        }

        Console.WriteLine($"    Telegram: message envoye (HTTP {(int)resp.StatusCode}).");
    }
}
