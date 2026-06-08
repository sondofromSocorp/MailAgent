using MailAgent.Configuration;
using MailAgent.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;

namespace MailAgent.Services;

/// <summary>Lit les emails non lus via IMAP (Gmail par defaut). Lecture seule : ne marque rien comme lu cote serveur.</summary>
public sealed class EmailReader(AgentConfig config)
{
    private const int BodyPreviewMaxChars = 2000;

    public async Task<IReadOnlyList<EmailItem>> GetUnreadAsync(CancellationToken ct = default)
    {
        using var client = new ImapClient();
        await client.ConnectAsync(config.Imap.Host, config.Imap.Port, SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(config.ImapUser, config.ImapPassword, ct);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct);

        var uids = await inbox.SearchAsync(SearchQuery.NotSeen, ct);

        // Les plus recents d'abord, plafonne pour controler le cout.
        var selected = uids.Reverse().Take(config.Imap.MaxUnreadToProcess).ToList();

        var items = new List<EmailItem>(selected.Count);
        foreach (var uid in selected)
        {
            ct.ThrowIfCancellationRequested();

            var msg = await inbox.GetMessageAsync(uid, ct);
            var body = msg.TextBody ?? msg.HtmlBody ?? "";
            if (body.Length > BodyPreviewMaxChars)
                body = body[..BodyPreviewMaxChars];

            items.Add(new EmailItem(
                MessageId: string.IsNullOrWhiteSpace(msg.MessageId) ? uid.ToString() : msg.MessageId,
                From: msg.From.ToString(),
                Subject: string.IsNullOrWhiteSpace(msg.Subject) ? "(sans objet)" : msg.Subject,
                BodyPreview: body,
                Date: msg.Date));
        }

        await client.DisconnectAsync(true, ct);
        return items;
    }
}
