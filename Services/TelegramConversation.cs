using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MailAgent.Configuration;
using MailAgent.Models;
using MimeKit;

namespace MailAgent.Services;

/// <summary>
/// Bot Telegram conversationnel : lit les messages entrants (getUpdates), determine l'intention
/// via le LLM configure (chat / repondre a un mail / valider / annuler) et agit. Repondre a un
/// mail passe TOUJOURS par une validation explicite. Sans etat local : l'offset Telegram est
/// confirme cote serveur, et le brouillon en attente vit dans un dossier IMAP (cf. EmailSender).
/// </summary>
public sealed class TelegramConversation(AgentConfig config, AccountConfig account, HttpClient http, ILlmClient llm, EmailReader reader, EmailSender sender, BlockListStore blocklist)
{
    private readonly UnsubscribeService _unsubscribe = new(account, sender, http);
    private readonly AssistantToolLoop _tools = new(reader, llm);

    private const string RouterPrompt =
        """
        Tu es le routeur d'un assistant mail personnel accessible sur Telegram. A partir du message
        de l'utilisateur et de la liste NUMEROTEE de ses derniers mails, determine l'INTENTION et
        reponds en JSON STRICT : {"intent":"...","target":N,"query":"...","reply":"...","answer":"..."}

        intent vaut EXACTEMENT l'une de ces valeurs :
        - "reply"  : l'utilisateur veut REPONDRE a un mail (ex. "reponds au syndic que je serai present").
                     target = le NUMERO du mail concerne dans la liste. reply = le texte COMPLET et poli
                     de la reponse a envoyer en son nom (salutation, message, formule de politesse),
                     en francais. answer = "".
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
        foreach (var (updateId, chatId, messageId, text) in updates)
        {
            maxUpdateId = Math.Max(maxUpdateId, updateId);
            if (chatId.ToString() != config.Telegram.ChatId || string.IsNullOrWhiteSpace(text)) continue;

            Console.WriteLine($"  [TELEGRAM] recu : {text}");
            try
            {
                await HandleAsync(text, context, recent, messageId, ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    [ERREUR] {ex.Message}");
                try { await SendTextAsync($"Desole, une erreur est survenue : {ex.Message}", ct); } catch { }
            }
        }

        await ConfirmUpdatesAsync(maxUpdateId + 1, ct);
    }

    private async Task HandleAsync(string text, string context, IReadOnlyList<EmailItem> recent, long messageId, CancellationToken ct)
    {
        var route = await RouteAsync(text, context, ct);
        switch (route.Intent)
        {
            case "send":
                await HandleSendAsync(ct);
                break;
            case "cancel":
                await sender.DeletePendingAsync(ct);
                await SendTextAsync("Ok, j'annule : rien n'a ete envoye.", ct);
                Console.WriteLine("    -> brouillon annule.");
                break;
            case "reply":
                await HandleReplyAsync(route, recent, ct);
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

    private async Task HandleReplyAsync(Route route, IReadOnlyList<EmailItem> recent, CancellationToken ct)
    {
        if (route.Target < 1 || route.Target > recent.Count || route.Reply.Length == 0)
        {
            await SendTextAsync("Je n'ai pas reussi a identifier le mail auquel repondre (il n'est peut-etre pas dans les 15 derniers). Precise l'expediteur ?", ct);
            return;
        }

        var original = recent[route.Target - 1];
        var msg = BuildReply(original, route.Reply, out var toAddress);
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
            $"✉️ Proposition de reponse a {toAddress}\nObjet : {msg.Subject}\n\n{route.Reply}\n\n" +
            $"Reponds OUI pour envoyer, ou dis-moi quoi changer.{noReplyWarn}", ct);
        Console.WriteLine($"    -> proposition de reponse stockee (a {toAddress}).");
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
            await SendTextAsync("Je n'ai pas identifie le mail dont tu veux te desabonner (il n'est peut-etre pas dans les 15 derniers). Precise l'expediteur ?", ct);
            return;
        }

        var email = recent[route.Target - 1];
        var outcome = await _unsubscribe.TryUnsubscribeAsync(email, ct);
        await SendTextAsync(outcome, ct);
        Console.WriteLine($"    -> desabonnement \"{email.Subject}\" : {outcome}");
    }

    private async Task HandleSendAsync(CancellationToken ct)
    {
        var pending = await sender.GetPendingAsync(ct);
        if (pending is null)
        {
            await SendTextAsync("Il n'y a aucune reponse en attente a envoyer.", ct);
            return;
        }

        await sender.SendAsync(pending, ct);
        await sender.DeletePendingAsync(ct);
        var to = pending.To.ToString();
        await SendTextAsync($"✅ Envoye a {to}.", ct);
        Console.WriteLine($"    -> mail envoye a {to}.");
    }

    /// <summary>Construit la reponse (MimeMessage) au mail d'origine, avec threading (Re: + In-Reply-To).</summary>
    private MimeMessage BuildReply(EmailItem original, string replyText, out string? toAddress)
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

        msg.Body = new TextPart("plain") { Text = replyText };
        return msg;
    }

    private async Task<Route> RouteAsync(string userMessage, string context, CancellationToken ct)
    {
        var userContent = $"Mails recents (numerotes) :\n{context}\n\nMessage de l'utilisateur :\n{userMessage}";
        var raw = ExtractJson(await llm.CompleteAsync(RouterPrompt, userContent, maxTokens: 1000, ct));

        try
        {
            var dto = JsonSerializer.Deserialize<RouteDto>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var intent = dto?.Intent?.Trim().ToLowerInvariant() ?? "chat";
            return new Route(intent, dto?.Target ?? 0, dto?.Query?.Trim() ?? "", dto?.Reply?.Trim() ?? "", dto?.Answer?.Trim() ?? "");
        }
        catch (JsonException)
        {
            // En cas de doute, on ne fait jamais d'action sensible : on retombe sur "chat".
            return new Route("chat", 0, "", "", "Je n'ai pas bien compris, peux-tu reformuler ?");
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

    private async Task<List<(long updateId, long chatId, long messageId, string text)>> GetUpdatesAsync(CancellationToken ct)
    {
        var url = $"https://api.telegram.org/bot{config.Telegram.BotToken}/getUpdates?timeout=0";
        using var resp = await http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var list = new List<(long, long, long, string)>();
        foreach (var u in doc.RootElement.GetProperty("result").EnumerateArray())
        {
            var updateId = u.GetProperty("update_id").GetInt64();
            if (!u.TryGetProperty("message", out var msg)) continue;
            var chatId = msg.GetProperty("chat").GetProperty("id").GetInt64();
            var messageId = msg.TryGetProperty("message_id", out var mid) ? mid.GetInt64() : 0;
            var text = msg.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            list.Add((updateId, chatId, messageId, text));
        }
        return list;
    }

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

    private sealed record Route(string Intent, int Target, string Query, string Reply, string Answer);

    private sealed record RouteDto(string? Intent, int Target, string? Query, string? Reply, string? Answer);
}
