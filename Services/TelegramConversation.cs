using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MailAgent.Configuration;
using MailAgent.Models;
using MimeKit;

namespace MailAgent.Services;

/// <summary>
/// Bot Telegram conversationnel : lit les messages entrants (getUpdates), determine l'intention
/// via le LLM configure (chat / repondre a un mail / envoyer un nouveau mail / valider / annuler)
/// et agit. Repondre a un mail ou en envoyer un nouveau passe TOUJOURS par une validation explicite. Sans etat local : l'offset Telegram est
/// confirme cote serveur, et le brouillon en attente vit dans un dossier IMAP (cf. EmailSender).
/// </summary>
public sealed class TelegramConversation(AgentConfig config, AccountConfig account, HttpClient http, ILlmClient llm, EmailReader reader, EmailSender sender, BlockListStore blocklist, GoogleCalendar calendar)
{
    private readonly UnsubscribeService _unsubscribe = new(account, sender, http);
    private readonly AssistantToolLoop _tools = new(reader, llm);

    private const string RouterPrompt =
        """
        Tu es le routeur d'un assistant mail personnel accessible sur Telegram. A partir du message
        de l'utilisateur et de la liste NUMEROTEE de ses derniers mails, determine l'INTENTION et
        reponds en JSON STRICT :
        {"intent":"...","target":N,"query":"...","reply":"...","answer":"...","subject":"...","start":"...","end":"..."}
        (start/end ne servent qu'a l'intention "agenda", subject qu'a "compose" ; mets "" sinon.)

        intent vaut EXACTEMENT l'une de ces valeurs :
        - "reply"  : l'utilisateur veut REPONDRE a un mail (ex. "reponds au syndic que je serai present"),
                     MEME si sa demande combine autre chose (ex. "resume-moi le mail de X ET propose une
                     reponse" -> reply). target = le NUMERO du mail concerne dans la liste. reply = le
                     texte COMPLET et poli de la reponse a envoyer AU NOM DE L'UTILISATEUR (c'est LUI qui
                     ecrit A l'expediteur du mail ; ne redige JAMAIS comme si tu etais l'expediteur, ne
                     signe jamais du nom de l'expediteur), avec salutation et formule de politesse,
                     en francais. answer = "".
        - "compose": l'utilisateur veut ENVOYER un NOUVEAU mail (PAS une reponse a un mail recu)
                     a un destinataire qu'il designe (ex. "envoie un mail a jean@exemple.fr pour lui
                     dire que...", "ecris a Paul pour le remercier"). query = l'adresse email du
                     destinataire si elle est donnee (recopie-la EXACTEMENT), sinon le nom cite.
                     subject = un objet court et pertinent en francais. reply = le texte COMPLET et
                     poli du mail, redige AU NOM DE L'UTILISATEUR, avec salutation et formule de
                     politesse, en francais. target=0, answer="".
        - "revise" : un BROUILLON EST EN ATTENTE (section "Brouillon en attente" fournie) et
                     l'utilisateur veut le MODIFIER (ex. "plus court", "plus formel", "ajoute que je
                     serai en retard", "enleve la derniere phrase", "change l'objet", "tutoie-le").
                     reply = le texte COMPLET du brouillon REECRIT en appliquant la demande : repars
                     du brouillon fourni, PAS d'un mail de la liste. subject = le nouvel objet si
                     l'utilisateur le change, sinon "". target=0, answer="".
        - "send"   : l'utilisateur VALIDE l'envoi en attente (ex. "oui", "envoie", "valide",
                     "ok envoie", "c'est bon"). target=0, reply="", answer="".
        - "cancel" : l'utilisateur ANNULE (ex. "annule", "non laisse tomber"). target=0, reply="", answer="".
        - "unsub"  : l'utilisateur veut se DESABONNER d'une newsletter / liste de diffusion
                     (ex. "desabonne-moi de Carrefour", "je ne veux plus recevoir ces mails").
                     target = le NUMERO du mail concerne dans la liste. reply="", answer="".
        - "block"  : l'utilisateur veut que l'agent BLOQUE / IGNORE un expediteur : ses mails
                     partiront desormais directement a la corbeille, sans analyse ni notification
                     (ex. "bloque Temu", "ignore les mails de X", "supprime automatiquement ces mails",
                     "je ne veux plus voir ces mails"). target = le NUMERO du mail concerne si le
                     message y fait reference, sinon 0. query = l'adresse, le domaine ou le nom de
                     l'expediteur s'il est cite, sinon "". reply="", answer="".
        - "unblock": l'utilisateur veut DEBLOQUER un expediteur precedemment bloque
                     (ex. "debloque Temu", "ne bloque plus X"). query = l'expediteur.
                     target=0, reply="", answer="".
        - "blocklist" : l'utilisateur demande la liste des expediteurs bloques (ex. "qui est
                     bloque ?", "montre la liste noire"). target=0, query="", reply="", answer="".
        - "agenda" : l'utilisateur veut AJOUTER un evenement ou un rappel a son agenda
                     (ex. "ajoute un rappel : appeler Emma le 31 aout a 10h45", "mets l'AG du
                     30 juin dans mon agenda"). query = l'intitule court de l'evenement
                     (ex. "Appeler Emma Lourau"). start = la date/heure au format ISO 8601
                     ("AAAA-MM-JJTHH:MM:SS", ou "AAAA-MM-JJ" si l'heure est inconnue), calculee
                     par rapport a la date du jour fournie. end = fin ISO 8601 ou "" si inconnue.
                     target=0, reply="", answer="".
        - "important" : l'utilisateur demande ses mails les plus IMPORTANTS / a traiter
                     (ex. "quels mails dois-je traiter ?", "mes 5 mails importants", "je dois faire quoi ?").
                     target = le nombre demande (0 si non precise). query="", reply="", answer="".
        - "search" : l'utilisateur veut RETROUVER des mails d'une personne ou sur un sujet
                     (ex. "retrouve le mail de Mme Dupont", "les mails de la CAF").
                     query = les termes de recherche (nom, adresse ou mots de l'objet, PAS de
                     mots generiques comme "mail de"). target=0, reply="", answer="".
        - "purge"  : l'utilisateur veut EFFACER les messages de cette conversation Telegram
                     (ex. "efface nos messages", "nettoie la conversation"). target=0, query="", reply="", answer="".
        - "chat"   : tout le reste (question, resume, demande d'info). answer = ta reponse en francais
                     (resume / reponse), en t'appuyant sur le contenu des mails. target=0, reply="".

        S'il y a un brouillon en attente et que le message porte sur CE brouillon (modification,
        reformulation, correction), c'est "revise" : jamais "reply" ni "compose" dans ce cas.
        Si des PIECES JOINTES sont fournies (liste en contexte), elles seront ajoutees
        automatiquement au mail : mentionne-les naturellement dans le texte redige
        (ex. "Vous trouverez ci-joint ..."), sans inventer leur contenu.
        En cas de DOUTE, choisis "chat" : ne declenche JAMAIS un envoi par erreur.
        Reponds UNIQUEMENT le JSON, sans texte ni balise autour.
        """;

    /// <summary>Lit les messages Telegram en attente et agit. Ne fait rien s'il n'y en a pas.</summary>
    public async Task RunAsync(IReadOnlyList<EmailItem> recentImportant, CancellationToken ct = default)
    {
        var updates = await GetUpdatesAsync(ct);
        if (updates.Count == 0) return;

        // 30 mails de contexte : assez pour "reponds a X" sans exploser le budget tokens des
        // tiers gratuits (limite TPM Groq notamment).
        var recent = await reader.GetRecentInboxWithBodyAsync(max: 30, ct);
        var context = BuildContext(recentImportant, recent);

        long maxUpdateId = 0;
        foreach (var u in updates)
        {
            maxUpdateId = Math.Max(maxUpdateId, u.UpdateId);
            // Un message sans texte ni fichier (sticker, vocal, contact...) est ignore.
            if (u.ChatId.ToString() != config.Telegram.ChatId || (string.IsNullOrWhiteSpace(u.Text) && u.File is null)) continue;

            Console.WriteLine($"  [TELEGRAM] recu : {u.Text}{(u.File is null ? "" : $" [fichier : {u.File.FileName}]")}");
            try
            {
                await HandleAsync(u, context, recent, ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    [ERREUR] {ex.Message}");
                try { await SendTextAsync($"Desole, une erreur est survenue : {ex.Message}", ct); } catch { }
            }
        }

        await ConfirmUpdatesAsync(maxUpdateId + 1, ct);
    }

    private async Task HandleAsync(Incoming u, string context, IReadOnlyList<EmailItem> recent, CancellationToken ct)
    {
        var text = u.Text;
        var messageId = u.MessageId;

        // Le brouillon en attente fait partie du contexte du routeur : « oui » veut dire
        // « envoie », « plus court » veut dire « modifie ce brouillon » (et pas une nouvelle
        // reponse a un autre mail). Relu a chaque message : il change au fil de la conversation.
        var (found, expired) = await GetValidPendingAsync(ct);
        var pending = expired ? null : found;   // expire = deja annule (avec ses pieces jointes)

        // Pieces jointes : celles deja recues et en attente, plus celle du message courant.
        // Un fichier est toujours stocke des sa reception : il survit a la fin de la passe et
        // sera fusionne dans le prochain brouillon (reponse, nouveau mail ou retouche). Meme
        // duree de vie que le brouillon : au-dela, un fichier oublie n'est plus joint.
        var attachments = new List<PendingAttachment>(
            await sender.GetAttachmentsAsync(TimeSpan.FromHours(config.Smtp.PendingTtlHours), ct));
        if (u.File is not null)
        {
            PendingAttachment received;
            try { received = await DownloadAsync(u.File, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                await SendTextAsync($"❌ Je n'ai pas pu recuperer le fichier « {u.File.FileName} » : {ex.Message}", ct);
                return;
            }
            await sender.SaveAttachmentAsync(received, ct);
            attachments.Add(received);
            Console.WriteLine($"    -> piece jointe stockee : {received.FileName} ({received.SizeLabel}).");

            // Fichier envoye SANS consigne : on l'ajoute au brouillon en cours s'il y en a un,
            // sinon on le garde pour le prochain et on le dit (plutot que d'ignorer en silence).
            if (string.IsNullOrWhiteSpace(text))
            {
                if (pending is not null) await AttachToPendingAsync(pending, attachments, ct);
                else await SendTextAsync(
                    $"📎 Fichier recu : {received.FileName} ({received.SizeLabel}). Dis-moi a quel mail repondre "
                    + "ou a qui l'envoyer, je le joindrai au brouillon.", ct);
                return;
            }
        }

        var routerContext = context;
        if (pending is not null) routerContext += "\n\n" + DescribePending(pending);
        if (attachments.Count > 0)
            routerContext += "\n\nPiece(s) jointe(s) fournie(s) par l'utilisateur, a joindre au prochain mail : "
                + string.Join(", ", attachments.Select(a => $"{a.FileName} ({a.SizeLabel})"));

        var route = await RouteAsync(text, routerContext, ct);
        var consumes = route.Intent is "reply" or "compose" or "revise";
        switch (route.Intent)
        {
            case "send":
                await HandleSendAsync(found, expired, ct);
                break;
            case "revise":
                await HandleReviseAsync(route, found, expired, attachments, ct);
                break;
            case "cancel":
                await sender.DeletePendingAsync(ct);
                await SendTextAsync("Ok, j'annule : rien n'a ete envoye.", ct);
                Console.WriteLine("    -> brouillon annule.");
                break;
            case "reply":
                await HandleReplyAsync(route, recent, attachments, ct);
                break;
            case "compose":
                await HandleComposeAsync(route, recent, attachments, ct);
                break;
            case "unsub":
                await HandleUnsubscribeAsync(route, recent, ct);
                break;
            case "block":
                await HandleBlockAsync(route, recent, ct);
                break;
            case "unblock":
                await HandleUnblockAsync(route, ct);
                break;
            case "blocklist":
                await HandleBlocklistAsync(ct);
                break;
            case "agenda":
                await HandleAgendaAsync(route, ct);
                break;
            case "important":
                await HandleImportantAsync(route, ct);
                break;
            case "search":
                await HandleSearchAsync(route, ct);
                break;
            case "purge":
                await HandlePurgeAsync(messageId, ct);
                break;
            default:
                // Hybride : les questions libres passent par la boucle d'outils (lecture seule),
                // qui peut consulter la boite (recherche, lecture d'un mail complet, apercu)
                // avant de repondre. La reponse directe du routeur sert de secours si la boucle
                // echoue en cours de route (ex. quota LLM epuise).
                string answer;
                try
                {
                    answer = await _tools.AnswerAsync(text, context, recent, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Console.WriteLine($"    [Assistant] boucle d'outils en echec ({ex.Message}) : repli sur la reponse directe.");
                    answer = route.Answer;
                }
                await SendTextAsync(answer.Length > 0 ? answer : "(pas de reponse)", ct);
                Console.WriteLine("    -> reponse chat envoyee.");
                break;
        }

        // Fichier joint a une demande qui ne produit pas de brouillon (question, recherche...) :
        // il reste en attente, on le dit pour que l'utilisateur sache qu'il n'est pas perdu.
        if (u.File is not null && !consumes && route.Intent is not "cancel")
            await SendTextAsync($"📎 J'ai garde « {u.File.FileName} » de cote : je le joindrai au prochain mail que tu me feras ecrire.", ct);
    }

    /// <summary>
    /// Fichier recu alors qu'un brouillon attend deja validation : on le joint a ce brouillon
    /// (reconstruit a l'identique avec les pieces jointes) et on represente la proposition.
    /// </summary>
    private async Task AttachToPendingAsync(MimeMessage pending, IReadOnlyList<PendingAttachment> attachments, CancellationToken ct)
    {
        var msg = CloneDraft(pending, pending.TextBody ?? "", pending.Subject, attachments);
        await sender.SavePendingAsync(msg, ct);
        await SendTextAsync(
            $"📎 Piece(s) jointe(s) ajoutee(s) au brouillon pour {msg.To}\nObjet : {msg.Subject}\n{DescribeAttachments(msg)}\n\n"
            + "Reponds OUI pour envoyer, ou dis-moi quoi changer.", ct);
        Console.WriteLine($"    -> {attachments.Count} piece(s) jointe(s) ajoutee(s) au brouillon (a {msg.To}).");
    }

    /// <summary>
    /// Nouveau brouillon a partir d'un brouillon existant : memes destinataires et fil de
    /// discussion, texte et objet donnes, pieces jointes existantes conservees + nouvelles.
    /// </summary>
    private MimeMessage CloneDraft(MimeMessage pending, string text, string subject, IEnumerable<PendingAttachment> added)
    {
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(account.User));
        msg.To.AddRange(pending.To);
        msg.Cc.AddRange(pending.Cc);
        msg.Subject = subject;
        if (!string.IsNullOrEmpty(pending.InReplyTo))
        {
            msg.InReplyTo = pending.InReplyTo;
            msg.References.AddRange(pending.References);
        }
        msg.Body = BuildBody(text, pending.Attachments, added);
        return msg;
    }

    /// <summary>Corps texte + pieces jointes (entites deja presentes dans un brouillon, et nouvelles).</summary>
    private static MimeEntity BuildBody(string text, IEnumerable<MimeEntity> existing, IEnumerable<PendingAttachment> added)
    {
        var builder = new BodyBuilder { TextBody = text };
        foreach (var e in existing) builder.Attachments.Add(e);
        foreach (var a in added) builder.Attachments.Add(a.FileName, a.Data, EmailSender.ParseContentType(a.MimeType));
        return builder.ToMessageBody();
    }

    /// <summary>Ligne « 📎 ... » listant les pieces jointes d'un brouillon, ou "" s'il n'y en a pas.</summary>
    private static string DescribeAttachments(MimeMessage msg)
    {
        var names = msg.Attachments.OfType<MimePart>().Select(p => p.FileName ?? "fichier").ToList();
        return names.Count == 0 ? "" : $"📎 Piece(s) jointe(s) : {string.Join(", ", names)}";
    }

    /// <summary>Telecharge un fichier recu sur Telegram (getFile + telechargement), borne en taille.</summary>
    private async Task<PendingAttachment> DownloadAsync(TelegramFile file, CancellationToken ct)
    {
        const long maxBytes = 20L * 1024 * 1024;   // limite de l'API Bot Telegram pour getFile
        if (file.Size > maxBytes)
            throw new InvalidOperationException($"fichier trop volumineux ({file.Size / 1048576.0:0.0} Mo), Telegram limite a 20 Mo pour les bots.");

        var token = config.Telegram.BotToken;
        using var resp = await http.GetAsync($"https://api.telegram.org/bot{token}/getFile?file_id={Uri.EscapeDataString(file.FileId)}", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Telegram a refuse getFile (HTTP {(int)resp.StatusCode}) : {body}");
        using var doc = JsonDocument.Parse(body);
        var path = doc.RootElement.GetProperty("result").GetProperty("file_path").GetString()
            ?? throw new InvalidOperationException("Telegram n'a pas renvoye de chemin de fichier.");

        var data = await http.GetByteArrayAsync($"https://api.telegram.org/file/bot{token}/{path}", ct);
        return new PendingAttachment(file.FileName, file.MimeType, data);
    }

    /// <summary>
    /// "Mes N mails importants" : classe les non-repondus de la boite (bien au-dela des 30 du
    /// contexte) via le LLM et renvoie une liste priorisee avec quoi faire.
    /// </summary>
    private async Task HandleImportantAsync(Route route, CancellationToken ct)
    {
        var n = route.Target is > 0 and <= 20 ? route.Target : 5;
        var candidates = await reader.GetUnansweredInboxAsync(max: 60, ct);
        if (candidates.Count == 0)
        {
            await SendTextAsync("Rien a traiter : aucun mail non repondu en boite. 🎉", ct);
            return;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < candidates.Count; i++)
            sb.AppendLine($"{i + 1}. {candidates[i].Date:dd/MM} | {(candidates[i].Seen ? "lu" : "NON-LU")} | {candidates[i].From} | {candidates[i].Subject}");

        var prompt =
            $"""
            Tu priorises la boite mail de l'utilisateur. Voici ses mails non repondus, du plus
            recent au plus ancien. Choisis les {n} PLUS IMPORTANTS a traiter (actions attendues,
            echeances, personnel/administratif avant marketing) et reponds en francais, en liste
            numerotee courte : expediteur — objet — ce qu'il faut faire. Rien d'autre.
            """;
        var answer = await llm.CompleteAsync(prompt, sb.ToString(), maxTokens: 1000, ct);
        await SendTextAsync(answer, ct);
        Console.WriteLine($"    -> top {n} importants envoye ({candidates.Count} candidats).");
    }

    /// <summary>"Retrouve le mail de X" : recherche IMAP (expediteur/objet) sur tout le compte, archives comprises.</summary>
    private async Task HandleSearchAsync(Route route, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(route.Query))
        {
            await SendTextAsync("Dis-moi qui ou quoi chercher (nom, adresse ou mots de l'objet).", ct);
            return;
        }

        var found = await reader.SearchAllMailAsync(route.Query.Trim(), max: 10, ct);
        if (found.Count == 0)
        {
            await SendTextAsync($"Aucun mail trouve pour « {route.Query} » (recherche sur l'expediteur et l'objet).", ct);
            return;
        }

        var sb = new StringBuilder($"🔎 {found.Count} resultat(s) pour « {route.Query} » :\n");
        foreach (var e in found)
            sb.AppendLine($"\n• {e.Date:dd/MM/yyyy} — {e.From}\n  {e.Subject}");
        await SendTextAsync(sb.ToString(), ct);
        Console.WriteLine($"    -> recherche « {route.Query} » : {found.Count} resultat(s).");
    }

    /// <summary>
    /// Efface les messages recents de la conversation. L'API Telegram n'expose pas l'historique :
    /// on balaie les identifiants (sequentiels par chat) en dessous du message declencheur.
    /// Limite Telegram : seuls les messages de moins de 48h sont supprimables.
    /// </summary>
    private async Task HandlePurgeAsync(long fromMessageId, CancellationToken ct)
    {
        var url = $"https://api.telegram.org/bot{config.Telegram.BotToken}/deleteMessage";
        var deleted = 0;
        for (var id = fromMessageId; id > Math.Max(0, fromMessageId - 200); id--)
        {
            var payload = new { chat_id = config.Telegram.ChatId, message_id = id };
            using var resp = await http.PostAsJsonAsync(url, payload, ct);
            if (resp.IsSuccessStatusCode) deleted++;
        }
        await SendTextAsync($"🧹 {deleted} message(s) efface(s). (Telegram ne permet d'effacer que les messages de moins de 48h.)", ct);
        Console.WriteLine($"    -> purge : {deleted} message(s) efface(s).");
    }

    private async Task HandleReplyAsync(Route route, IReadOnlyList<EmailItem> recent, IReadOnlyList<PendingAttachment> attachments, CancellationToken ct)
    {
        if (route.Target < 1 || route.Target > recent.Count || route.Reply.Length == 0)
        {
            await SendTextAsync("Je n'ai pas reussi a identifier le mail auquel repondre (il n'est peut-etre pas dans les 30 derniers). Precise l'expediteur ?", ct);
            return;
        }

        var original = recent[route.Target - 1];
        var msg = BuildReply(original, route.Reply, attachments, out var toAddress);
        if (toAddress is null)
        {
            await SendTextAsync($"Impossible de determiner l'adresse de reponse pour \"{original.Subject}\".", ct);
            return;
        }

        await sender.SavePendingAsync(msg, ct);

        var noReplyWarn =
            toAddress.Contains("no-reply", StringComparison.OrdinalIgnoreCase) ||
            toAddress.Contains("noreply", StringComparison.OrdinalIgnoreCase)
                ? "\n\n⚠️ L'adresse ressemble a un no-reply : la reponse sera peut-etre ignoree."
                : "";

        await SendTextAsync(
            $"✉️ Proposition de reponse a {toAddress}\nObjet : {msg.Subject}\n{DescribeAttachments(msg)}\n\n{route.Reply}\n\n" +
            $"Reponds OUI pour envoyer, ou dis-moi quoi changer.{noReplyWarn}", ct);
        Console.WriteLine($"    -> proposition de reponse stockee (a {toAddress}, {attachments.Count} piece(s) jointe(s)).");
    }

    /// <summary>
    /// « Envoie un mail a X » : compose un NOUVEAU mail (pas une reponse). Comme pour reply,
    /// rien ne part sans validation : le brouillon est stocke et attend un OUI ("send").
    /// Si le destinataire est donne par son nom, on tente de retrouver son adresse parmi
    /// les mails recents ; sinon on demande l'adresse.
    /// </summary>
    private async Task HandleComposeAsync(Route route, IReadOnlyList<EmailItem> recent, IReadOnlyList<PendingAttachment> attachments, CancellationToken ct)
    {
        if (route.Reply.Length == 0)
        {
            await SendTextAsync("Dis-moi quoi ecrire dans ce mail (destinataire + message).", ct);
            return;
        }

        var toAddress = ResolveAddress(route.Query, recent);
        if (toAddress is null)
        {
            await SendTextAsync(
                route.Query.Length > 0
                    ? $"Je n'ai pas d'adresse email pour « {route.Query} » (introuvable dans les mails recents). Donne-moi son adresse ?"
                    : "A quelle adresse email dois-je envoyer ce mail ?", ct);
            return;
        }

        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(account.User));
        msg.To.Add(toAddress);
        msg.Subject = route.Subject.Length > 0 ? route.Subject : "(sans objet)";
        msg.Body = BuildBody(route.Reply, [], attachments);

        await sender.SavePendingAsync(msg, ct);
        await SendTextAsync(
            $"📨 Nouveau mail pour {toAddress.Address}\nObjet : {msg.Subject}\n{DescribeAttachments(msg)}\n\n{route.Reply}\n\n" +
            "Reponds OUI pour envoyer, ou dis-moi quoi changer.", ct);
        Console.WriteLine($"    -> nouveau mail propose (a {toAddress.Address}, {attachments.Count} piece(s) jointe(s)).");
    }

    /// <summary>
    /// Resout le destinataire d'un nouveau mail : adresse explicite si le routeur en a extrait
    /// une, sinon recherche du nom parmi les expediteurs des mails recents. Null si introuvable.
    /// </summary>
    private static MailboxAddress? ResolveAddress(string query, IReadOnlyList<EmailItem> recent)
    {
        query = query.Trim().TrimEnd('.', ',', ';');
        if (query.Length == 0) return null;

        if (query.Contains('@') && MailboxAddress.TryParse(query, out var parsed))
            return parsed;

        foreach (var e in recent)
        {
            if (!e.From.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            var address = SenderAddress(e.From);
            if (MailboxAddress.TryParse(address, out var fromRecent)) return fromRecent;
        }
        return null;
    }

    /// <summary>
    /// « Bloque X » : ajoute l'expediteur a la liste noire persistee en IMAP (BlockListStore).
    /// Des la prochaine passe de tri, ses mails partent directement a la corbeille, sans
    /// analyse ni notification. Si le message designe un mail de la liste, on bloque
    /// l'ADRESSE exacte de son expediteur (plus sur qu'un nom approximatif).
    /// </summary>
    private async Task HandleBlockAsync(Route route, IReadOnlyList<EmailItem> recent, CancellationToken ct)
    {
        var fragment = route.Target >= 1 && route.Target <= recent.Count
            ? SenderAddress(recent[route.Target - 1].From)
            : route.Query.Trim();

        if (fragment.Length < 3)
        {
            await SendTextAsync("Dis-moi qui bloquer (adresse, domaine ou nom d'expediteur).", ct);
            return;
        }

        var list = await blocklist.AddAsync(fragment, ct);
        await SendTextAsync(
            $"🚫 « {fragment} » est bloque : ses prochains mails iront directement a la corbeille, "
            + $"sans notification (corbeille recuperable 30 jours).\nDis « debloque {fragment} » pour annuler.\n\n"
            + FormatBlocklist(account.Classifier.BlockedSenders, list), ct);
        Console.WriteLine($"    -> expediteur bloque : {fragment}");
    }

    /// <summary>« Debloque X » : retire l'expediteur de la liste noire persistee (pas de la config).</summary>
    private async Task HandleUnblockAsync(Route route, CancellationToken ct)
    {
        var fragment = route.Query.Trim();
        if (fragment.Length == 0)
        {
            await SendTextAsync("Dis-moi qui debloquer (adresse ou domaine).", ct);
            return;
        }

        var (removed, list) = await blocklist.RemoveAsync(fragment, ct);
        if (removed)
        {
            await SendTextAsync($"✅ « {fragment} » n'est plus bloque.\n\n"
                + FormatBlocklist(account.Classifier.BlockedSenders, list), ct);
            Console.WriteLine($"    -> expediteur debloque : {fragment}");
        }
        else if (account.Classifier.BlockedSenders.Any(b =>
                     b.Contains(fragment, StringComparison.OrdinalIgnoreCase)
                     || fragment.Contains(b, StringComparison.OrdinalIgnoreCase)))
        {
            await SendTextAsync(
                $"« {fragment} » est bloque par la CONFIGURATION (BlockedSenders dans appsettings.json) : "
                + "je ne peux pas le retirer d'ici, il faut editer le fichier.", ct);
        }
        else
        {
            await SendTextAsync($"« {fragment} » n'etait pas dans la liste des expediteurs bloques.", ct);
        }
    }

    /// <summary>
    /// « Ajoute un rappel a mon agenda » : cree l'evenement dans le Google Agenda de
    /// l'utilisateur (intitule + date extraits par le routeur). Necessite les secrets GOOGLE_*.
    /// </summary>
    private async Task HandleAgendaAsync(Route route, CancellationToken ct)
    {
        if (!calendar.IsConfigured)
        {
            await SendTextAsync(
                "L'agenda Google n'est pas configure sur ce serveur : il manque les secrets "
                + "GOOGLE_CLIENT_ID / GOOGLE_CLIENT_SECRET / GOOGLE_REFRESH_TOKEN.", ct);
            return;
        }
        if (route.Query.Length == 0 || route.Start.Length == 0)
        {
            await SendTextAsync("Precise l'intitule et la date/heure du rappel (ex. « appeler Emma le 31 aout a 10h45 »).", ct);
            return;
        }

        var evt = new EventInfo(route.Query, route.Start, route.End, "");
        var link = await calendar.CreateEventAsync(evt, "Ajoute depuis Telegram a ta demande.", ct);
        await SendTextAsync(link is null
            ? $"❌ Je n'ai pas reussi a creer l'evenement « {route.Query} » ({route.Start})."
            : $"📅 Ajoute a ton agenda : {route.Query} ({route.Start}).", ct);
        Console.WriteLine($"    -> agenda : {(link is null ? "echec" : "evenement cree")} ({route.Query}, {route.Start}).");
    }

    /// <summary>« Qui est bloque ? » : liste noire complete (Telegram + configuration).</summary>
    private async Task HandleBlocklistAsync(CancellationToken ct)
    {
        var list = await blocklist.GetAsync(ct);
        await SendTextAsync(FormatBlocklist(account.Classifier.BlockedSenders, list), ct);
        Console.WriteLine($"    -> liste des bloques envoyee ({list.Count} via Telegram).");
    }

    private static string FormatBlocklist(string[] fromConfig, IReadOnlyList<string> fromTelegram)
    {
        if (fromConfig.Length == 0 && fromTelegram.Count == 0)
            return "Aucun expediteur bloque pour le moment.";

        var sb = new StringBuilder("Expediteurs bloques (direct corbeille) :");
        foreach (var b in fromTelegram) sb.Append("\n• ").Append(b);
        foreach (var b in fromConfig) sb.Append("\n• ").Append(b).Append("  (config)");
        return sb.ToString();
    }

    /// <summary>Extrait l'adresse pure d'un champ From (« Nom &lt;a@b.c&gt; » -> « a@b.c »).</summary>
    private static string SenderAddress(string from)
    {
        try
        {
            foreach (var mb in InternetAddressList.Parse(from).Mailboxes)
                return mb.Address;
        }
        catch (ParseException) { }
        return from.Trim();
    }

    private async Task HandleUnsubscribeAsync(Route route, IReadOnlyList<EmailItem> recent, CancellationToken ct)
    {
        if (route.Target < 1 || route.Target > recent.Count)
        {
            await SendTextAsync("Je n'ai pas identifie le mail dont tu veux te desabonner (il n'est peut-etre pas dans les 30 derniers). Precise l'expediteur ?", ct);
            return;
        }

        var email = recent[route.Target - 1];
        var outcome = await _unsubscribe.TryUnsubscribeAsync(email, ct);
        await SendTextAsync(outcome, ct);
        Console.WriteLine($"    -> desabonnement \"{email.Subject}\" : {outcome}");
    }

    private async Task HandleSendAsync(MimeMessage? pending, bool expired, CancellationToken ct)
    {
        if (expired && pending is not null)
        {
            await SendTextAsync(ExpiredMessage(pending), ct);
            return;
        }
        if (pending is null)
        {
            await SendTextAsync("Il n'y a aucun mail en attente a envoyer.", ct);
            return;
        }

        await sender.SendAsync(pending, ct);
        await sender.DeletePendingAsync(ct);
        var to = pending.To.ToString();
        await SendTextAsync($"✅ Envoye a {to} : « {pending.Subject} ».", ct);
        Console.WriteLine($"    -> mail envoye a {to}.");
    }

    /// <summary>
    /// « Plus court », « ajoute que... » : remplace le brouillon en attente par sa version
    /// reecrite (meme destinataire, meme fil de discussion), toujours soumise a validation.
    /// </summary>
    private async Task HandleReviseAsync(Route route, MimeMessage? pending, bool expired, IReadOnlyList<PendingAttachment> attachments, CancellationToken ct)
    {
        if (expired && pending is not null)
        {
            await SendTextAsync(ExpiredMessage(pending), ct);
            return;
        }
        if (pending is null)
        {
            await SendTextAsync("Il n'y a aucun brouillon en attente a modifier. Dis-moi a qui repondre ou quoi envoyer.", ct);
            return;
        }
        if (route.Reply.Length == 0)
        {
            await SendTextAsync("Dis-moi ce que tu veux changer dans le brouillon (ton, longueur, contenu, objet).", ct);
            return;
        }

        // Les pieces jointes deja dans le brouillon sont conservees, les nouvelles ajoutees.
        var msg = CloneDraft(pending, route.Reply, route.Subject.Length > 0 ? route.Subject : pending.Subject, attachments);

        await sender.SavePendingAsync(msg, ct);
        await SendTextAsync(
            $"✏️ Brouillon mis a jour pour {msg.To}\nObjet : {msg.Subject}\n{DescribeAttachments(msg)}\n\n{route.Reply}\n\n" +
            "Reponds OUI pour envoyer, ou dis-moi quoi changer.", ct);
        Console.WriteLine($"    -> brouillon revise (a {msg.To}).");
    }

    /// <summary>
    /// Brouillon en attente encore valide. Un brouillon plus vieux que Smtp:PendingTtlHours est
    /// annule (corbeille) et signale Expired=true : un « oui » tardif, a propos d'autre chose,
    /// ne doit jamais expedier un mail oublie.
    /// </summary>
    private async Task<(MimeMessage? Pending, bool Expired)> GetValidPendingAsync(CancellationToken ct)
    {
        var pending = await sender.GetPendingAsync(ct);
        if (pending is null) return (null, false);

        var age = DateTimeOffset.Now - pending.Date;
        if (age <= TimeSpan.FromHours(config.Smtp.PendingTtlHours)) return (pending, false);

        await sender.DeletePendingAsync(ct);
        Console.WriteLine($"    -> brouillon expire ({age.TotalHours:0} h, a {pending.To}) : annule.");
        return (pending, true);
    }

    private string ExpiredMessage(MimeMessage pending) =>
        $"⏳ Le brouillon pour {pending.To} (« {pending.Subject} ») datait de plus de "
        + $"{config.Smtp.PendingTtlHours} h : je l'ai annule par securite, rien n'a ete envoye. "
        + "Redemande-le-moi si tu veux toujours l'envoyer.";

    /// <summary>Section de contexte decrivant le brouillon en attente, pour le routeur.</summary>
    private static string DescribePending(MimeMessage pending) =>
        $"--- Brouillon EN ATTENTE de validation (destinataire : {pending.To} | objet : {pending.Subject}"
        + (DescribeAttachments(pending) is { Length: > 0 } pj ? $" | {pj}" : "") + ") ---\n"
        + (pending.TextBody ?? "").Trim()
        + "\n--- fin du brouillon ---";

    /// <summary>Construit la reponse (MimeMessage) au mail d'origine, avec threading (Re: + In-Reply-To).</summary>
    private MimeMessage BuildReply(EmailItem original, string replyText, IEnumerable<PendingAttachment> attachments, out string? toAddress)
    {
        toAddress = null;
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(account.User));

        foreach (var mb in InternetAddressList.Parse(original.From).Mailboxes)
        {
            msg.To.Add(mb);
            toAddress = mb.Address;
            break;
        }

        msg.Subject = original.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
            ? original.Subject
            : "Re: " + original.Subject;

        if (original.MessageId.Contains('@'))
        {
            msg.InReplyTo = original.MessageId;
            msg.References.Add(original.MessageId);
        }

        msg.Body = BuildBody(replyText, [], attachments);
        return msg;
    }

    private async Task<Route> RouteAsync(string userMessage, string context, CancellationToken ct)
    {
        // La date du jour permet au routeur de resoudre les dates relatives ("demain",
        // "le 31 aout") en ISO 8601 pour l'intention agenda.
        var userContent = $"Nous sommes le {DateTime.Now:yyyy-MM-dd} ({DateTime.Now:dddd}).\n"
            + $"Mails recents (numerotes) :\n{context}\n\nMessage de l'utilisateur :\n{userMessage}";
        var raw = ExtractJson(await llm.CompleteAsync(RouterPrompt, userContent, maxTokens: 1000, ct));

        try
        {
            var dto = JsonSerializer.Deserialize<RouteDto>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var intent = dto?.Intent?.Trim().ToLowerInvariant() ?? "chat";
            return new Route(intent, dto?.Target ?? 0, dto?.Query?.Trim() ?? "", dto?.Reply?.Trim() ?? "",
                dto?.Answer?.Trim() ?? "", dto?.Subject?.Trim() ?? "", dto?.Start?.Trim() ?? "", dto?.End?.Trim() ?? "");
        }
        catch (JsonException)
        {
            // En cas de doute, on ne fait jamais d'action sensible : on retombe sur "chat".
            return new Route("chat", 0, "", "", "Je n'ai pas bien compris, peux-tu reformuler ?", "", "", "");
        }
    }

    private static string BuildContext(IReadOnlyList<EmailItem> recentImportant, IReadOnlyList<EmailItem> recent)
    {
        var sb = new StringBuilder();

        if (recentImportant.Count > 0)
        {
            sb.AppendLine("Mails importants detectes a la derniere passe :");
            foreach (var e in recentImportant.Take(10))
                sb.AppendLine($"- De {e.From} | {e.Subject}");
            sb.AppendLine();
        }

        if (recent.Count > 0)
        {
            sb.AppendLine("Derniers mails recus (numerotes, avec apercu du contenu) :");
            for (var i = 0; i < recent.Count; i++)
            {
                var e = recent[i];
                sb.AppendLine($"--- Mail #{i + 1} | De: {e.From} | Objet: {e.Subject} | {(e.Seen ? "lu" : "non-lu")} | {e.Date:yyyy-MM-dd} ---");
                if (e.BodyPreview.Length > 0) sb.AppendLine(e.BodyPreview);
                sb.AppendLine();
            }
        }

        return sb.Length > 0 ? sb.ToString() : "(aucun mail recent en contexte)";
    }

    private async Task<List<Incoming>> GetUpdatesAsync(CancellationToken ct)
    {
        var url = $"https://api.telegram.org/bot{config.Telegram.BotToken}/getUpdates?timeout=0";
        using var resp = await http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var list = new List<Incoming>();
        foreach (var u in doc.RootElement.GetProperty("result").EnumerateArray())
        {
            var updateId = u.GetProperty("update_id").GetInt64();
            if (!u.TryGetProperty("message", out var msg)) continue;
            var chatId = msg.GetProperty("chat").GetProperty("id").GetInt64();
            var messageId = msg.TryGetProperty("message_id", out var mid) ? mid.GetInt64() : 0;

            // Un message avec fichier n'a pas de "text" : sa consigne est dans "caption".
            var text = msg.TryGetProperty("text", out var t) ? t.GetString() ?? ""
                : msg.TryGetProperty("caption", out var c) ? c.GetString() ?? "" : "";

            TelegramFile? file = null;
            if (msg.TryGetProperty("document", out var d))
            {
                file = new TelegramFile(
                    d.GetProperty("file_id").GetString() ?? "",
                    d.TryGetProperty("file_name", out var fn) ? fn.GetString() ?? "fichier" : "fichier",
                    d.TryGetProperty("mime_type", out var mt) ? mt.GetString() ?? "application/octet-stream" : "application/octet-stream",
                    d.TryGetProperty("file_size", out var fs) ? fs.GetInt64() : 0);
            }
            else if (msg.TryGetProperty("photo", out var p) && p.ValueKind == JsonValueKind.Array && p.GetArrayLength() > 0)
            {
                // Telegram fournit plusieurs tailles : la derniere est la plus grande.
                var best = p[p.GetArrayLength() - 1];
                file = new TelegramFile(
                    best.GetProperty("file_id").GetString() ?? "",
                    $"photo_{messageId}.jpg", "image/jpeg",
                    best.TryGetProperty("file_size", out var ps) ? ps.GetInt64() : 0);
            }

            list.Add(new Incoming(updateId, chatId, messageId, text, file));
        }
        return list;
    }

    /// <summary>Message Telegram entrant : texte (ou legende du fichier) et fichier eventuel.</summary>
    private sealed record Incoming(long UpdateId, long ChatId, long MessageId, string Text, TelegramFile? File);

    /// <summary>Fichier joint a un message Telegram (document ou photo), avant telechargement.</summary>
    private sealed record TelegramFile(string FileId, string FileName, string MimeType, long Size);

    private async Task ConfirmUpdatesAsync(long offset, CancellationToken ct)
    {
        var url = $"https://api.telegram.org/bot{config.Telegram.BotToken}/getUpdates?offset={offset}";
        try { using var _ = await http.GetAsync(url, ct); } catch { /* best effort */ }
    }

    private async Task SendTextAsync(string text, CancellationToken ct)
    {
        // Telegram refuse (HTTP 400) un message vide ou de plus de 4096 caracteres : on garantit
        // un texte non vide et on decoupe les longues reponses (ex. un resume) en plusieurs envois.
        if (string.IsNullOrWhiteSpace(text)) text = "(vide)";

        var url = $"https://api.telegram.org/bot{config.Telegram.BotToken}/sendMessage";
        foreach (var chunk in SplitForTelegram(text))
        {
            var payload = new { chat_id = config.Telegram.ChatId, text = chunk, disable_web_page_preview = true };
            using var resp = await http.PostAsJsonAsync(url, payload, ct);
            if (!resp.IsSuccessStatusCode)
            {
                // Le corps Telegram explique le refus (ex. message trop long, chat introuvable) : utile pour diagnostiquer.
                var detail = await resp.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"Telegram a refuse l'envoi (HTTP {(int)resp.StatusCode}) : {detail}");
            }
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

    /// <summary>Retire d'eventuels backticks autour du JSON.</summary>
    private static string ExtractJson(string s)
    {
        s = s.Trim();
        var first = s.IndexOf('{');
        var last = s.LastIndexOf('}');
        return first >= 0 && last > first ? s[first..(last + 1)] : s;
    }

    private sealed record Route(string Intent, int Target, string Query, string Reply, string Answer, string Subject, string Start, string End);

    private sealed record RouteDto(string? Intent, int Target, string? Query, string? Reply, string? Answer, string? Subject, string? Start, string? End);
}
