using MailAgent.Models;

namespace MailAgent.Services;

/// <summary>
/// Abstraction du canal de notification. Permet de brancher plus tard
/// SMS Twilio, appel Twilio, Telegram, etc. sans toucher au reste.
/// </summary>
public interface INotifier
{
    Task NotifyAsync(EmailItem email, Classification classification, CancellationToken ct = default);
}
