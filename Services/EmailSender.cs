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
public sealed class EmailSender(AgentConfig config)
{
    /// <summary>Envoie un message deja construit (To/Subject/References/corps) via SMTP.</summary>
    public async Task SendAsync(MimeMessage message, CancellationToken ct = default)
    {
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(config.Smtp.Host, config.Smtp.Port, SecureSocketOptions.StartTls, ct);
        await smtp.AuthenticateAsync(config.ImapUser, config.ImapPassword, ct);
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

        await ClearAsync(folder, ct);
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
            if (folder.Count > 0) msg = await folder.GetMessageAsync(folder.Count - 1, ct);
        }
        await client.DisconnectAsync(true, ct);
        return msg;
    }

    /// <summary>Supprime le brouillon en attente (apres envoi ou annulation).</summary>
    public async Task DeletePendingAsync(CancellationToken ct = default)
    {
        using var client = await ConnectImapAsync(ct);
        var folder = await TryGetPendingAsync(client, ct);
        if (folder is not null) await ClearAsync(folder, ct);
        await client.DisconnectAsync(true, ct);
    }

    private async Task<ImapClient> ConnectImapAsync(CancellationToken ct)
    {
        var client = new ImapClient();
        await client.ConnectAsync(config.Imap.Host, config.Imap.Port, SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(config.ImapUser, config.ImapPassword, ct);
        return client;
    }

    private async Task<IMailFolder?> TryGetPendingAsync(ImapClient client, CancellationToken ct)
    {
        var root = client.GetFolder(client.PersonalNamespaces[0]);
        try { return await root.GetSubfolderAsync(config.Smtp.PendingFolder, ct); }
        catch (FolderNotFoundException) { return null; }
    }

    private static async Task ClearAsync(IMailFolder folder, CancellationToken ct)
    {
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        if (folder.Count > 0)
        {
            var all = await folder.SearchAsync(SearchQuery.All, ct);
            await folder.AddFlagsAsync(all, MessageFlags.Deleted, silent: true, ct);
            await folder.ExpungeAsync(ct);
        }
    }
}
