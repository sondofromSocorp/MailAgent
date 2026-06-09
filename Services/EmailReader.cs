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

    public async Task<IReadOnlyList<EmailItem>> GetToProcessAsync(CancellationToken ct = default)
    {
        using var client = new ImapClient();
        await client.ConnectAsync(config.Imap.Host, config.Imap.Port, SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(config.ImapUser, config.ImapPassword, ct);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct);

        // Tous les mails (lus comme non lus) SANS le marqueur de suivi, limites aux MaxAgeDays
        // derniers jours. L'etat anti-doublon vit dans la boite (pas de state.json).
        SearchQuery query = SearchQuery.NotKeyword(config.Imap.NotifiedKeyword);
        if (config.Imap.MaxAgeDays > 0)
            query = query.And(SearchQuery.DeliveredAfter(DateTime.Now.AddDays(-config.Imap.MaxAgeDays)));
        var uids = await inbox.SearchAsync(query, ct);

        // Les plus recents d'abord, plafonne le nombre traite par passe (cout / duree).
        var selected = uids.Reverse().Take(config.Imap.MaxPerPass).ToList();
        if (selected.Count == 0)
        {
            await client.DisconnectAsync(true, ct);
            return [];
        }

        // Drapeaux (lu / repondu) en un seul fetch, pour calibrer les notifications.
        var flagsByUid = new Dictionary<UniqueId, MessageFlags>();
        foreach (var s in await inbox.FetchAsync(selected, MessageSummaryItems.Flags, ct))
            flagsByUid[s.UniqueId] = s.Flags ?? MessageFlags.None;

        var items = new List<EmailItem>(selected.Count);
        foreach (var uid in selected)
        {
            ct.ThrowIfCancellationRequested();

            var msg = await inbox.GetMessageAsync(uid, ct);
            var body = msg.TextBody ?? msg.HtmlBody ?? "";
            if (body.Length > BodyPreviewMaxChars)
                body = body[..BodyPreviewMaxChars];

            // Defaut sur (lu + repondu) si les drapeaux manquent : ne pas notifier par securite.
            var flags = flagsByUid.TryGetValue(uid, out var f) ? f : (MessageFlags.Seen | MessageFlags.Answered);
            items.Add(new EmailItem(
                Uid: uid,
                Seen: flags.HasFlag(MessageFlags.Seen),
                Answered: flags.HasFlag(MessageFlags.Answered),
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
    /// Apercu leger de la boite (sujets / expediteurs / flags) des N derniers jours, pour
    /// donner du contexte a l'assistant conversationnel. Ne telecharge pas le corps des messages.
    /// </summary>
    public async Task<IReadOnlyList<EmailItem>> GetInboxOverviewAsync(int maxAgeDays, int max, CancellationToken ct = default)
    {
        using var client = new ImapClient();
        await client.ConnectAsync(config.Imap.Host, config.Imap.Port, SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(config.ImapUser, config.ImapPassword, ct);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct);

        SearchQuery query = maxAgeDays > 0
            ? SearchQuery.DeliveredAfter(DateTime.Now.AddDays(-maxAgeDays))
            : SearchQuery.All;
        var uids = await inbox.SearchAsync(query, ct);
        var selected = uids.Reverse().Take(max).ToList();

        var items = new List<EmailItem>(selected.Count);
        if (selected.Count > 0)
        {
            foreach (var s in await inbox.FetchAsync(selected, MessageSummaryItems.Flags | MessageSummaryItems.Envelope, ct))
            {
                var env = s.Envelope;
                items.Add(new EmailItem(
                    Uid: s.UniqueId,
                    Seen: s.Flags?.HasFlag(MessageFlags.Seen) ?? false,
                    Answered: s.Flags?.HasFlag(MessageFlags.Answered) ?? false,
                    MessageId: env?.MessageId ?? s.UniqueId.ToString(),
                    From: env?.From?.ToString() ?? "",
                    Subject: string.IsNullOrWhiteSpace(env?.Subject) ? "(sans objet)" : env!.Subject,
                    BodyPreview: "",
                    Date: env?.Date ?? DateTimeOffset.MinValue));
            }
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

    /// <summary>
    /// Deplace les mails vers un dossier dedie (cree s'il n'existe pas). Le chemin peut etre
    /// hierarchique ("Factures/Bouygues") : chaque niveau manquant est cree. Sur Gmail, ces
    /// dossiers apparaissent comme des libelles. Rien n'est supprime : tout reste recuperable.
    /// Portable sur tout serveur IMAP.
    /// </summary>
    public async Task MoveToFolderAsync(IList<UniqueId> uids, string folderPath, CancellationToken ct = default)
    {
        if (uids.Count == 0) return;

        using var client = new ImapClient();
        await client.ConnectAsync(config.Imap.Host, config.Imap.Port, SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(config.ImapUser, config.ImapPassword, ct);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite, ct);

        var root = client.GetFolder(client.PersonalNamespaces[0]);
        var target = await EnsureFolderPathAsync(root, folderPath, ct);

        // Marque comme lu avant le deplacement (le flag est conserve dans le dossier cible),
        // pour faire baisser le compteur de non-lus sur les mails inutiles.
        if (config.Imap.MarkMovedAsRead)
            await inbox.AddFlagsAsync(uids, MessageFlags.Seen, silent: true, ct);

        await inbox.MoveToAsync(uids, target, ct);
        await client.DisconnectAsync(true, ct);
    }

    /// <summary>
    /// Resout un chemin hierarchique ("Parent/Enfant") sous la racine, en creant chaque
    /// niveau manquant. Le decoupage se fait sur '/' (chemin logique) ; MailKit applique
    /// le separateur reel du serveur lors de la creation de chaque sous-dossier.
    /// </summary>
    private static async Task<IMailFolder> EnsureFolderPathAsync(IMailFolder root, string folderPath, CancellationToken ct)
    {
        var current = root;
        foreach (var name in folderPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            IMailFolder child;
            try
            {
                child = await current.GetSubfolderAsync(name, ct);
            }
            catch (FolderNotFoundException)
            {
                child = await current.CreateAsync(name, isMessageFolder: true, ct);
            }
            current = child;
        }
        return current;
    }
}
