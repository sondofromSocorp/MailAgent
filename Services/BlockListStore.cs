using MailAgent.Configuration;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace MailAgent.Services;

/// <summary>
/// Liste des expediteurs bloques DEPUIS TELEGRAM (« bloque X »), persistee dans un dossier
/// IMAP dedie de la boite principale (pas de fichier d'etat, disque CI ephemere) : un unique
/// message dont le corps contient un fragment d'adresse par ligne. Elle COMPLETE les
/// BlockedSenders de la configuration (fixes) et s'applique a toutes les boites surveillees.
/// </summary>
public sealed class BlockListStore(AccountConfig account)
{
    /// <summary>Fragments bloques via Telegram (liste vide si le dossier n'existe pas encore).</summary>
    public async Task<IReadOnlyList<string>> GetAsync(CancellationToken ct = default)
    {
        using var client = await ConnectAsync(ct);
        var list = await ReadAsync(client, ct);
        await client.DisconnectAsync(true, ct);
        return list;
    }

    /// <summary>Ajoute un fragment (idempotent, insensible a la casse). Renvoie la liste a jour.</summary>
    public async Task<IReadOnlyList<string>> AddAsync(string fragment, CancellationToken ct = default)
    {
        using var client = await ConnectAsync(ct);
        var list = new List<string>(await ReadAsync(client, ct));
        if (!list.Any(f => string.Equals(f, fragment, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(fragment);
            await SaveAsync(client, list, ct);
        }
        await client.DisconnectAsync(true, ct);
        return list;
    }

    /// <summary>
    /// Retire les fragments contenant le terme (insensible a la casse) : « debloque saint-maclou »
    /// suffit pour retirer « news.saint-maclou.com ». Renvoie (retire ?, liste a jour).
    /// </summary>
    public async Task<(bool Removed, IReadOnlyList<string> List)> RemoveAsync(string fragment, CancellationToken ct = default)
    {
        using var client = await ConnectAsync(ct);
        var list = new List<string>(await ReadAsync(client, ct));
        var removed = list.RemoveAll(f => f.Contains(fragment, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed) await SaveAsync(client, list, ct);
        await client.DisconnectAsync(true, ct);
        return (removed, list);
    }

    private async Task<ImapClient> ConnectAsync(CancellationToken ct)
    {
        var client = new ImapClient();
        await client.ConnectAsync(account.Imap.Host, account.Imap.Port, SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(account.User, account.Password, ct);
        return client;
    }

    private async Task<IReadOnlyList<string>> ReadAsync(ImapClient client, CancellationToken ct)
    {
        var root = client.GetFolder(client.PersonalNamespaces[0]);
        IMailFolder folder;
        try { folder = await root.GetSubfolderAsync(account.Imap.BlocklistFolder, ct); }
        catch (FolderNotFoundException) { return []; }

        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        if (folder.Count == 0) return [];

        var msg = await folder.GetMessageAsync(folder.Count - 1, ct);
        return (msg.TextBody ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Remplace le message-liste (le dossier ne contient jamais qu'un message).</summary>
    private async Task SaveAsync(ImapClient client, IReadOnlyList<string> list, CancellationToken ct)
    {
        var root = client.GetFolder(client.PersonalNamespaces[0]);
        IMailFolder folder;
        try { folder = await root.GetSubfolderAsync(account.Imap.BlocklistFolder, ct); }
        catch (FolderNotFoundException) { folder = await root.CreateAsync(account.Imap.BlocklistFolder, isMessageFolder: true, ct); }

        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        if (folder.Count > 0)
        {
            var all = await folder.SearchAsync(SearchQuery.All, ct);
            await folder.AddFlagsAsync(all, MessageFlags.Deleted, silent: true, ct);
            await folder.ExpungeAsync(ct);
        }

        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(account.User));
        msg.To.Add(MailboxAddress.Parse(account.User));
        msg.Subject = "MailAgent : expediteurs bloques";
        msg.Body = new TextPart("plain") { Text = string.Join("\n", list) };
        await folder.AppendAsync(msg, MessageFlags.Seen, ct);
    }
}
