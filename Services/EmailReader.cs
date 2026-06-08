using MailAgent.Configuration;
using MailAgent.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;

namespace MailAgent.Services;

/// <summary>
/// Lit les emails non lus via IMAP (tout fournisseur). Anti-doublon sans fichier d'etat :
/// les mails deja traites portent un marqueur IMAP (keyword standard) et sont exclus
/// de la recherche. Le mail n'est jamais marque comme lu cote serveur.
/// </summary>
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

        // IMAP standard : non lus QUI N'ONT PAS encore le marqueur de suivi.
        // -> remplace l'ancien state.json (l'etat anti-doublon vit dans la boite).
        var uids = await inbox.SearchAsync(
            SearchQuery.NotSeen.And(SearchQuery.NotKeyword(config.Imap.NotifiedKeyword)), ct);

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
                Uid: uid,
                MessageId: string.IsNullOrWhiteSpace(msg.MessageId) ? uid.ToString() : msg.MessageId,
                From: msg.From.ToString(),
                Subject: string.IsNullOrWhiteSpace(msg.Subject) ? "(sans objet)" : msg.Subject,
                BodyPreview: body,
                Date: msg.Date));
        }

        await client.DisconnectAsync(true, ct);
        return items;
    }

    /// <summary>
    /// Pose le marqueur de suivi (keyword IMAP) sur les mails traites.
    /// Ils seront exclus des prochaines passes. N'affecte ni le statut lu/non lu, ni le contenu.
    /// </summary>
    public async Task MarkNotifiedAsync(IList<UniqueId> uids, CancellationToken ct = default)
    {
        if (uids.Count == 0) return;

        using var client = new ImapClient();
        await client.ConnectAsync(config.Imap.Host, config.Imap.Port, SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(config.ImapUser, config.ImapPassword, ct);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite, ct);
        await inbox.AddFlagsAsync(uids, MessageFlags.None,
            new HashSet<string> { config.Imap.NotifiedKeyword }, silent: true, ct);

        await client.DisconnectAsync(true, ct);
    }
}
