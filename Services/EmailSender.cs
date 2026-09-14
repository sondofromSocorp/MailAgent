using MailAgent.Configuration;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace MailAgent.Services;

/// <summary>
/// Envoi de mails (SMTP) et gestion du brouillon en attente de validation. L'etat (le brouillon
/// pret a partir) vit dans un dossier IMAP dedie (pas de fichier d'etat, disque CI ephemere).
/// Un seul brouillon en attente a la fois.
/// </summary>
public sealed class EmailSender(AgentConfig config, AccountConfig account)
{
    /// <summary>Envoie un message deja construit (To/Subject/References/corps) via SMTP.</summary>
    public async Task SendAsync(MimeMessage message, CancellationToken ct = default)
    {
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(config.Smtp.Host, config.Smtp.Port, SecureSocketOptions.StartTls, ct);
        await smtp.AuthenticateAsync(account.User, account.Password, ct);
        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(true, ct);
    }

    /// <summary>Stocke le brouillon en attente (remplace tout brouillon precedent).</summary>
    public async Task SavePendingAsync(MimeMessage message, CancellationToken ct = default)
    {
        using var client = await ConnectImapAsync(ct);
        var root = client.GetFolder(client.PersonalNamespaces[0]);
        IMailFolder folder;
        try { folder = await root.GetSubfolderAsync(config.Smtp.PendingFolder, ct); }
        catch (FolderNotFoundException) { folder = await root.CreateAsync(config.Smtp.PendingFolder, isMessageFolder: true, ct); }

        await ClearAsync(client, folder, ct);
        // Date de creation du brouillon : sert au delai d'expiration (Smtp:PendingTtlHours).
        message.Date = DateTimeOffset.Now;
        await folder.AppendAsync(message, MessageFlags.Draft, ct);
        await client.DisconnectAsync(true, ct);
    }

    /// <summary>Recupere le brouillon en attente (le plus recent), ou null s'il n'y en a pas.</summary>
    public async Task<MimeMessage?> GetPendingAsync(CancellationToken ct = default)
    {
        using var client = await ConnectImapAsync(ct);
        var folder = await TryGetPendingAsync(client, ct);
        MimeMessage? msg = null;
        if (folder is not null)
        {
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);
            // Du plus recent au plus ancien, en ignorant les pieces jointes en attente (voir
            // SaveAttachmentAsync) qui partagent le meme dossier.
            for (var i = folder.Count - 1; i >= 0 && msg is null; i--)
            {
                var m = await folder.GetMessageAsync(i, ct);
                if (m.Headers[KindHeader] != AttachmentKind) msg = m;
            }
        }
        await client.DisconnectAsync(true, ct);
        return msg;
    }

    // Les pieces jointes recues sur Telegram AVANT qu'un brouillon existe (ou apres, en attendant
    // d'etre fusionnees) sont stockees dans le meme dossier IMAP, une par message, marquees par
    // cet en-tete. SavePendingAsync vide le dossier : l'appelant doit d'abord les fusionner dans
    // le brouillon (BodyBuilder), ce qui les fait voyager avec lui.
    private const string KindHeader = "X-MailAgent-Kind";
    private const string AttachmentKind = "attachment";

    /// <summary>Stocke une piece jointe en attente d'un brouillon (survit a la fin de la passe).</summary>
    public async Task SaveAttachmentAsync(PendingAttachment attachment, CancellationToken ct = default)
    {
        using var client = await ConnectImapAsync(ct);
        var root = client.GetFolder(client.PersonalNamespaces[0]);
        IMailFolder folder;
        try { folder = await root.GetSubfolderAsync(config.Smtp.PendingFolder, ct); }
        catch (FolderNotFoundException) { folder = await root.CreateAsync(config.Smtp.PendingFolder, isMessageFolder: true, ct); }

        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(account.User));
        msg.To.Add(MailboxAddress.Parse(account.User));
        msg.Subject = $"[MailAgent] piece jointe en attente : {attachment.FileName}";
        msg.Headers.Add(KindHeader, AttachmentKind);
        msg.Date = DateTimeOffset.Now;
        var body = new BodyBuilder { TextBody = "Piece jointe recue via Telegram, en attente d'un brouillon." };
        body.Attachments.Add(attachment.FileName, attachment.Data, ParseContentType(attachment.MimeType));
        msg.Body = body.ToMessageBody();

        await folder.AppendAsync(msg, MessageFlags.Seen, ct);
        await client.DisconnectAsync(true, ct);
    }

    /// <summary>
    /// Pieces jointes en attente (ordre de reception), ou liste vide. Celles plus vieilles que
    /// maxAge sont ignorees (elles disparaitront au prochain brouillon/annulation).
    /// </summary>
    public async Task<IReadOnlyList<PendingAttachment>> GetAttachmentsAsync(TimeSpan maxAge, CancellationToken ct = default)
    {
        var list = new List<PendingAttachment>();
        using var client = await ConnectImapAsync(ct);
        var folder = await TryGetPendingAsync(client, ct);
        if (folder is not null)
        {
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);
            for (var i = 0; i < folder.Count; i++)
            {
                var m = await folder.GetMessageAsync(i, ct);
                if (m.Headers[KindHeader] != AttachmentKind) continue;
                if (DateTimeOffset.Now - m.Date > maxAge) continue;
                foreach (var part in m.Attachments.OfType<MimePart>())
                {
                    using var ms = new MemoryStream();
                    await part.Content.DecodeToAsync(ms, ct);
                    list.Add(new PendingAttachment(part.FileName ?? "fichier", part.ContentType.MimeType, ms.ToArray()));
                }
            }
        }
        await client.DisconnectAsync(true, ct);
        return list;
    }

    /// <summary>Type MIME tolerant : une valeur illisible retombe sur application/octet-stream.</summary>
    public static ContentType ParseContentType(string mimeType) =>
        ContentType.TryParse(mimeType, out var ct) ? ct : new ContentType("application", "octet-stream");

    /// <summary>Supprime le brouillon en attente ET les pieces jointes en attente (apres envoi ou annulation).</summary>
    public async Task DeletePendingAsync(CancellationToken ct = default)
    {
        using var client = await ConnectImapAsync(ct);
        var folder = await TryGetPendingAsync(client, ct);
        if (folder is not null) await ClearAsync(client, folder, ct);
        await client.DisconnectAsync(true, ct);
    }

    private async Task<ImapClient> ConnectImapAsync(CancellationToken ct)
    {
        var client = new ImapClient();
        await client.ConnectAsync(account.Imap.Host, account.Imap.Port, SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(account.User, account.Password, ct);
        return client;
    }

    private async Task<IMailFolder?> TryGetPendingAsync(ImapClient client, CancellationToken ct)
    {
        var root = client.GetFolder(client.PersonalNamespaces[0]);
        try { return await root.GetSubfolderAsync(config.Smtp.PendingFolder, ct); }
        catch (FolderNotFoundException) { return null; }
    }

    /// <summary>
    /// Vide le dossier des brouillons en attente en les deplacant vers la CORBEILLE.
    /// Un simple expunge ne suffit pas : Gmail ARCHIVE le message expurge d'un libelle,
    /// et le brouillon abandonne reste alors affiche dans le fil de conversation comme
    /// s'il avait ete envoye (constate le 24/08 avec une reponse remplacee).
    /// </summary>
    private async Task ClearAsync(ImapClient client, IMailFolder folder, CancellationToken ct)
    {
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        if (folder.Count == 0) return;

        var all = await folder.SearchAsync(SearchQuery.All, ct);
        var trash = await EmailReader.FindTrashAsync(client, account.Imap, ct);
        if (trash is not null)
        {
            await folder.MoveToAsync(all, trash, ct);
        }
        else
        {
            await folder.AddFlagsAsync(all, MessageFlags.Deleted, silent: true, ct);
            await folder.ExpungeAsync(ct);
        }
    }
}

/// <summary>Piece jointe recue via Telegram, en attente d'etre integree a un brouillon.</summary>
public sealed record PendingAttachment(string FileName, string MimeType, byte[] Data)
{
    public string SizeLabel => Data.Length < 1024 * 1024 ? $"{Data.Length / 1024.0:0} Ko" : $"{Data.Length / 1048576.0:0.0} Mo";
}
