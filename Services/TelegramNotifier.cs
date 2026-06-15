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
        // Message en langage naturel redige par le modele ; repli sur un format simple si absent.
        var text = classification.Notif.Length > 0
            ? $"📩 {classification.Notif}"
            : $"📧 Mail important\n" +
              $"De : {email.From}\n" +
              $"Objet : {email.Subject}\n" +
              (classification.Action.Length > 0 ? $"➡️ A faire : {classification.Action}\n" : "") +
              $"Raison : {classification.Reason}";

        await SendTextAsync(text, ct);
    }

    public async Task SendTextAsync(string text, CancellationToken ct = default)
    {
        // Telegram refuse (HTTP 400) un message vide ou de plus de 4096 caracteres : on garantit
        // un texte non vide et on decoupe au besoin (une notif en langage naturel peut etre longue).
        if (string.IsNullOrWhiteSpace(text)) text = "(vide)";

        var url = $"https://api.telegram.org/bot{config.Telegram.BotToken}/sendMessage";
        foreach (var chunk in SplitForTelegram(text))
        {
            var payload = new { chat_id = config.Telegram.ChatId, text = chunk, disable_web_page_preview = true };

            using var resp = await http.PostAsJsonAsync(url, payload, ct);
            if (!resp.IsSuccessStatusCode)
            {
                // Le corps de la reponse Telegram explique l'echec (ex. chat introuvable, token invalide, message trop long).
                var detail = await resp.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"Telegram a refuse l'envoi (HTTP {(int)resp.StatusCode}) : {detail}");
            }

            Console.WriteLine($"    Telegram: message envoye (HTTP {(int)resp.StatusCode}).");
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
}
