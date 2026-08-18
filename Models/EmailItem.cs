using MailKit;

namespace MailAgent.Models;

/// <summary>Un email lu depuis la boite, reduit aux infos utiles au tri.</summary>
public sealed record EmailItem(
    UniqueId Uid,
    bool Seen,
    bool Answered,
    string MessageId,
    string From,
    string Subject,
    string BodyPreview,
    DateTimeOffset Date,
    string UnsubscribeHeader = "",      // valeur brute de List-Unsubscribe (RFC 2369), "" si absent
    bool OneClickUnsubscribe = false);  // en-tete List-Unsubscribe-Post=One-Click present (RFC 8058)
