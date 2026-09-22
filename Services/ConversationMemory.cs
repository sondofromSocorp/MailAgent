using System.Text;
using System.Text.Json;

namespace MailAgent.Services;

/// <summary>
/// Memoire courte de la conversation Telegram : les derniers echanges (message de l'utilisateur,
/// reponse du bot), fournis au routeur pour comprendre un message court qui repond a une question
/// precedente (« le deuxieme », « Durand », « et le mail d'avant ? »). Le bot tourne en passes
/// separees (une par lancement) : la memoire est persistee dans un petit fichier JSON.
/// Toute erreur de lecture/ecriture est ignoree : la memoire est un confort, jamais un bloquant.
/// </summary>
public sealed class ConversationMemory(string path, int maxExchanges, TimeSpan ttl)
{
    private const int MaxEntryChars = 300;
    private readonly List<Entry> _entries = Load(path);

    /// <summary>Enregistre un message de l'utilisateur.</summary>
    public void AddUser(string text) => Add("user", text);

    /// <summary>Enregistre une reponse du bot.</summary>
    public void AddBot(string text) => Add("bot", text);

    /// <summary>
    /// Section de contexte pour le LLM (du plus ancien au plus recent), ou "" si aucun echange
    /// recent. Les echanges plus vieux que le TTL sont ignores : « le deuxieme » lance des heures
    /// plus tard ne se rapporte plus a rien.
    /// </summary>
    public string Describe()
    {
        var cutoff = DateTimeOffset.UtcNow - ttl;
        var recent = _entries.Where(e => e.At >= cutoff).ToList();
        if (recent.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("--- Derniers echanges Telegram (du plus ancien au plus recent) ---");
        foreach (var e in recent)
            sb.AppendLine($"{(e.Role == "user" ? "Utilisateur" : "Assistant")} : {e.Text}");
        sb.Append("--- fin des echanges ---");
        return sb.ToString();
    }

    /// <summary>Ecrit la memoire sur disque (a appeler en fin de passe).</summary>
    public void Save()
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(_entries));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    [Memoire] sauvegarde impossible ({ex.Message}) : la conversation ne sera pas memorisee.");
        }
    }

    private void Add(string role, string text)
    {
        text = text.Replace("\r", "").Replace('\n', ' ').Trim();
        if (text.Length == 0) return;
        if (text.Length > MaxEntryChars) text = text[..MaxEntryChars] + "…";
        _entries.Add(new Entry(role, text, DateTimeOffset.UtcNow));

        // On borne en nombre de messages (un echange = deux messages) et on purge les entrees
        // expirees pour que le fichier ne grossisse pas.
        var cutoff = DateTimeOffset.UtcNow - ttl;
        _entries.RemoveAll(e => e.At < cutoff);
        var max = Math.Max(2, maxExchanges * 2);
        if (_entries.Count > max) _entries.RemoveRange(0, _entries.Count - max);
    }

    private static List<Entry> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    [Memoire] lecture impossible ({ex.Message}) : on repart sans historique.");
            return [];
        }
    }

    private sealed record Entry(string Role, string Text, DateTimeOffset At);
}
