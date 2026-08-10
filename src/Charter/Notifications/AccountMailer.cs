namespace Charter.Notifications;

/// <summary>
/// Who is standing in front of the screen that started a one-time link.
/// </summary>
/// <remarks>
/// This is the difference between an invitation and a password reset when email is off, and it is a
/// security boundary rather than a presentational one. An administrator who has just created an
/// account may be shown that account's setup link, because they were entitled to create the account
/// at all. The anonymous visitor who typed an address into the forgot-password form may not be shown
/// anything, because anybody can type anybody's address into that form - surfacing the link there
/// would turn "email is not configured" into "account takeover by anyone who knows an address".
/// </remarks>
public enum OneTimeLinkAudience
{
    /// <summary>An administrator acting on somebody else's account.</summary>
    Administrator,

    /// <summary>The person the link is for, not yet authenticated.</summary>
    Recipient,
}

/// <summary>The result of trying to get a one-time link to a person.</summary>
public sealed record OneTimeLinkDelivery
{
    /// <summary>True when it was emailed.</summary>
    public required bool Emailed { get; init; }

    /// <summary>
    /// The link, when the caller may show it. Null whenever it was emailed, and null whenever
    /// showing it would disclose it to the wrong person.
    /// </summary>
    public Uri? LinkToSurface { get; init; }

    /// <summary>What to tell whoever is looking at the screen. Plain language (section 11).</summary>
    public required string Explanation { get; init; }

    /// <summary>The underlying delivery attempt, when one was made.</summary>
    public EmailDeliveryResult? Delivery { get; init; }
}

/// <summary>
/// The two account emails that must keep working when email does not: invitation and reset.
/// </summary>
/// <remarks>
/// Change spec 001 C.1: <em>under <c>none</c>, admins create users directly with a one-time link
/// surfaced in the UI, and email-dependent settings are disabled with an explanation rather than
/// silently failing to send</em>. The important half is the last clause. Every method here reports
/// what happened, so the caller can render the link, or the explanation, instead of showing a
/// success message for a message nobody sent.
/// </remarks>
public interface IAccountMailer
{
    /// <summary>Whether mail can be sent at all, and what to say when it cannot.</summary>
    EmailAvailability Availability { get; }

    /// <summary>Invites somebody, falling back to the link when email is off or refuses.</summary>
    Task<OneTimeLinkDelivery> SendInvitationAsync(
        EmailAddress to,
        InvitationEmail invitation,
        OneTimeLinkAudience audience = OneTimeLinkAudience.Administrator,
        CancellationToken cancellationToken = default);

    /// <summary>Sends a password reset link.</summary>
    Task<OneTimeLinkDelivery> SendPasswordResetAsync(
        EmailAddress to,
        PasswordResetEmail reset,
        OneTimeLinkAudience audience = OneTimeLinkAudience.Recipient,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAccountMailer" />
public sealed class AccountMailer : IAccountMailer
{
    private const string SelfServiceUnavailable =
        "Email is not set up on this instance, so Charter cannot send a reset link. " +
        "Ask an administrator to reset your password for you.";

    private readonly IEmailSender sender;

    /// <summary>Creates the mailer.</summary>
    public AccountMailer(IEmailSender sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        this.sender = sender;
    }

    /// <inheritdoc />
    public EmailAvailability Availability => sender.Availability;

    /// <inheritdoc />
    public async Task<OneTimeLinkDelivery> SendInvitationAsync(
        EmailAddress to,
        InvitationEmail invitation,
        OneTimeLinkAudience audience = OneTimeLinkAudience.Administrator,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(invitation);

        var result = await sender.SendAsync(
            new EmailMessage
            {
                To = to,
                Content = EmailTemplates.Invitation(invitation),
                Category = EmailCategory.Transactional,
                Kind = EmailTemplates.InvitationKind,
            },
            cancellationToken).ConfigureAwait(false);

        if (result.Delivered)
        {
            return new OneTimeLinkDelivery
            {
                Emailed = true,
                Explanation = $"Invitation sent to {to.Address}.",
                Delivery = result,
            };
        }

        // A failed send is treated exactly like no email at all. The alternative - report the
        // failure and stop - leaves an administrator holding an account nobody can sign in to, which
        // is the outcome part C.1 exists to prevent.
        return new OneTimeLinkDelivery
        {
            Emailed = false,
            LinkToSurface = audience is OneTimeLinkAudience.Administrator ? invitation.AcceptUrl : null,
            Explanation = audience is OneTimeLinkAudience.Administrator
                ? Explain(result, to)
                : "Charter could not send the invitation. Ask an administrator to send you the link.",
            Delivery = result,
        };
    }

    /// <inheritdoc />
    public async Task<OneTimeLinkDelivery> SendPasswordResetAsync(
        EmailAddress to,
        PasswordResetEmail reset,
        OneTimeLinkAudience audience = OneTimeLinkAudience.Recipient,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(reset);

        var result = await sender.SendAsync(
            new EmailMessage
            {
                To = to,
                Content = EmailTemplates.PasswordReset(reset),
                Category = EmailCategory.Transactional,
                Kind = EmailTemplates.PasswordResetKind,
            },
            cancellationToken).ConfigureAwait(false);

        if (result.Delivered)
        {
            return new OneTimeLinkDelivery
            {
                Emailed = true,
                Explanation = "If that address has an account, a reset link is on its way.",
                Delivery = result,
            };
        }

        return new OneTimeLinkDelivery
        {
            Emailed = false,
            LinkToSurface = audience is OneTimeLinkAudience.Administrator ? reset.ResetUrl : null,
            Explanation = audience is OneTimeLinkAudience.Administrator
                ? Explain(result, to)
                : SelfServiceUnavailable,
            Delivery = result,
        };
    }

    private static string Explain(EmailDeliveryResult result, EmailAddress to) => result.Status switch
    {
        EmailDeliveryStatus.Skipped =>
            "Email is not set up on this instance. Send this one-time link to " +
            $"{to.Address} yourself - it works once.",

        EmailDeliveryStatus.RateLimited =>
            $"Charter has already sent {to.Address} several messages recently and held this one " +
            "back. Send this one-time link yourself, or try again shortly.",

        _ =>
            $"Charter could not email {to.Address}: {result.Summary} Send this one-time link " +
            "yourself in the meantime.",
    };
}
