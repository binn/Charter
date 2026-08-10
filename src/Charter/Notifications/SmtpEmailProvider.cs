using Charter.Configuration;

namespace Charter.Notifications;

/// <summary>
/// The one <see cref="IEmailProvider"/> implementation this build ships (change spec 001, part C.1).
/// </summary>
/// <remarks>
/// <para>
/// SMTP was chosen over an API provider for coverage per unit of code. One implementation reaches
/// Amazon SES, Postmark, Mailgun, Resend's own SMTP endpoint, Fastmail, Google Workspace and a
/// Postfix container on the same host. An API provider would reach exactly one of those and would
/// need a vendor account before a single test could run.
/// </para>
/// <para>
/// This class does not open sockets. It renders the message and hands an envelope to
/// <see cref="ISmtpTransport"/>, which is what lets everything above it be exercised without a
/// network.
/// </para>
/// </remarks>
public sealed class SmtpEmailProvider : IEmailProvider
{
    private readonly EmailConfig config;
    private readonly ISmtpTransport transport;
    private readonly TimeProvider clock;
    private readonly EmailAddress from;

    /// <summary>Creates the provider for <paramref name="config"/>.</summary>
    /// <exception cref="ArgumentException">Email is not enabled in <paramref name="config"/>.</exception>
    public SmtpEmailProvider(EmailConfig config, ISmtpTransport transport, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(clock);

        if (!config.Enabled || config.FromAddress is null)
        {
            throw new ArgumentException(
                "SmtpEmailProvider needs an enabled email configuration. Register NullEmailProvider " +
                "when CHARTER_EMAIL_PROVIDER is none.",
                nameof(config));
        }

        this.config = config;
        this.transport = transport;
        this.clock = clock;

        from = EmailAddress.Create(config.FromAddress, config.FromName);
    }

    /// <inheritdoc />
    public string Name => "smtp";

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public async Task<EmailDeliveryResult> SendAsync(
        EmailMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var boundary = MimeWriter.NewBoundary();
        var messageId = $"{Guid.NewGuid():n}@{DomainOf(from.Address)}";

        var document = MimeWriter.Write(
            message,
            from,
            config.ReplyTo,
            clock.GetUtcNow(),
            boundary,
            messageId);

        var result = await transport.SendAsync(
            new SmtpEnvelope
            {
                From = from.Address,
                To = message.To.Address,
                Message = document,
            },
            cancellationToken).ConfigureAwait(false);

        if (result.Accepted)
        {
            return EmailDeliveryResult.Sent(result.Detail);
        }

        // Section 11: what an administrator reads is a sentence. The server's own words are kept,
        // but as detail beside it rather than as the message.
        var summary = result.Transient
            ? "The mail server did not accept this message and may accept it later."
            : "The mail server refused this message.";

        return EmailDeliveryResult.Failed(summary, result.Detail);
    }

    private static string DomainOf(string address)
        => address[(address.IndexOf('@', StringComparison.Ordinal) + 1)..];
}
