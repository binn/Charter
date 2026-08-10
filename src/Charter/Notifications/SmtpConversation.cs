using System.Globalization;
using System.Text;
using Charter.Configuration;

namespace Charter.Notifications;

/// <summary>One SMTP reply: the status code and the text the server sent with it.</summary>
internal sealed record SmtpReply(int Code, string Text)
{
    /// <summary>2xx.</summary>
    internal bool IsPositive => Code is >= 200 and < 300;

    /// <summary>3xx - the server wants more input, as after <c>DATA</c> or an AUTH challenge.</summary>
    internal bool IsIntermediate => Code is >= 300 and < 400;

    /// <summary>
    /// 4xx. Worth trying again later; 5xx is not. Recorded so the delivery log can tell an
    /// administrator whether the message is coming back.
    /// </summary>
    internal bool IsTransient => Code is >= 400 and < 500;
}

/// <summary>An SMTP exchange that did not end the way it had to.</summary>
internal sealed class SmtpProtocolException : Exception
{
    internal SmtpProtocolException(string message, SmtpReply? reply = null, bool transient = false)
        : base(message)
    {
        Reply = reply;
        Transient = transient || (reply?.IsTransient ?? false);
    }

    internal SmtpReply? Reply { get; }

    /// <summary>True when trying again later is reasonable.</summary>
    internal bool Transient { get; }
}

/// <summary>
/// Drives one message through one connection: greeting, EHLO, TLS, AUTH, envelope, DATA, QUIT.
/// </summary>
/// <remarks>
/// <para>
/// Two decisions here are security ones rather than protocol ones, and both fail closed.
/// </para>
/// <para>
/// A configured <c>starttls</c> that the server does not advertise is an error, not a downgrade to
/// plaintext. The alternative - carry on unencrypted - is how a password ends up on the wire in
/// front of whoever is between Charter and the relay, and it would happen silently.
/// </para>
/// <para>
/// Credentials are never written to a log or an exception. The AUTH exchange is the one part of the
/// conversation this class refuses to describe: the command name reaches a log line, the argument
/// never does, and a failed AUTH reports the server's status code with a fixed sentence rather than
/// echoing back a response that may quote what was sent.
/// </para>
/// </remarks>
internal sealed class SmtpConversation
{
    /// <summary>What Charter announces itself as when the operator has not said otherwise.</summary>
    internal const string DefaultClientName = "charter";

    private readonly ISmtpChannel channel;
    private readonly string clientName;

    internal SmtpConversation(ISmtpChannel channel, string? clientName = null)
    {
        ArgumentNullException.ThrowIfNull(channel);

        this.channel = channel;
        this.clientName = string.IsNullOrWhiteSpace(clientName) ? DefaultClientName : clientName.Trim();
    }

    /// <summary>Extensions the server advertised in its last EHLO response, upper-cased.</summary>
    internal IReadOnlySet<string> Extensions { get; private set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Runs the whole exchange and returns the server's final response to the message body.
    /// </summary>
    /// <exception cref="SmtpProtocolException">The server refused a step.</exception>
    internal async Task<SmtpReply> SendAsync(
        SmtpTlsMode tls,
        string? username,
        Secret? password,
        string envelopeFrom,
        string envelopeTo,
        string message,
        CancellationToken cancellationToken)
    {
        var greeting = await ReadReplyAsync(cancellationToken).ConfigureAwait(false);
        if (greeting.Code != 220)
        {
            throw new SmtpProtocolException("The mail server did not accept the connection.", greeting);
        }

        await GreetAsync(cancellationToken).ConfigureAwait(false);

        if (tls is SmtpTlsMode.StartTls && !channel.IsSecure)
        {
            await StartTlsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (username is not null)
        {
            await AuthenticateAsync(tls, username, password, cancellationToken).ConfigureAwait(false);
        }

        await ExpectAsync($"MAIL FROM:<{envelopeFrom}>", 250, cancellationToken).ConfigureAwait(false);

        var recipient = await CommandAsync($"RCPT TO:<{envelopeTo}>", cancellationToken).ConfigureAwait(false);
        if (recipient.Code is not (250 or 251))
        {
            throw new SmtpProtocolException(
                "The mail server would not accept the recipient address.",
                recipient);
        }

        var data = await CommandAsync("DATA", cancellationToken).ConfigureAwait(false);
        if (!data.IsIntermediate)
        {
            throw new SmtpProtocolException("The mail server would not accept the message body.", data);
        }

        await channel.WriteRawAsync(DotStuff(message), cancellationToken).ConfigureAwait(false);
        await channel.WriteLineAsync(".", cancellationToken).ConfigureAwait(false);

        var accepted = await ReadReplyAsync(cancellationToken).ConfigureAwait(false);
        if (!accepted.IsPositive)
        {
            throw new SmtpProtocolException("The mail server rejected the message.", accepted);
        }

        // A server that refuses QUIT has still taken the message; failing here would report a
        // delivery that happened as a delivery that did not.
        try
        {
            await channel.WriteLineAsync("QUIT", cancellationToken).ConfigureAwait(false);
            _ = await ReadReplyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SmtpProtocolException)
        {
            // Nothing to do: the message is already accepted.
        }
        catch (IOException)
        {
            // Same: some servers close the socket rather than answering QUIT.
        }

        return accepted;
    }

    /// <summary>
    /// RFC 5321 transparency: a body line that begins with a period gets a second one, or it ends
    /// the message early.
    /// </summary>
    internal static string DotStuff(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return message.StartsWith('.')
            ? "." + message.Replace("\r\n.", "\r\n..", StringComparison.Ordinal)
            : message.Replace("\r\n.", "\r\n..", StringComparison.Ordinal);
    }

    private async Task GreetAsync(CancellationToken cancellationToken)
    {
        await channel.WriteLineAsync($"EHLO {clientName}", cancellationToken).ConfigureAwait(false);
        var reply = await ReadReplyAsync(cancellationToken).ConfigureAwait(false);

        if (!reply.IsPositive)
        {
            // A server too old for EHLO cannot do STARTTLS or AUTH either, so HELO is only useful
            // for an unauthenticated relay - which is a configuration Charter still supports.
            await channel.WriteLineAsync($"HELO {clientName}", cancellationToken).ConfigureAwait(false);
            var fallback = await ReadReplyAsync(cancellationToken).ConfigureAwait(false);

            if (!fallback.IsPositive)
            {
                throw new SmtpProtocolException("The mail server did not accept the greeting.", fallback);
            }

            Extensions = new HashSet<string>(StringComparer.Ordinal);
            return;
        }

        Extensions = ParseExtensions(reply.Text);
    }

    private async Task StartTlsAsync(CancellationToken cancellationToken)
    {
        if (!Extensions.Contains("STARTTLS"))
        {
            throw new SmtpProtocolException(
                "The mail server does not offer STARTTLS, so Charter stopped rather than sending " +
                "credentials over an unencrypted connection. Set CHARTER_SMTP_TLS to implicit for a " +
                "TLS-only port, or to none if this relay is genuinely on a private network.");
        }

        await ExpectAsync("STARTTLS", 220, cancellationToken).ConfigureAwait(false);
        await channel.UpgradeToTlsAsync(cancellationToken).ConfigureAwait(false);

        // The extension list before the handshake is not trustworthy and, per RFC 3207, is discarded.
        await GreetAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task AuthenticateAsync(
        SmtpTlsMode tls,
        string username,
        Secret? password,
        CancellationToken cancellationToken)
    {
        if (tls is not SmtpTlsMode.None && !channel.IsSecure)
        {
            throw new SmtpProtocolException(
                "The connection is not encrypted, so Charter did not send the SMTP password.");
        }

        var mechanisms = AuthMechanisms();
        var secret = password?.Reveal() ?? string.Empty;

        if (mechanisms.Count > 0 && !mechanisms.Contains("PLAIN") && !mechanisms.Contains("LOGIN"))
        {
            throw new SmtpProtocolException(
                "The mail server does not offer a username and password sign-in Charter can use.");
        }

        SmtpReply reply;

        if (mechanisms.Count == 0 || mechanisms.Contains("PLAIN"))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{username}\0{secret}"));
            reply = await CommandAsync($"AUTH PLAIN {token}", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            reply = await CommandAsync("AUTH LOGIN", cancellationToken).ConfigureAwait(false);
            if (reply.IsIntermediate)
            {
                reply = await CommandAsync(
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(username)),
                    cancellationToken).ConfigureAwait(false);
            }

            if (reply.IsIntermediate)
            {
                reply = await CommandAsync(
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (!reply.IsPositive)
        {
            // Deliberately not the server's text: some servers echo the submitted username, and a
            // few echo the whole AUTH argument. The status code is enough to act on.
            throw new SmtpProtocolException(
                "The mail server rejected the username and password.",
                new SmtpReply(reply.Code, $"authentication failed with status {reply.Code}"));
        }
    }

    private IReadOnlySet<string> AuthMechanisms()
    {
        foreach (var extension in Extensions)
        {
            if (extension.StartsWith("AUTH", StringComparison.Ordinal))
            {
                return new HashSet<string>(
                    extension.Split([' ', '='], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Skip(1),
                    StringComparer.Ordinal);
            }
        }

        return new HashSet<string>(StringComparer.Ordinal);
    }

    private async Task ExpectAsync(string command, int code, CancellationToken cancellationToken)
    {
        var reply = await CommandAsync(command, cancellationToken).ConfigureAwait(false);
        if (reply.Code != code)
        {
            throw new SmtpProtocolException($"The mail server refused '{Verb(command)}'.", reply);
        }
    }

    /// <summary>
    /// Writes a command and reads its reply. Nothing in this class logs a command, which is what
    /// makes it safe to pass an AUTH argument through it.
    /// </summary>
    private async Task<SmtpReply> CommandAsync(string command, CancellationToken cancellationToken)
    {
        await channel.WriteLineAsync(command, cancellationToken).ConfigureAwait(false);
        return await ReadReplyAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a reply, following multi-line continuations.</summary>
    private async Task<SmtpReply> ReadReplyAsync(CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        int code;

        while (true)
        {
            var line = await channel.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                       ?? throw new SmtpProtocolException("The mail server closed the connection.", transient: true);

            if (line.Length < 3 ||
                !int.TryParse(line[..3], NumberStyles.None, CultureInfo.InvariantCulture, out code))
            {
                throw new SmtpProtocolException("The mail server sent a response Charter could not read.");
            }

            lines.Add(line.Length > 4 ? line[4..] : string.Empty);

            if (line.Length <= 3 || line[3] != '-')
            {
                break;
            }
        }

        return new SmtpReply(code, string.Join('\n', lines));
    }

    private static IReadOnlySet<string> ParseExtensions(string text)
        => new HashSet<string>(
            text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Skip(1)
                .Select(line => line.ToUpperInvariant()),
            StringComparer.Ordinal);

    /// <summary>The command name alone. Arguments can carry an address or a credential.</summary>
    private static string Verb(string command)
    {
        var space = command.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? command : command[..space];
    }
}
