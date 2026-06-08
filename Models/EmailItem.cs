using MailKit;

namespace MailAgent.Models;

/// <summary>Un email lu depuis la boite, reduit aux infos utiles au tri.</summary>
public sealed record EmailItem(
    UniqueId Uid,
    bool Seen,
    string MessageId,
    string From,
    string Subject,
    string BodyPreview,
    DateTimeOffset Date);
