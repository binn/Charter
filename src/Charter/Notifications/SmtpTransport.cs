using System.Net.Sockets;
using System.Security.Authentication;
using Charter.Configuration;
using Microsoft.Extensions.Logging;

namespace Charter.Notifications;

/// <summary>One message, already rendered, with the envelope it needs.</summary>
public sealed record SmtpEnvelope
{
    /// <summary>The <c>MAIL FROM</c> address.</summary>
    public required string From { get; init; }

    /// <summary>The <c>RCPT TO</c> address.</summary>
    public required string To { get; init; }

    /// <summary>The full RFC 5322 document, CRLF line endings, no trailing dot.</summary>
    public required string Message { get; init; }
}

/// <summary>What the mail server said.</summary>
public sealed record SmtpTransportResult
{
    /// <summary>True when the server accepted the message.</summary>
    public required bool Accepted { get; init; }

    /// <summary>The final status code, or <c>null</c> when the connection never got that far.</summary>
    public int? Code { get; init; }

    /// <summary>The server's text, or the reason the attempt did not reach a server.</summary>
    public string? Detail { get; init; }

    /// <summary>True when the same message is worth sending again later.</summary>
    public bool Transient { get; init; }

    /// <summary>Accepted.</summary>
    public static SmtpTransportResult Ok(int code, string? detail) =>
        new() { Accepted = true, Code = code, Detail = detail };

    /// <summary>Refused.</summary>
    public static SmtpTransportResult Refused(int? code, string? detail, bool transient) =>
        new() { Accepted = false, Code = code, Detail = detail, Transient = transient };
}

/// <summary>
/// The socket-owning half of SMTP delivery.
/// </summary>
/// <remarks>
/// Split from <see cref="SmtpEmailProvider"/> so that everything above it - templates, rate
/// limiting, the delivery log, the notification fan-out - is testable with no network at all. A test
/// substitutes this interface; the protocol itself is covered separately by driving
/// <see cref="SmtpConversation"/> over a scripted channel.
/// </remarks>
public interface ISmtpTransport
{
    /// <summary>Opens a connection, sends one message, and closes it.</summary>
    Task<SmtpTransportResult> SendAsync(SmtpEnvelope envelope, CancellationToken cancellationToken = default);
}

/// <summary>An <see cref="ISmtpTransport"/> that talks to a real mail server.</summary>
/// <remarks>
/// A connection per message. Connection reuse would be faster, but Charter's outbound volume is a
/// handful of messages an hour by design (change spec 001 C.3 rate-limits it deliberately), and a
/// pooled SMTP connection that has gone stale fails on the message somebody is waiting for.
/// </remarks>
public sealed class SmtpTransport : ISmtpTransport
{
    private readonly SmtpConfig config;
    private readonly ILogger<SmtpTransport> logger;
    private readonly TimeSpan timeout;

    /// <summary>Creates a transport for <paramref name="config"/>.</summary>
    public SmtpTransport(SmtpConfig config, ILogger<SmtpTransport> logger)
        : this(config, logger, TimeSpan.FromSeconds(30))
    {
    }

    /// <summary>Creates a transport with an explicit connect and exchange timeout.</summary>
    public SmtpTransport(SmtpConfig config, ILogger<SmtpTransport> logger, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        this.config = config;
        this.logger = logger;
        this.timeout = timeout;
    }

    /// <inheritdoc />
    public async Task<SmtpTransportResult> SendAsync(
        SmtpEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        ISmtpChannel? channel = null;

        try
        {
            channel = await NetworkSmtpChannel
                .ConnectAsync(config.Host, config.Port, config.ImplicitTls, timeout, deadline.Token)
                .ConfigureAwait(false);

            var conversation = new SmtpConversation(channel);
            var reply = await conversation.SendAsync(
                config.Tls,
                config.Username,
                config.Password,
                envelope.From,
                envelope.To,
                envelope.Message,
                deadline.Token).ConfigureAwait(false);

            // Only ever the endpoint. The URL carries the password, and a redacted URL is one
            // interpolation away from not being redacted.
            logger.LogInformation(
                "Mail accepted by {SmtpEndpoint} with status {SmtpStatus}",
                config.Endpoint,
                reply.Code);

            return SmtpTransportResult.Ok(reply.Code, reply.Text);
        }
        catch (SmtpProtocolException ex)
        {
            logger.LogError(
                "Mail server {SmtpEndpoint} refused the message: {Reason} (status {SmtpStatus})",
                config.Endpoint,
                ex.Message,
                ex.Reply?.Code);

            return SmtpTransportResult.Refused(ex.Reply?.Code, Describe(ex), ex.Transient);
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException)
        {
            logger.LogError(ex, "Could not reach the mail server at {SmtpEndpoint}", config.Endpoint);
            return SmtpTransportResult.Refused(null, ex.Message, transient: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                "The mail server at {SmtpEndpoint} did not answer within {Timeout}",
                config.Endpoint,
                timeout);

            return SmtpTransportResult.Refused(
                null,
                $"the mail server did not answer within {timeout.TotalSeconds:0} seconds",
                transient: true);
        }
        finally
        {
            if (channel is not null)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static string Describe(SmtpProtocolException exception)
        => exception.Reply is { } reply
            ? $"{exception.Message} Server said: {reply.Code} {reply.Text}"
            : exception.Message;
}
