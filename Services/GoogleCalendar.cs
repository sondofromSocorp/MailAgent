using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using MailAgent.Configuration;
using MailAgent.Models;

namespace MailAgent.Services;

/// <summary>
/// Cree des evenements dans l'agenda Google de l'utilisateur via l'API Calendar (REST).
/// Authentification OAuth par refresh token (client_id / client_secret / refresh_token en secrets).
/// Inactif tant que ces secrets ne sont pas fournis (cf. IsConfigured).
/// </summary>
public sealed class GoogleCalendar(AgentConfig config, HttpClient http)
{
    public bool IsConfigured =>
        config.Calendar.Enabled &&
        !string.IsNullOrWhiteSpace(config.Calendar.ClientId) &&
        !string.IsNullOrWhiteSpace(config.Calendar.ClientSecret) &&
        !string.IsNullOrWhiteSpace(config.Calendar.RefreshToken);

    /// <summary>Cree un evenement et retourne son lien (htmlLink), ou null en cas d'echec.</summary>
    public async Task<string?> CreateEventAsync(EventInfo evt, string description, CancellationToken ct = default)
    {
        var token = await GetAccessTokenAsync(ct);
        var (start, end, allDay) = ParseTimes(evt);

        object startObj = allDay ? new { date = start } : new { dateTime = start, timeZone = config.Calendar.TimeZone };
        object endObj = allDay ? new { date = end } : new { dateTime = end, timeZone = config.Calendar.TimeZone };

        var body = new
        {
            summary = evt.Title,
            location = string.IsNullOrWhiteSpace(evt.Location) ? null : evt.Location,
            description,
            start = startObj,
            end = endObj,
            reminders = new
            {
                useDefault = false,
                overrides = new[]
                {
                    new { method = "popup", minutes = 1440 },
                    new { method = "popup", minutes = 180 }
                }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post,
            "https://www.googleapis.com/calendar/v3/calendars/primary/events");
        req.Headers.Add("Authorization", $"Bearer {token}");
        req.Content = JsonContent.Create(body);

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"      [Agenda] echec creation (HTTP {(int)resp.StatusCode}) : {await resp.Content.ReadAsStringAsync(ct)}");
            return null;
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("htmlLink", out var l) ? l.GetString() ?? "" : "";
    }

    /// <summary>Echange le refresh token contre un access token de courte duree.</summary>
    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = config.Calendar.ClientId,
            ["client_secret"] = config.Calendar.ClientSecret,
            ["refresh_token"] = config.Calendar.RefreshToken,
            ["grant_type"] = "refresh_token"
        });

        using var resp = await http.PostAsync("https://oauth2.googleapis.com/token", form, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Google n'a pas renvoye d'access_token.");
    }

    /// <summary>
    /// Determine debut/fin. Date seule -> evenement journee entiere (fin = lendemain, exclusif Google).
    /// Avec heure -> fin = +2h si non precisee.
    /// </summary>
    private static (string start, string end, bool allDay) ParseTimes(EventInfo evt)
    {
        var s = evt.Start.Trim();

        if (!s.Contains('T'))
        {
            var date = DateOnly.Parse(s, CultureInfo.InvariantCulture);
            return (date.ToString("yyyy-MM-dd"), date.AddDays(1).ToString("yyyy-MM-dd"), true);
        }

        var startDt = DateTime.Parse(s, CultureInfo.InvariantCulture);
        var end = !string.IsNullOrWhiteSpace(evt.End) && evt.End.Contains('T')
            ? evt.End
            : startDt.AddHours(2).ToString("yyyy-MM-ddTHH:mm:ss");
        return (startDt.ToString("yyyy-MM-ddTHH:mm:ss"), end, false);
    }
}
