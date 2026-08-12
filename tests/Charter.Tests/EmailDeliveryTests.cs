using Charter.Configuration;
using Charter.Notifications;
using Microsoft.Extensions.Logging;

namespace Charter.Tests;

/// <summary>
/// An <see cref="ISmtpTransport"/> that answers from memory.
/// </summary>
/// <remarks>
/// No test in this suite opens a socket. The transport interface exists precisely so that the
/// interesting half of email - templates, rate limiting, the delivery log, the section 22 fan-out -
/// can be exercised without a mail server, and the protocol itself is covered separately by driving
/// the conversation over a scripted channel.
/// </remarks>
internal sealed class StubSmtpTransport : ISmtpTransport
{
    public List<SmtpEnvelope> Sent { get; } = [];

    public SmtpTransportResult Next { get; set; } = SmtpTransportResult.Ok(250, "2.0.0 Ok: queued as 7F3A");

    public Exception? Throws { get; set; }

    public SmtpEnvelope Last => Sent[^1];

    public Task<SmtpTransportResult> SendAsync(
        SmtpEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        Sent.Add(envelope);

        return Throws is not null
            ? Task.FromException<SmtpTransportResult>(Throws)
            : Task.FromResult(Next);
    }
}

/// <summary>An <see cref="IEmailProvider"/> whose every answer is set by the test.</summary>
internal sealed class StubEmailProvider : IEmailProvider
{
    public List<EmailMessage> Sent { get; } = [];

    public string Name => "stub";

    public bool IsEnabled { get; set; } = true;

    public EmailDeliveryResult Next { get; set; } = EmailDeliveryResult.Sent();

    public Exception? Throws { get; set; }

    public Task<EmailDeliveryResult> SendAsync(
        EmailMessage message,
        CancellationToken cancellationToken = default)
    {
        Sent.Add(message);

        return Throws is not null
            ? Task.FromException<EmailDeliveryResult>(Throws)
            : Task.FromResult(Next);
    }
}

/// <summary>Configurations and messages the email tests share.</summary>
internal static class EmailFixture
{
    public const string Password = "smtp-password-do-not-log";

    public static EmailConfig Enabled(int maxPerHour = 20) => new()
    {
        Provider = EmailProviderKind.Smtp,
        Smtp = new SmtpConfig
        {
            Host = "smtp.example.com",
            Port = 587,
            Username = "mailer",
            Password = new Secret(Password),
            Tls = SmtpTlsMode.StartTls,
        },
        FromAddress = "charter@example.com",
        FromName = "Charter",
        MaxPerRecipientPerHour = maxPerHour,
    };

    public static EmailConfig Disabled() => new()
    {
        Provider = EmailProviderKind.None,
        MaxPerRecipientPerHour = 20,
    };

    public static EmailMessage Message(
        string to = "person@example.com",
        EmailCategory category = EmailCategory.Notification,
        string kind = "needs_input") => new()
        {
            To = EmailAddress.Create(to, "A Person"),
            Content = new EmailContent
            {
                Subject = "A question about your request",
                Text = "Question for you\n\nThere is one thing to check.\n",
                Html = "<p>There is one thing to check.</p>",
            },
            Category = category,
            Kind = kind,
        };

    public static EmailSender Sender(
        IEmailProvider provider,
        EmailConfig config,
        out RecordingLogger<EmailSender> logger,
        out IEmailDeliveryLog deliveryLog,
        TimeProvider? clock = null)
    {
        logger = new RecordingLogger<EmailSender>();
        deliveryLog = new RecentEmailDeliveryLog();
        var time = clock ?? CharterTime.System;

        return new EmailSender(
            provider,
            new EmailRateLimiter(config.MaxPerRecipientPerHour, time),
            deliveryLog,
            config,
            logger,
            time);
    }
}

/// <summary>
/// Covers change spec 001 part C: one provider, <c>none</c> that degrades rather than fails, and
/// delivery failures that are logged and surfaced rather than swallowed.
/// </summary>
public class EmailDeliveryTests
{
    [Fact]
    public async Task NoneDegradesRatherThanThrowing()
    {
        // Part C.1: a self-hoster with no SMTP server must still be able to add a colleague, so the
        // absence of email changes what the UI offers and never whether an operation succeeds.
        var provider = new NullEmailProvider();

        var result = await provider.SendAsync(EmailFixture.Message(), TestContext.Current.CancellationToken);

        Assert.Equal(EmailDeliveryStatus.Skipped, result.Status);
        Assert.False(result.Delivered);
        Assert.False(result.IsFailure);
        Assert.Contains("not set up", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSendPathUnderNoneRecordsTheSkipAndReportsWhy()
    {
        var config = EmailFixture.Disabled();
        var sender = EmailFixture.Sender(new NullEmailProvider(), config, out _, out var log);

        var result = await sender.SendAsync(EmailFixture.Message(), TestContext.Current.CancellationToken);

        Assert.Equal(EmailDeliveryStatus.Skipped, result.Status);
        Assert.False(sender.Availability.Enabled);
        Assert.Equal("none", sender.Availability.Provider);
        Assert.NotNull(sender.Availability.DisabledReason);

        // Part C.1: disabled *with an explanation*, and the explanation names what to change.
        Assert.Contains("CHARTER_SMTP_URL", sender.Availability.HowToEnable!, StringComparison.Ordinal);
        Assert.Equal(EmailDeliveryStatus.Skipped, Assert.Single(log.Recent()).Status);
    }

    [Fact]
    public async Task AnAdminCanStillAddAColleagueWithNoMailServer()
    {
        // The whole point of `none`. The invitation is not sent, the operation still succeeds, and
        // the administrator is handed the one-time link to pass on.
        var config = EmailFixture.Disabled();
        var sender = EmailFixture.Sender(new NullEmailProvider(), config, out _, out _);
        var mailer = new AccountMailer(sender);

        var delivery = await mailer.SendInvitationAsync(
            EmailAddress.Create("newcomer@example.com"),
            Invitation(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(delivery.Emailed);
        Assert.Equal(new Uri("https://charter.example.com/invite/one-time-token"), delivery.LinkToSurface);
        Assert.Contains("Email is not set up", delivery.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedInvitationAlsoFallsBackToTheLink()
    {
        // A mail server that refuses is the same problem as no mail server: someone is holding an
        // account nobody can sign in to. Reporting the failure and stopping would leave them there.
        var config = EmailFixture.Enabled();
        var provider = new StubEmailProvider { Next = EmailDeliveryResult.Failed("The mail server refused this message.") };
        var sender = EmailFixture.Sender(provider, config, out _, out _);
        var mailer = new AccountMailer(sender);

        var delivery = await mailer.SendInvitationAsync(
            EmailAddress.Create("newcomer@example.com"),
            Invitation(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(delivery.Emailed);
        Assert.NotNull(delivery.LinkToSurface);
        Assert.Equal(EmailDeliveryStatus.Failed, delivery.Delivery!.Status);
    }

    [Fact]
    public async Task AResetLinkIsNeverShownToWhoeverTypedTheAddress()
    {
        // Anybody can type anybody's address into a forgot-password form. Surfacing the link there
        // when email is off would turn a missing mail server into account takeover.
        var config = EmailFixture.Disabled();
        var sender = EmailFixture.Sender(new NullEmailProvider(), config, out _, out _);
        var mailer = new AccountMailer(sender);

        var reset = new PasswordResetEmail
        {
            ResetUrl = new Uri("https://charter.example.com/reset/one-time-token"),
            ValidFor = TimeSpan.FromHours(1),
        };

        var toRecipient = await mailer.SendPasswordResetAsync(
            EmailAddress.Create("person@example.com"),
            reset,
            OneTimeLinkAudience.Recipient,
            TestContext.Current.CancellationToken);

        var toAdmin = await mailer.SendPasswordResetAsync(
            EmailAddress.Create("person@example.com"),
            reset,
            OneTimeLinkAudience.Administrator,
            TestContext.Current.CancellationToken);

        Assert.Null(toRecipient.LinkToSurface);
        Assert.Contains("Ask an administrator", toRecipient.Explanation, StringComparison.Ordinal);
        Assert.Equal(reset.ResetUrl, toAdmin.LinkToSurface);
    }

    [Fact]
    public async Task ADeliveryFailureIsLoggedAndSurfacedRatherThanSwallowed()
    {
        // Change spec 001 C.3. Three things have to be true at once: the caller is told, the log has
        // it, and the admin settings list has it.
        var config = EmailFixture.Enabled();
        var provider = new StubEmailProvider
        {
            Next = EmailDeliveryResult.Failed("The mail server refused this message.", "550 5.7.1 Relay denied"),
        };

        var sender = EmailFixture.Sender(provider, config, out var logger, out var log);

        var result = await sender.SendAsync(EmailFixture.Message(), TestContext.Current.CancellationToken);

        Assert.Equal(EmailDeliveryStatus.Failed, result.Status);
        Assert.Equal("550 5.7.1 Relay denied", result.Detail);

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error);

        var recorded = Assert.Single(log.Recent());
        Assert.Equal(EmailDeliveryStatus.Failed, recorded.Status);
        Assert.Equal("person@example.com", recorded.Recipient);
        Assert.Equal("550 5.7.1 Relay denied", recorded.Detail);
        Assert.Same(recorded, log.LastFailure);
    }

    [Fact]
    public async Task AProviderThatThrowsBecomesARecordedFailureRatherThanAnException()
    {
        // A notification is a side effect of a state transition. A transition must not roll back
        // because a mail server was down, and an invitation form must not answer 500.
        var config = EmailFixture.Enabled();
        var provider = new StubEmailProvider { Throws = new InvalidOperationException("socket exploded") };
        var sender = EmailFixture.Sender(provider, config, out var logger, out var log);

        var result = await sender.SendAsync(EmailFixture.Message(), TestContext.Current.CancellationToken);

        Assert.Equal(EmailDeliveryStatus.Failed, result.Status);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Equal(EmailDeliveryStatus.Failed, Assert.Single(log.Recent()).Status);
    }

    [Fact]
    public async Task TheSmtpProviderRendersBothBodiesIntoOneMultipartMessage()
    {
        var transport = new StubSmtpTransport();
        var provider = new SmtpEmailProvider(EmailFixture.Enabled(), transport, CharterTime.System);

        var result = await provider.SendAsync(EmailFixture.Message(), TestContext.Current.CancellationToken);

        Assert.True(result.Delivered);
        Assert.Equal("charter@example.com", transport.Last.From);
        Assert.Equal("person@example.com", transport.Last.To);

        var message = transport.Last.Message;
        Assert.Contains("multipart/alternative", message, StringComparison.Ordinal);
        Assert.Contains("text/plain; charset=utf-8", message, StringComparison.Ordinal);
        Assert.Contains("text/html; charset=utf-8", message, StringComparison.Ordinal);
        Assert.Contains("From: \"Charter\" <charter@example.com>", message, StringComparison.Ordinal);
        Assert.Contains("Subject: A question about your request", message, StringComparison.Ordinal);

        // Section 22 mail is machine-sent; an auto-reply loop with a shared inbox is a real outage.
        Assert.Contains("Auto-Submitted: auto-generated", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASubjectCannotSmuggleASecondHeader()
    {
        var transport = new StubSmtpTransport();
        var provider = new SmtpEmailProvider(EmailFixture.Enabled(), transport, CharterTime.System);

        var message = EmailFixture.Message() with
        {
            Content = new EmailContent
            {
                Subject = "Ready to try\r\nBcc: attacker@example.net",
                Text = "text",
                Html = "<p>html</p>",
            },
        };

        _ = await provider.SendAsync(message, TestContext.Current.CancellationToken);

        // The injected text survives as part of the subject, which is harmless. What must not
        // survive is the line break: a header can only start at the beginning of a line, so
        // collapsing the CRLF is what stops the second half being read as a Bcc.
        Assert.DoesNotContain("\r\nBcc:", transport.Last.Message, StringComparison.Ordinal);
        Assert.Contains(
            "Subject: Ready to try  Bcc: attacker@example.net\r\n",
            transport.Last.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedMessageSaysWhetherItIsWorthTryingAgain()
    {
        var transport = new StubSmtpTransport
        {
            Next = SmtpTransportResult.Refused(451, "4.3.0 try later", transient: true),
        };

        var provider = new SmtpEmailProvider(EmailFixture.Enabled(), transport, CharterTime.System);

        var result = await provider.SendAsync(EmailFixture.Message(), TestContext.Current.CancellationToken);

        Assert.Equal(EmailDeliveryStatus.Failed, result.Status);
        Assert.Contains("may accept it later", result.Summary, StringComparison.Ordinal);
        Assert.Equal("4.3.0 try later", result.Detail);
    }

    [Fact]
    public void TheSmtpProviderRefusesToExistWithoutAnEnabledConfiguration()
    {
        // Which provider is registered is decided once, at startup. Constructing the SMTP one from a
        // `none` configuration is a wiring bug, and it should fail there rather than on first send.
        var transport = new StubSmtpTransport();

        Assert.Throws<ArgumentException>(() =>
            new SmtpEmailProvider(EmailFixture.Disabled(), transport, CharterTime.System));
    }

    [Fact]
    public async Task TheTestEmailUsesTheSamePathARealMessageTakes()
    {
        // Change spec 001 C.3 asks for this button because misconfiguration is otherwise discovered
        // when an invitation silently fails. A test that bypassed the send path would prove nothing.
        var transport = new StubSmtpTransport();
        var provider = new SmtpEmailProvider(EmailFixture.Enabled(), transport, CharterTime.System);
        var sender = EmailFixture.Sender(provider, EmailFixture.Enabled(), out _, out var log);
        var tester = new EmailTester(sender, CharterTime.System, "charter.example.com");

        var result = await tester.SendTestAsync("admin@example.com", TestContext.Current.CancellationToken);

        Assert.True(result.Sent);
        Assert.Equal("admin@example.com", result.Recipient);
        Assert.Equal("test", Assert.Single(log.Recent()).Kind);
        Assert.Contains("Charter test email", transport.Last.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTestEmailExplainsItselfWhenEmailIsOff()
    {
        var sender = EmailFixture.Sender(new NullEmailProvider(), EmailFixture.Disabled(), out _, out _);
        var tester = new EmailTester(sender, CharterTime.System, "charter.example.com");

        var result = await tester.SendTestAsync("admin@example.com", TestContext.Current.CancellationToken);

        Assert.False(result.Sent);
        Assert.Contains("Email is not set up", result.Message, StringComparison.Ordinal);
        Assert.Contains("CHARTER_EMAIL_PROVIDER", result.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTestEmailRejectsSomethingThatIsNotAnAddress()
    {
        var sender = EmailFixture.Sender(new StubEmailProvider(), EmailFixture.Enabled(), out _, out _);
        var tester = new EmailTester(sender, CharterTime.System, "charter.example.com");

        var result = await tester.SendTestAsync("not-an-address", TestContext.Current.CancellationToken);

        Assert.False(result.Sent);
        Assert.Contains("does not look like an email address", result.Message, StringComparison.Ordinal);
    }

    private static InvitationEmail Invitation() => new()
    {
        InviterName = "Priya",
        OrganizationName = "Acme",
        AcceptUrl = new Uri("https://charter.example.com/invite/one-time-token"),
        ExpiresAt = new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.Zero),
    };
}
