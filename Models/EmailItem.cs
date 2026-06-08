using MailKit;

namespace MailAgent.Models;

/// <summary>Un email lu depuis la boite, reduit aux infos utiles au tri.</summary>
public sealed record EmailItem(
    UniqueId Uid,
    string MessageId,
    string From,
    string Subject,
    string BodyPreview,
    DateTimeOffset Date);
