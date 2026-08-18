using MailAgent.Configuration;
using MailAgent.Models;
using MimeKit;

namespace MailAgent.Services;

/// <summary>
/// Desabonnement A LA DEMANDE (via l'assistant Telegram) en s'appuyant sur l'en-tete standard
/// List-Unsubscribe du mail : POST "one-click" (RFC 8058, concu pour etre automatise) ou envoi
/// d'un mail de desinscription (cible mailto:). Jamais automatique sur simple classification :
/// cliquer un lien de desabonnement d'un spam confirme que l'adresse est active.
/// </summary>
public sealed class UnsubscribeService(AccountConfig account, EmailSender sender, HttpClient http)
{
    /// <summary>Tente le desabonnement et renvoie un message resultat a afficher a l'utilisateur.</summary>
    public async Task<string> TryUnsubscribeAsync(EmailItem email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email.UnsubscribeHeader))
            return $"\"{email.Subject}\" ne fournit pas d'en-tete de desabonnement standard : "
                 + "il faudra passer par le lien de desinscription dans le corps du mail.";

        var targets = ParseTargets(email.UnsubscribeHeader);
        var httpUrl = targets.FirstOrDefault(t => t.StartsWith("http", StringComparison.OrdinalIgnoreCase));
        var mailto = targets.FirstOrDefault(t => t.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase));

        // 1) One-click (RFC 8058) : un POST suffit, sans interaction. Le cas ideal.
        if (email.OneClickUnsubscribe && httpUrl is not null)
        {
            try
            {
                using var content = new StringContent("List-Unsubscribe=One-Click",
                    System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
                using var resp = await http.PostAsync(httpUrl, content, ct);
                if (resp.IsSuccessStatusCode)
                    return $"✅ Desabonnement one-click effectue pour \"{email.Subject}\" (de {email.From}).";
                Console.WriteLine($"    [Desabo] POST one-click refuse (HTTP {(int)resp.StatusCode}), repli mailto/lien.");
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"    [Desabo] POST one-click impossible ({ex.Message}), repli mailto/lien.");
            }
        }

        // 2) Cible mailto: -> on envoie le mail de desinscription a la place de l'utilisateur.
        if (mailto is not null)
        {
            var (address, subject) = ParseMailto(mailto);
            if (address.Length > 0)
            {
                var msg = new MimeMessage();
                msg.From.Add(MailboxAddress.Parse(account.User));
                msg.To.Add(MailboxAddress.Parse(address));
                msg.Subject = subject;
                msg.Body = new TextPart("plain") { Text = "unsubscribe" };
                await sender.SendAsync(msg, ct);
                return $"✅ Mail de desabonnement envoye a {address} pour \"{email.Subject}\".";
            }
        }

        // 3) Il ne reste qu'un lien web sans one-click : une visite automatique ne suffirait
        //    probablement pas (page de confirmation) -- on transmet le lien.
        return httpUrl is not null
            ? $"Ce desabonnement demande un clic manuel : {httpUrl}"
            : "Aucun mecanisme de desabonnement exploitable dans l'en-tete du mail.";
    }

    /// <summary>Decoupe l'en-tete List-Unsubscribe : cibles entre chevrons, separees par des virgules.</summary>
    private static List<string> ParseTargets(string header) =>
        [.. header.Split(',')
            .Select(t => t.Trim().TrimStart('<').TrimEnd('>').Trim())
            .Where(t => t.Length > 0)];

    /// <summary>Extrait adresse et sujet d'une cible mailto: (sujet "unsubscribe" par defaut).</summary>
    private static (string Address, string Subject) ParseMailto(string mailto)
    {
        var rest = mailto["mailto:".Length..];
        var q = rest.IndexOf('?');
        var address = (q >= 0 ? rest[..q] : rest).Trim();
        var subject = "unsubscribe";
        if (q >= 0)
        {
            foreach (var part in rest[(q + 1)..].Split('&'))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2 && kv[0].Equals("subject", StringComparison.OrdinalIgnoreCase))
                    subject = Uri.UnescapeDataString(kv[1]);
            }
        }
        return (address, subject);
    }
}
