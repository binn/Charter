namespace Charter.Notifications;

/// <summary>
/// What <c>CHARTER_EMAIL_PROVIDER=none</c> does: nothing, on purpose, without failing.
/// </summary>
/// <remarks>
/// <para>
/// Change spec 001 C.1 is explicit that <c>none</c> must degrade cleanly. Email is needed for
/// invitations, notifications, 2FA recovery and password reset, and a self-hoster with no mail
/// server still has to be able to add a colleague - so the absence of email changes what the UI
/// offers, never whether an operation succeeds.
/// </para>
/// <para>
/// Two shapes were rejected here. Throwing turns every notification site into a try/catch and
/// eventually into a swallowed exception. Registering nothing at all makes <c>IEmailProvider</c>
/// nullable at every injection point, and a nullable dependency is checked correctly right up until
/// the day it is not. Returning <see cref="EmailDeliveryStatus.Skipped"/> with a sentence a person
/// can read is the version that cannot rot: callers that care branch on it, callers that do not
/// carry on.
/// </para>
/// </remarks>
public sealed class NullEmailProvider : IEmailProvider
{
    /// <summary>The sentence shown wherever an email-dependent feature is disabled.</summary>
    public const string Explanation =
        "Email is not set up on this instance, so Charter cannot send messages. " +
        "Invitations and reset links are shown in the app for you to pass on yourself.";

    /// <inheritdoc />
    public string Name => "none";

    /// <inheritdoc />
    public bool IsEnabled => false;

    /// <inheritdoc />
    public Task<EmailDeliveryResult> SendAsync(
        EmailMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(EmailDeliveryResult.Skipped(Explanation));
    }
}
