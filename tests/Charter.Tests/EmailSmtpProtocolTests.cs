using Charter.Configuration;
using Charter.Notifications;

namespace Charter.Tests;

/// <summary>
/// An <see cref="ISmtpChannel"/> that reads from a script and remembers what was written.
/// </summary>
/// <remarks>
/// No socket is opened anywhere in this suite. The SMTP conversation - greeting, EHLO, STARTTLS,
/// AUTH, envelope, DATA - is where the bugs live, and it is exactly the part a test cannot reach if
/// the client owns its own connection. Splitting the channel out is what makes it reachable.
/// </remarks>
internal sealed class ScriptedSmtpChannel : ISmtpChannel
{
    private readonly Queue<string> replies;

    public ScriptedSmtpChannel(IEnumerable<string> replies, bool secure = false)
    {
        this.replies = new Queue<string>(replies);
        IsSecure = secure;
    }

    public List<string> Commands { get; } = [];

    public string Body { get; private set; } = string.Empty;

    public bool IsSecure { get; private set; }

    public bool Upgraded { get; private set; }

    public bool Disposed { get; private set; }

    public Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        => Task.FromResult(replies.Count > 0 ? replies.Dequeue() : null);

    public Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        Commands.Add(line);
        return Task.CompletedTask;
    }

    public Task WriteRawAsync(string text, CancellationToken cancellationToken)
    {
        Body += text;
        return Task.CompletedTask;
    }

    public Task UpgradeToTlsAsync(CancellationToken cancellationToken)
    {
        Upgraded = true;
        IsSecure = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The SMTP exchange itself, driven over a scripted channel rather than a network.
/// </summary>
public class EmailSmtpProtocolTests
{
    private const string Message = "Subject: hello\r\n\r\nbody\r\n";

    [Fact]
    public async Task UpgradesWithStartTlsBeforeAuthenticating()
    {
        var channel = new ScriptedSmtpChannel(
        [
            "220 smtp.example.com ESMTP",
            "250-smtp.example.com",
            "250-STARTTLS",
            "250 AUTH PLAIN LOGIN",
            "220 2.0.0 Ready to start TLS",
            "250-smtp.example.com",
            "250 AUTH PLAIN LOGIN",
            "235 2.7.0 Authentication successful",
            "250 2.1.0 Ok",
            "250 2.1.5 Ok",
            "354 End data with <CR><LF>.<CR><LF>",
            "250 2.0.0 Ok: queued as 7F3A",
            "221 2.0.0 Bye",
        ]);

        var reply = await Send(channel, SmtpTlsMode.StartTls, "mailer", new Secret("secret"));

        Assert.Equal(250, reply.Code);
        Assert.True(channel.Upgraded);

        var starttls = channel.Commands.IndexOf("STARTTLS");
        var auth = channel.Commands.FindIndex(command => command.StartsWith("AUTH", StringComparison.Ordinal));

        Assert.True(starttls >= 0);
        Assert.True(auth > starttls, "credentials must not be sent before the connection is encrypted");

        // RFC 3207: the extension list from before the handshake is not trustworthy, so EHLO runs
        // again afterwards.
        Assert.Equal(2, channel.Commands.Count(command => command.StartsWith("EHLO", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RefusesToFallBackToPlaintextWhenStartTlsWasAskedForAndNotOffered()
    {
        // Failing closed. Carrying on unencrypted is how a password ends up on the wire in front of
        // whoever is between Charter and the relay, and it would happen silently.
        var channel = new ScriptedSmtpChannel(
        [
            "220 smtp.example.com ESMTP",
            "250-smtp.example.com",
            "250 AUTH PLAIN LOGIN",
        ]);

        var failure = await Assert.ThrowsAsync<SmtpProtocolException>(
            () => Send(channel, SmtpTlsMode.StartTls, "mailer", new Secret("secret")));

        Assert.Contains("does not offer STARTTLS", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            channel.Commands,
            command => command.StartsWith("AUTH", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendsTheEnvelopeAndTheBodyInOrder()
    {
        var channel = new ScriptedSmtpChannel(
        [
            "220 smtp.example.com ESMTP",
            "250 smtp.example.com",
            "250 2.1.0 Ok",
            "250 2.1.5 Ok",
            "354 End data",
            "250 2.0.0 Ok: queued as 7F3A",
            "221 Bye",
        ]);

        var reply = await Send(channel, SmtpTlsMode.None, username: null, password: null);

        Assert.Equal(250, reply.Code);
        Assert.Contains("queued as 7F3A", reply.Text, StringComparison.Ordinal);

        Assert.Equal(
            ["EHLO charter", "MAIL FROM:<charter@example.com>", "RCPT TO:<person@example.com>", "DATA", ".", "QUIT"],
            channel.Commands);

        Assert.Equal(Message, channel.Body);
    }

    [Fact]
    public async Task ARejectedRecipientIsReportedRatherThanTreatedAsDelivered()
    {
        var channel = new ScriptedSmtpChannel(
        [
            "220 smtp.example.com ESMTP",
            "250 smtp.example.com",
            "250 2.1.0 Ok",
            "550 5.1.1 No such user here",
        ]);

        var failure = await Assert.ThrowsAsync<SmtpProtocolException>(
            () => Send(channel, SmtpTlsMode.None, username: null, password: null));

        Assert.Contains("recipient", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(550, failure.Reply!.Code);
        Assert.False(failure.Transient);
    }

    [Fact]
    public async Task ATemporaryRefusalIsMarkedAsWorthRetrying()
    {
        var channel = new ScriptedSmtpChannel(
        [
            "220 smtp.example.com ESMTP",
            "250 smtp.example.com",
            "450 4.3.2 Try again later",
        ]);

        var failure = await Assert.ThrowsAsync<SmtpProtocolException>(
            () => Send(channel, SmtpTlsMode.None, username: null, password: null));

        Assert.True(failure.Transient);
    }

    [Fact]
    public async Task AServerThatHangsUpIsATransientFailureRatherThanACrash()
    {
        var channel = new ScriptedSmtpChannel(["220 smtp.example.com ESMTP"]);

        var failure = await Assert.ThrowsAsync<SmtpProtocolException>(
            () => Send(channel, SmtpTlsMode.None, username: null, password: null));

        Assert.Contains("closed the connection", failure.Message, StringComparison.Ordinal);
        Assert.True(failure.Transient);
    }

    [Fact]
    public async Task AMessageIsStillDeliveredWhenTheServerWillNotAnswerQuit()
    {
        // The message is already accepted at that point. Failing here would report a delivery that
        // happened as a delivery that did not, and the retry would send it twice.
        var channel = new ScriptedSmtpChannel(
        [
            "220 smtp.example.com ESMTP",
            "250 smtp.example.com",
            "250 2.1.0 Ok",
            "250 2.1.5 Ok",
            "354 End data",
            "250 2.0.0 Ok: queued as 7F3A",
        ]);

        var reply = await Send(channel, SmtpTlsMode.None, username: null, password: null);

        Assert.Equal(250, reply.Code);
    }

    [Fact]
    public void ALeadingPeriodIsStuffedSoTheMessageCannotEndEarly()
    {
        Assert.Equal(
            "..hidden\r\n..also\r\nfine\r\n",
            SmtpConversation.DotStuff(".hidden\r\n.also\r\nfine\r\n"));
    }

    private static Task<SmtpReply> Send(
        ScriptedSmtpChannel channel,
        SmtpTlsMode tls,
        string? username,
        Secret? password) => new SmtpConversation(channel).SendAsync(
            tls,
            username,
            password,
            "charter@example.com",
            "person@example.com",
            Message,
            TestContext.Current.CancellationToken);
}
