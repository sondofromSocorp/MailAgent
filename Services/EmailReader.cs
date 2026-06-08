using MailAgent.Configuration;
using MailAgent.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;

namespace MailAgent.Services;

/// <summary>
/// Lit les emails non lus via IMAP (Gmail). Anti-doublon sans fichier d'etat :
/// les mails deja traites portent un libelle Gmail et sont exclus de la recherche.
/// Le mail n'est jamais marque comme lu cote serveur.
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

        // Recherche native Gmail : non lus QUI N'ONT PAS encore le libelle de suivi.
        // -> remplace l'ancien state.json (l'etat anti-doublon vit dans Gmail).
        var uids = await inbox.SearchAsync(
            SearchQuery.GMailRawSearch($"is:unread -label:{config.Imap.NotifiedLabel}"), ct);

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
    /// Pose le libelle de suivi sur les mails traites (Gmail cree le libelle s'il n'existe pas).
    /// Ils seront exclus des prochaines passes. N'affecte ni le statut lu/non lu, ni le contenu.
    /// </summary>
    public async Task AddNotifiedLabelsAsync(IList<UniqueId> uids, CancellationToken ct = default)
    {
        if (uids.Count == 0) return;

        using var client = new ImapClient();
        await client.ConnectAsync(config.Imap.Host, config.Imap.Port, SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(config.ImapUser, config.ImapPassword, ct);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite, ct);
        await inbox.AddLabelsAsync(uids, new[] { config.Imap.NotifiedLabel }, silent: true, ct);

        await client.DisconnectAsync(true, ct);
    }
}
