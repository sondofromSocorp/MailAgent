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
public sealed class EmailReader(AccountConfig account)
{
    private const int BodyPreviewMaxChars = 2000;

    /// <param name="excludeDeferred">
    /// true (heures silencieuses) : ignore les mails deja marques "notif reportee", pour ne pas
    /// les re-classer a chaque passe de la nuit. false : ils sont repris (et notifies).
    /// </param>
    public async Task<IReadOnlyList<EmailItem>> GetToProcessAsync(bool excludeDeferred, CancellationToken ct = default)
    {
        using var client = new ImapClient();
        await client.ConnectAsync(account.Imap.Host, account.Imap.Port, SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(account.User, account.Password, ct);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct);

        // Tous les mails (lus comme non lus) SANS le marqueur de suivi, limites aux MaxAgeDays
        // derniers jours. L'etat anti-doublon vit dans la boite (pas de state.json).
        SearchQuery query = SearchQuery.NotKeyword(account.Imap.NotifiedKeyword);
        if (excludeDeferred)
            query = query.And(SearchQuery.NotKeyword(account.Imap.DeferredKeyword));
        if (account.Imap.MaxAgeDays > 0)
            query = query.And(SearchQuery.DeliveredAfter(DateTime.Now.AddDays(-account.Imap.MaxAgeDays)));
        var uids = await inbox.SearchAsync(query, ct);

        // Les plus recents d'abord, plafonne le nombre traite par passe (cout / duree).
        var selected = uids.Reverse().Take(account.Imap.MaxPerPass).ToList();
        if (selected.Count == 0)
        {
            await client.DisconnectAsync(true, ct);
            return [];
        }

        // Drapeaux (lu / repondu) et keywords (notif reportee) en un seul fetch.
        var flagsByUid = new Dictionary<UniqueId, MessageFlags>();
        var deferredUids = new HashSet<UniqueId>();
        foreach (var s in await inbox.FetchAsync(selected, MessageSummaryItems.Flags, ct))
        {
            flagsByUid[s.UniqueId] = s.Flags ?? MessageFlags.None;
            if (s.Keywords is not null && s.Keywords.Contains(account.Imap.DeferredKeyword))
                deferredUids.Add(s.UniqueId);
        }

        var items = new List<EmailItem>(selected.Count);
        foreach (var uid in selected)
        {
            ct.ThrowIfCancellationRequested();

            var msg = await inbox.GetMessageAsync(uid, ct);
            var body = ExtractBody(msg, BodyPreviewMaxChars);

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
                Date: msg.Date,
                UnsubscribeHeader: msg.Headers["List-Unsubscribe"] ?? "",
                OneClickUnsubscribe: (msg.Headers["List-Unsubscribe-Post"] ?? "")
                    .Contains("One-Click", StringComparison.OrdinalIgnoreCase),
                Deferred: deferredUids.Contains(uid)));
        }

        await client.DisconnectAsync(true, ct);
        return items;
    }

    /// <summary>
    /// Corps d'un mail en TEXTE, tronque a maxChars. La partie texte est preferee ; a defaut,
    /// la partie HTML est convertie (balises, styles et scripts retires) : sans cela, un mail
    /// HTML-seul ne donnait au modele que des balises et du CSS dans ses premiers caracteres.
    /// </summary>
    private static string ExtractBody(MimeKit.MimeMessage msg, int maxChars)
    {
        var body = msg.TextBody;
        if (string.IsNullOrWhiteSpace(body))
            body = HtmlText.ToPlainText(msg.HtmlBody ?? "");
        return body.Length > maxChars ? body[..maxChars] : body;
    }

    /// <summary>
    /// Apercu leger de la boite (sujets / expediteurs / flags) des N derniers jours, pour
    /// donner du contexte a l'assistant conversationnel. Ne telecharge pas le corps des messages.
    /// </summary>
    public async Task<IReadOnlyList<EmailItem>> GetInboxOverviewAsync(int maxAgeDays, int max, CancellationToken ct = default)
    {
        using var client = new ImapClient();
        await client.ConnectAsync(account.Imap.Host, account.Imap.Port, SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(account.User, account.Password, ct);

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
    /// Recupere les N mails les plus recents de la boite AVEC un apercu de leur contenu (corps
    /// tronque), pour permettre a l'assistant conversationnel de les resumer ou de repondre a
    /// une question sur un mail precis. Lus comme non lus.
    /// </summary>
    public async Task<IReadOnlyList<EmailItem>> GetRecentInboxWithBodyAsync(int max, CancellationToken ct = default)
    {
        using var client = new ImapClient();
        await client.ConnectAsync(account.Imap.Host, account.Imap.Port, SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(account.User, account.Password, ct);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct);

        var uids = await inbox.SearchAsync(SearchQuery.All, ct);
        var selected = uids.Reverse().Take(max).ToList();

        var flagsByUid = new Dictionary<UniqueId, MessageFlags>();
        if (selected.Count > 0)
            foreach (var s in await inbox.FetchAsync(selected, MessageSummaryItems.Flags, ct))
                flagsByUid[s.UniqueId] = s.Flags ?? MessageFlags.None;

        var items = new List<EmailItem>(selected.Count);
        foreach (var uid in selected)
        {
            ct.ThrowIfCancellationRequested();

            var msg = await inbox.GetMessageAsync(uid, ct);
            var body = ExtractBody(msg, 600);   // apercu court pour le contexte conversationnel

            var flags = flagsByUid.TryGetValue(uid, out var f) ? f : MessageFlags.None;
            items.Add(new EmailItem(
                Uid: uid,
                Seen: flags.HasFlag(MessageFlags.Seen),
                Answered: flags.HasFlag(MessageFlags.Answered),
                MessageId: string.IsNullOrWhiteSpace(msg.MessageId) ? uid.ToString() : msg.MessageId,
                From: msg.From.ToString(),
                Subject: string.IsNullOrWhiteSpace(msg.Subject) ? "(sans objet)" : msg.Subject,
                BodyPreview: body,
                Date: msg.Date,
                UnsubscribeHeader: msg.Headers["List-Unsubscribe"] ?? "",
                OneClickUnsubscribe: (msg.Headers["List-Unsubscribe-Post"] ?? "")
                    .Contains("One-Click", StringComparison.OrdinalIgnoreCase)));
        }

        await client.DisconnectAsync(true, ct);
        return items;
    }

    /// <summary>
    /// Mails de la boite de reception encore "a traiter" : non repondus, du plus recent au plus
    /// ancien. Envelope + drapeaux seulement (pas de corps) : sert au classement d'importance
    /// par le LLM, qui doit rester leger en tokens.
    /// </summary>
    public async Task<IReadOnlyList<EmailItem>> GetUnansweredInboxAsync(int max, CancellationToken ct = default)
    {
        using var client = new ImapClient();
        await client.ConnectAsync(account.Imap.Host, account.Imap.Port, SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(account.User, account.Password, ct);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct);

        var uids = await inbox.SearchAsync(SearchQuery.NotAnswered, ct);
        var selected = uids.Reverse().Take(max).ToList();

        var items = new List<EmailItem>(selected.Count);
        if (selected.Count > 0)
            foreach (var s in await inbox.FetchAsync(selected, MessageSummaryItems.Flags | MessageSummaryItems.Envelope, ct))
                items.Add(SummaryToItem(s));

        await client.DisconnectAsync(true, ct);
        return items.OrderByDescending(i => i.Date).ToList();
    }

    /// <summary>
    /// Recherche dans TOUT le compte ("Tous les messages" Gmail, donc aussi les archives) les
    /// mails dont l'expediteur ou l'objet contient le terme. Du plus recent au plus ancien.
    /// </summary>
    public async Task<IReadOnlyList<EmailItem>> SearchAllMailAsync(string query, int max, CancellationToken ct = default)
    {
        using var client = new ImapClient();
        await client.ConnectAsync(account.Imap.Host, account.Imap.Port, SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(account.User, account.Password, ct);

        // "Tous les messages" n'existe que sur Gmail ; un serveur sans SPECIAL-USE (OVH...)
        // leve NotSupportedException : on cherche alors dans la boite de reception seule.
        IMailFolder all;
        try { all = client.GetFolder(SpecialFolder.All) ?? client.Inbox; }
        catch (NotSupportedException) { all = client.Inbox; }
        await all.OpenAsync(FolderAccess.ReadOnly, ct);

        var q = SearchQuery.FromContains(query).Or(SearchQuery.SubjectContains(query));
        var uids = await all.SearchAsync(q, ct);
        var selected = uids.Reverse().Take(max).ToList();

        var items = new List<EmailItem>(selected.Count);
        if (selected.Count > 0)
            foreach (var s in await all.FetchAsync(selected, MessageSummaryItems.Flags | MessageSummaryItems.Envelope, ct))
                items.Add(SummaryToItem(s));

        await client.DisconnectAsync(true, ct);
        return items.OrderByDescending(i => i.Date).ToList();
    }

    /// <summary>
    /// Corps complet (tronque a maxChars) d'un mail de la boite, par UID. Sert a l'outil
    /// "lire_mail" de l'assistant conversationnel (les apercus du contexte sont courts).
    /// </summary>
    public async Task<string> GetBodyAsync(UniqueId uid, int maxChars, CancellationToken ct = default)
    {
        using var client = new ImapClient();
        await client.ConnectAsync(account.Imap.Host, account.Imap.Port, SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(account.User, account.Password, ct);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct);
        var msg = await inbox.GetMessageAsync(uid, ct);
        var body = ExtractBody(msg, maxChars);
        await client.DisconnectAsync(true, ct);

        return body;
    }

    /// <summary>Convertit un resume IMAP (envelope + flags, sans corps) en EmailItem.</summary>
    private static EmailItem SummaryToItem(IMessageSummary s)
    {
        var env = s.Envelope;
        return new EmailItem(
            Uid: s.UniqueId,
            Seen: s.Flags?.HasFlag(MessageFlags.Seen) ?? false,
            Answered: s.Flags?.HasFlag(MessageFlags.Answered) ?? false,
            MessageId: env?.MessageId ?? s.UniqueId.ToString(),
            From: env?.From?.ToString() ?? "",
            Subject: string.IsNullOrWhiteSpace(env?.Subject) ? "(sans objet)" : env!.Subject,
            BodyPreview: "",
            Date: env?.Date ?? DateTimeOffset.MinValue);
    }

    /// <summary>
    /// Pose le marqueur de suivi (keyword IMAP) sur les mails traites.
    /// Ils seront exclus des prochaines passes. N'affecte ni le statut lu/non lu, ni le contenu.
    /// </summary>
    public async Task MarkNotifiedAsync(IList<UniqueId> uids, CancellationToken ct = default)
    {
        if (uids.Count == 0) return;

        using var client = new ImapClient();
        await client.ConnectAsync(account.Imap.Host, account.Imap.Port, SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(account.User, account.Password, ct);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite, ct);
        await inbox.AddFlagsAsync(uids, MessageFlags.None,
            new HashSet<string> { account.Imap.NotifiedKeyword }, silent: true, ct);

        await client.DisconnectAsync(true, ct);
    }

    /// <summary>
    /// Marque les mails dont la notification est reportee (heures silencieuses). Ils sont
    /// ignores par les passes suivantes tant que la plage dure, puis repris UNE fois.
    /// </summary>
    public async Task MarkDeferredAsync(IList<UniqueId> uids, CancellationToken ct = default)
    {
        if (uids.Count == 0) return;

        using var client = new ImapClient();
        await client.ConnectAsync(account.Imap.Host, account.Imap.Port, SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(account.User, account.Password, ct);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite, ct);
        await inbox.AddFlagsAsync(uids, MessageFlags.None,
            new HashSet<string> { account.Imap.DeferredKeyword }, silent: true, ct);

        await client.DisconnectAsync(true, ct);
    }

    /// <summary>Retire le marqueur "notif reportee" des mails repris apres les heures silencieuses.</summary>
    public async Task ClearDeferredAsync(IList<UniqueId> uids, CancellationToken ct = default)
    {
        if (uids.Count == 0) return;

        using var client = new ImapClient();
        await client.ConnectAsync(account.Imap.Host, account.Imap.Port, SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(account.User, account.Password, ct);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite, ct);
        await inbox.RemoveFlagsAsync(uids, MessageFlags.None,
            new HashSet<string> { account.Imap.DeferredKeyword }, silent: true, ct);

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
        await client.ConnectAsync(account.Imap.Host, account.Imap.Port, SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(account.User, account.Password, ct);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite, ct);

        var root = client.GetFolder(client.PersonalNamespaces[0]);
        var target = await EnsureFolderPathAsync(root, folderPath, ct);

        // Marque comme lu avant le deplacement (le flag est conserve dans le dossier cible),
        // pour faire baisser le compteur de non-lus sur les mails inutiles.
        if (account.Imap.MarkMovedAsRead)
            await inbox.AddFlagsAsync(uids, MessageFlags.Seen, silent: true, ct);

        await inbox.MoveToAsync(uids, target, ct);
        await client.DisconnectAsync(true, ct);
    }

    /// <summary>
    /// Deplace les mails vers la CORBEILLE du serveur (sur Gmail : [Gmail]/Corbeille, recuperable
    /// 30 jours). Utilise pour les expediteurs auto-supprimes. Rien n'est efface definitivement.
    /// </summary>
    public async Task MoveToTrashAsync(IList<UniqueId> uids, CancellationToken ct = default)
    {
        if (uids.Count == 0) return;

        using var client = new ImapClient();
        await client.ConnectAsync(account.Imap.Host, account.Imap.Port, SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(account.User, account.Password, ct);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite, ct);

        var trash = await FindTrashAsync(client, account.Imap, ct)
            ?? throw new InvalidOperationException(
                "Dossier Corbeille introuvable sur le serveur IMAP (renseigne Imap:TrashFolder pour cette boite).");
        await inbox.MoveToAsync(uids, trash, ct);

        await client.DisconnectAsync(true, ct);
    }

    /// <summary>
    /// Corbeille du compte : dossier special (Gmail, serveurs SPECIAL-USE), sinon le nom configure
    /// (Imap:TrashFolder), sinon les noms usuels. Null si rien ne convient.
    /// </summary>
    public static async Task<IMailFolder?> FindTrashAsync(ImapClient client, ImapConfig imap, CancellationToken ct)
    {
        try
        {
            var special = client.GetFolder(SpecialFolder.Trash);
            if (special is not null) return special;
        }
        catch (NotSupportedException) { /* pas de SPECIAL-USE : on cherche par nom */ }

        var root = client.GetFolder(client.PersonalNamespaces[0]);
        var candidates = imap.TrashFolder.Length > 0
            ? [imap.TrashFolder]
            : new[] { "Trash", "Corbeille", "Deleted Messages", "Éléments supprimés", "Deleted Items" };
        foreach (var name in candidates)
        {
            try
            {
                var f = await root.GetSubfolderAsync(name, ct);
                if (f.Exists) return f;
            }
            catch (FolderNotFoundException) { }
        }
        return null;
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
            IMailFolder? child = null;
            try
            {
                child = await current.GetSubfolderAsync(name, ct);
                // Certains serveurs (Dovecot/OVH) renvoient un dossier "fantome" plutot qu'une
                // erreur pour un nom inconnu : on verifie qu'il existe vraiment avant de s'en servir.
                if (!child.Exists) child = null;
            }
            catch (FolderNotFoundException) { }
            child ??= await current.CreateAsync(name, isMessageFolder: true, ct);
            current = child;
        }
        return current;
    }
}
