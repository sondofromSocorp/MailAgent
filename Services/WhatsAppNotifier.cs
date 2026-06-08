using MailAgent.Configuration;
using MailAgent.Models;
using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace MailAgent.Services;

/// <summary>Envoie la notification via Twilio WhatsApp (sandbox par defaut).</summary>
public sealed class WhatsAppNotifier : INotifier
{
    private readonly AgentConfig _config;

    public WhatsAppNotifier(AgentConfig config)
    {
        _config = config;
        TwilioClient.Init(config.TwilioAccountSid, config.TwilioAuthToken);
    }

    public async Task NotifyAsync(EmailItem email, Classification classification, CancellationToken ct = default)
    {
        var body =
            $"\ud83d\udce7 Mail important\n" +
            $"De : {email.From}\n" +
            $"Objet : {email.Subject}\n" +
            $"Raison : {classification.Reason}";

        await MessageResource.CreateAsync(
            from: new PhoneNumber(_config.WhatsApp.FromNumber),
            to: new PhoneNumber(_config.WhatsApp.ToNumber),
            body: body);
    }
}
