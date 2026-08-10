namespace Charter.Notifications;

/// <summary>What happened to one attempted send.</summary>
public enum EmailDeliveryStatus
{
    /// <summary>The provider accepted the message.</summary>
    Sent,

    /// <summary>Email is off on this instance. Not a failure - see change spec 001, part C.1.</summary>
    Skipped,

    /// <summary>The per-recipient limit was already reached (change spec 001, part C.3).</summary>
    RateLimited,

    /// <summary>The provider refused it, or the connection did not survive.</summary>
    Failed,
}

/// <summary>
/// The outcome of a send, in a form both the admin settings page and a log line can use.
/// </summary>
/// <remarks>
/// Change spec 001 C.3: delivery failures are logged and surfaced, never swallowed. That is why
/// nothing here throws on a refusal - a thrown exception is either caught and lost or escapes into
/// an unrelated request. A returned result has to be looked at, and it carries two descriptions:
/// <see cref="Summary"/> for a person, <see cref="Detail"/> for whoever is debugging the mail
/// server. Neither ever contains a credential.
/// </remarks>
public sealed record EmailDeliveryResult
{
    /// <summary>What happened.</summary>
    public required EmailDeliveryStatus Status { get; init; }

    /// <summary>Plain language, safe for the settings UI. Never a stack trace (section 11).</summary>
    public required string Summary { get; init; }

    /// <summary>The server's own words, when there were any. Engineer-facing.</summary>
    public string? Detail { get; init; }

    /// <summary>How long until this recipient's bucket allows another message.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>True only when the provider took the message.</summary>
    public bool Delivered => Status is EmailDeliveryStatus.Sent;

    /// <summary>True when something went wrong, as opposed to email simply being off.</summary>
    public bool IsFailure => Status is EmailDeliveryStatus.Failed;

    /// <summary>The provider accepted it.</summary>
    public static EmailDeliveryResult Sent(string? detail = null) => new()
    {
        Status = EmailDeliveryStatus.Sent,
        Summary = "Delivered to the mail server.",
        Detail = detail,
    };

    /// <summary>Email is off; nothing was attempted.</summary>
    public static EmailDeliveryResult Skipped(string summary) => new()
    {
        Status = EmailDeliveryStatus.Skipped,
        Summary = summary,
    };

    /// <summary>The recipient has had enough mail for now.</summary>
    public static EmailDeliveryResult RateLimited(TimeSpan retryAfter) => new()
    {
        Status = EmailDeliveryStatus.RateLimited,
        Summary = "Held back: this recipient has already had the maximum number of messages for now.",
        RetryAfter = retryAfter,
    };

    /// <summary>It did not go.</summary>
    public static EmailDeliveryResult Failed(string summary, string? detail = null) => new()
    {
        Status = EmailDeliveryStatus.Failed,
        Summary = summary,
        Detail = detail,
    };
}

/// <summary>
/// The email seam of change spec 001, part C.1.
/// </summary>
/// <remarks>
/// <para>
/// Three providers are specified - <c>resend</c>, <c>smtp</c>, <c>none</c> - and the implementation
/// discipline section says exactly one ships. That one is <see cref="SmtpEmailProvider"/>: it
/// reaches SES, Postmark, Mailgun, Resend's own SMTP endpoint and a self-hosted relay without a
/// second code path, and it can be tested without holding an account anywhere.
/// <see cref="NullEmailProvider"/> is not a second implementation; it is what <c>none</c> means.
/// </para>
/// <para>
/// The interface is deliberately narrow: one already-rendered message, one recipient, a result that
/// never throws. An API provider - Resend, or SES over HTTPS - implements the same three members by
/// posting <see cref="EmailMessage.Content"/> as a JSON body and mapping the response status onto
/// <see cref="EmailDeliveryResult"/>. Nothing in this shape assumes a socket, an envelope or a
/// multipart body; all of that lives inside the SMTP implementation.
/// </para>
/// </remarks>
public interface IEmailProvider
{
    /// <summary>The configuration token this provider answers to: <c>smtp</c>, <c>none</c>.</summary>
    string Name { get; }

    /// <summary>False when the provider cannot deliver anything, which is a supported state.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Attempts one delivery. Implementations report failure by returning it, not by throwing.
    /// </summary>
    Task<EmailDeliveryResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
