namespace Charter.Domain;

/// <summary>
/// What happened to one attempted send, as the database spells it.
/// </summary>
/// <remarks>
/// The domain's own copy of <c>Charter.Notifications.EmailDeliveryStatus</c>, so the stored value is
/// a persistence contract rather than an enum in a namespace above it (the same split
/// <see cref="CredentialKind"/> keeps from the model layer).
/// </remarks>
public enum EmailDeliveryOutcome
{
    /// <summary>The provider accepted the message.</summary>
    Sent,

    /// <summary>Email is off on this instance. Not a failure.</summary>
    Skipped,

    /// <summary>The per-recipient limit was already reached.</summary>
    RateLimited,

    /// <summary>The provider refused it, or the connection did not survive.</summary>
    Failed,
}

/// <summary>
/// One attempted email delivery, kept so a failure is visible after the container restarts.
/// </summary>
/// <remarks>
/// <para>
/// Change spec 001 C.3 asks that delivery failures be surfaced in admin settings rather than only
/// logged. In memory that held until the next deploy, which is the point at which a self-hoster
/// would have most wanted to know that invitations have been failing all week — so the log is a
/// table.
/// </para>
/// <para>
/// It is pruned, because an unbounded record of every email an instance ever sent is its own
/// problem: it grows without limit, and it is a list of who was contacted and when. Retention is
/// enforced by the store, not by an operator remembering to run something.
/// </para>
/// <para>
/// <strong>Nothing here is message content.</strong> The recipient, the template name and the mail
/// server's own words about the failure — no subject line, no body, and never a credential.
/// </para>
/// </remarks>
public sealed class EmailDelivery
{
    /// <summary>Enough for the longest addr-spec.</summary>
    public const int MaxRecipientLength = 320;

    /// <summary>Template labels are short and machine-chosen: <c>invitation</c>, <c>needs_input</c>.</summary>
    public const int MaxKindLength = 60;

    /// <summary>A sentence for an administrator.</summary>
    public const int MaxSummaryLength = 300;

    /// <summary>An SMTP reply, truncated. Diagnostics, not a transcript.</summary>
    public const int MaxDetailLength = 1000;

    private EmailDelivery()
    {
    }

    private EmailDelivery(
        Guid id,
        DateTimeOffset at,
        string recipient,
        string kind,
        EmailDeliveryOutcome outcome,
        string summary,
        string? detail)
    {
        Id = id;
        At = at;
        Recipient = recipient;
        Kind = kind;
        Outcome = outcome;
        Summary = summary;
        Detail = detail;
    }

    public Guid Id { get; private set; }

    /// <summary>When the attempt finished. The column retention sweeps on.</summary>
    public DateTimeOffset At { get; private set; }

    public string Recipient { get; private set; } = string.Empty;

    /// <summary>The template: <c>invitation</c>, <c>needs_input</c>, and so on.</summary>
    public string Kind { get; private set; } = string.Empty;

    public EmailDeliveryOutcome Outcome { get; private set; }

    /// <summary>A sentence for the administrator.</summary>
    public string Summary { get; private set; } = string.Empty;

    /// <summary>The mail server's own words, when it gave any.</summary>
    public string? Detail { get; private set; }

    public static EmailDelivery Record(
        DateTimeOffset at,
        string recipient,
        string kind,
        EmailDeliveryOutcome outcome,
        string summary,
        string? detail = null,
        Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        return new EmailDelivery(
            id ?? Guid.CreateVersion7(),
            DomainTime.Resolve(at),
            Truncate(recipient.Trim().ToLowerInvariant(), MaxRecipientLength),
            Truncate(kind.Trim(), MaxKindLength),
            outcome,
            Truncate(summary ?? string.Empty, MaxSummaryLength),
            string.IsNullOrWhiteSpace(detail) ? null : Truncate(detail, MaxDetailLength));
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
