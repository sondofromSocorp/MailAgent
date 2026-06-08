using System.Text.Json;

namespace MailAgent.Services;

/// <summary>Suivi anti-doublon : memorise les MessageId deja traites dans un fichier JSON.</summary>
public sealed class NotificationStore
{
    private readonly string _path;
    private readonly HashSet<string> _seen;

    private NotificationStore(string path, HashSet<string> seen)
    {
        _path = path;
        _seen = seen;
    }

    public static NotificationStore Load(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? [];
                return new NotificationStore(path, [.. ids]);
            }
            catch
            {
                // Fichier corrompu : on repart d'un etat vide.
            }
        }
        return new NotificationStore(path, []);
    }

    public bool AlreadyHandled(string messageId) => _seen.Contains(messageId);

    public void MarkHandled(string messageId) => _seen.Add(messageId);

    public async Task SaveAsync(CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(_seen.ToList(),
            new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_path, json, ct);
    }
}
