using Charter.Configuration;
using Microsoft.Extensions.Logging;

namespace Charter.Notifications;

/// <summary>
/// What the settings UI needs in order to disable an email-dependent control and say why.
/// </summary>
/// <remarks>
/// Change spec 001 C.1: under <c>none</c>, email-dependent settings are disabled with an explanation
/// rather than silently failing to send. That explanation has to come from the server - the client
/// cannot know whether SMTP is configured - so it is part of the settings payload rather than a
/// string in the SPA.
/// </remarks>
public sealed record EmailAvailability
{
    /// <summary>True when Charter can send mail.</summary>
    public required bool Enabled { get; init; }

    /// <summary>The configured provider token: <c>smtp</c> or <c>none</c>.</summary>
    public required string Provider { get; init; }

    /// <summary>Why email is off, in plain language. Null when it is on.</summary>
    public string? DisabledReason { get; init; }

    /// <summary>What an operator would change to turn it on. Null when it is on.</summary>
    public string? HowToEnable { get; init; }

    /// <summary>The address mail is sent as, when there is one.</summary>
    public string? FromAddress { get; init; }

    /// <summary>Reads the availability out of the parsed configuration.</summary>
    public static EmailAvailability From(EmailConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.Enabled
            ? new EmailAvailability
            {
                Enabled = true,
                Provider = config.ProviderToken,
                FromAddress = config.FromAddress,
            }
            : new EmailAvailability
            {
                Enabled = false,
                Provider = config.ProviderToken,
                DisabledReason = NullEmailProvider.Explanation,
                HowToEnable =
                    "Set CHARTER_EMAIL_PROVIDER=smtp, CHARTER_SMTP_URL to your mail server, and " +
                    "CHARTER_EMAIL_FROM to an address on a domain you control, then restart Charter.",
            };
    }
}

/// <summary>
/// The one place a message leaves Charter: rate limit, provider, record, return.
/// </summary>
/// <remarks>
/// Every caller goes through this rather than through <see cref="IEmailProvider"/> directly, because
/// the three obligations in change spec 001 C.3 - failures logged, failures surfaced, mail
/// rate-limited per recipient - are obligations on <em>sending</em>, not on any one provider. A
/// second provider added later inherits all three by construction.
/// </remarks>
public interface IEmailSender
{
    /// <summary>Whether mail can be sent, and what to tell the user when it cannot.</summary>
    EmailAvailability Availability { get; }

    /// <summary>Sends one message. Never throws for a delivery problem; returns it.</summary>
    Task<EmailDeliveryResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IEmailSender" />
public sealed class EmailSender : IEmailSender
{
    private readonly IEmailProvider provider;
    private readonly IEmailRateLimiter rateLimiter;
    private readonly IEmailDeliveryLog deliveryLog;
    private readonly ILogger<EmailSender> logger;
    private readonly TimeProvider clock;

    /// <summary>Creates the sender.</summary>
    public EmailSender(
        IEmailProvider provider,
        IEmailRateLimiter rateLimiter,
        IEmailDeliveryLog deliveryLog,
        EmailConfig config,
        ILogger<EmailSender> logger,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(rateLimiter);
        ArgumentNullException.ThrowIfNull(deliveryLog);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(clock);

        this.provider = provider;
        this.rateLimiter = rateLimiter;
        this.deliveryLog = deliveryLog;
        this.logger = logger;
        this.clock = clock;

        Availability = EmailAvailability.From(config);
    }

    /// <inheritdoc />
    public EmailAvailability Availability { get; }

    /// <inheritdoc />
    public async Task<EmailDeliveryResult> SendAsync(
        EmailMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!provider.IsEnabled)
        {
            // Not a failure and not an error-level event. Section C.1: no email is a supported
            // configuration, and logging it as a problem would train an operator to ignore this log.
            var skipped = EmailDeliveryResult.Skipped(Availability.DisabledReason ?? NullEmailProvider.Explanation);
            logger.LogDebug("Email is disabled; {EmailKind} for {Recipient} was not sent", message.Kind, message.To);

            return Record(message, skipped);
        }

        if (!rateLimiter.TryAcquire(message.To.Address, message.Category, out var retryAfter))
        {
            var limited = EmailDeliveryResult.RateLimited(retryAfter);

            logger.LogWarning(
                "Rate limit reached for {Recipient}: {EmailKind} held back, {RetryAfter} until the next slot",
                message.To,
                message.Kind,
                retryAfter);

            return Record(message, limited);
        }

        try
        {
            var result = await provider.SendAsync(message, cancellationToken).ConfigureAwait(false);

            if (result.IsFailure)
            {
                // C.3: logged and surfaced, never swallowed. Both happen here, and the result is
                // still returned so the caller can offer the fallback - a link on screen, say.
                logger.LogError(
                    "Email delivery failed for {EmailKind} to {Recipient} via {EmailProvider}: {Reason} {Detail}",
                    message.Kind,
                    message.To,
                    provider.Name,
                    result.Summary,
                    result.Detail);
            }
            else
            {
                logger.LogInformation(
                    "Sent {EmailKind} to {Recipient} via {EmailProvider}",
                    message.Kind,
                    message.To,
                    provider.Name);
            }

            return Record(message, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A provider is not supposed to throw, and one that does must not take a request down
            // with it. Turning it into a recorded failure is the difference between "email is
            // broken, here is when and why" and a 500 on the invitation form.
            logger.LogError(
                ex,
                "Email delivery threw for {EmailKind} to {Recipient} via {EmailProvider}",
                message.Kind,
                message.To,
                provider.Name);

            return Record(
                message,
                EmailDeliveryResult.Failed("Charter could not reach the mail server.", ex.Message));
        }
    }

    private EmailDeliveryResult Record(EmailMessage message, EmailDeliveryResult result)
    {
        deliveryLog.Record(new EmailDeliveryRecord
        {
            At = clock.GetUtcNow(),
            Recipient = message.To.Address,
            Kind = message.Kind,
            Status = result.Status,
            Summary = result.Summary,
            Detail = result.Detail,
        });

        return result;
    }
}
