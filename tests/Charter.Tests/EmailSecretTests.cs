using Charter.Configuration;
using Charter.Notifications;

namespace Charter.Tests;

/// <summary>
/// SMTP credentials are configuration secrets. Nothing may put one in a log, an exception, a UI
/// payload or a record's <c>ToString()</c> (sections 4.2, 20b.2).
/// </summary>
public class EmailSecretTests
{
    private const string Password = "smtp-password-do-not-log";

    [Fact]
    public void TheSmtpPasswordIsRedactedInEveryStringRepresentation()
    {
        var config = ConfigTestEnvironment.Valid(
            ("CHARTER_SMTP_URL", $"smtp://mailer:{Password}@smtp.example.com:2525"),
            ("CHARTER_EMAIL_FROM", "charter@example.com"));

        // Records generate a ToString that prints every property. Wrapping the value in Secret is
        // what makes that safe, rather than everyone remembering never to interpolate a config.
        foreach (var rendered in new[]
                 {
                     config.ToString(),
                     config.Email.ToString(),
                     config.Smtp!.ToString(),
                     config.Smtp.Password!.ToString(),
                 })
        {
            Assert.DoesNotContain(Password, rendered, StringComparison.Ordinal);
        }

        Assert.Contains(Secret.Placeholder, config.Smtp.ToString(), StringComparison.Ordinal);
        Assert.Equal(Password, config.Smtp.Password.Reveal());
    }

    [Fact]
    public void TheLoggableEndpointCarriesNoCredentialsAtAll()
    {
        // Not a redacted URL - no URL. A redacted string is one interpolation away from not being
        // redacted, and there is no reason a log line ever needs the user info.
        var config = ConfigTestEnvironment.Valid(
            ("CHARTER_SMTP_URL", $"smtp://mailer:{Password}@smtp.example.com:2525"),
            ("CHARTER_EMAIL_FROM", "charter@example.com"));

        Assert.Equal("smtp.example.com:2525", config.Smtp!.Endpoint);
        Assert.DoesNotContain("mailer", config.Smtp.Endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, config.Smtp.Endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingOnTheSendPathLogsTheCredential()
    {
        var config = EmailFixture.Enabled();
        var transport = new StubSmtpTransport
        {
            Next = SmtpTransportResult.Refused(535, "5.7.8 authentication failed", transient: false),
        };

        var provider = new SmtpEmailProvider(config, transport, TimeProvider.System);
        var sender = EmailFixture.Sender(provider, config, out var logger, out var log);

        var result = await sender.SendAsync(EmailFixture.Message(), TestContext.Current.CancellationToken);

        Assert.Equal(EmailDeliveryStatus.Failed, result.Status);

        var everything = string.Join(
            '\n',
            logger.Entries.Select(entry => entry.Message)
                .Concat([result.Summary, result.Detail ?? string.Empty])
                .Concat(log.Recent().Select(record => $"{record.Summary} {record.Detail}")));

        Assert.DoesNotContain(EmailFixture.Password, everything, StringComparison.Ordinal);
        Assert.DoesNotContain("smtp://", everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARejectedSignInReportsTheStatusCodeRatherThanEchoingWhatWasSent()
    {
        // Some servers echo the submitted username, and a few echo the whole AUTH argument. The
        // status code is enough to act on, and it is the only part that is safe to keep.
        var channel = new ScriptedSmtpChannel(
        [
            "220 smtp.example.com ESMTP",
            "250-smtp.example.com",
            "250 AUTH PLAIN LOGIN",
            $"535 5.7.8 rejected credentials for mailer / {Password}",
        ]);

        var conversation = new SmtpConversation(channel);

        var failure = await Assert.ThrowsAsync<SmtpProtocolException>(() => conversation.SendAsync(
            SmtpTlsMode.None,
            "mailer",
            new Secret(Password),
            "charter@example.com",
            "person@example.com",
            "Subject: hi\r\n\r\nhi\r\n",
            TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Password, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, failure.Reply!.Text, StringComparison.Ordinal);
        Assert.Contains("535", failure.Reply.Text, StringComparison.Ordinal);
    }
}
