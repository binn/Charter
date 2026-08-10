using Charter.Configuration;

namespace Charter.Tests;

/// <summary>
/// Covers the change spec 001 part C.2 configuration block: one provider, a sender identity, a TLS
/// mode, and a rate limit - validated once at startup with every problem reported together
/// (section 4.1).
/// </summary>
public class EmailConfigTests
{
    [Fact]
    public void EmailIsOffByDefaultAndThatIsNotAnError()
    {
        // Part C.1: `none` degrades cleanly. An instance with no mail server is a supported
        // instance, so an empty environment has to produce a valid config, not a startup failure.
        var config = ConfigTestEnvironment.Valid();

        Assert.Equal(EmailProviderKind.None, config.Email.Provider);
        Assert.False(config.Email.Enabled);
        Assert.False(config.SmtpEnabled);
        Assert.Null(config.Smtp);
        Assert.Equal("none", config.Email.ProviderToken);
    }

    [Fact]
    public void SettingOnlyTheUrlTurnsSmtpOn()
    {
        // The variable that already existed keeps working on its own: an operator upgrading into
        // this change should not have to add a second variable to keep sending mail.
        var config = ConfigTestEnvironment.Valid(
            ("CHARTER_SMTP_URL", "smtp://mailer:secret@smtp.example.com:2525"),
            ("CHARTER_EMAIL_FROM", "charter@example.com"));

        Assert.Equal(EmailProviderKind.Smtp, config.Email.Provider);
        Assert.True(config.Email.Enabled);
        Assert.True(config.SmtpEnabled);
        Assert.Equal("smtp.example.com", config.Smtp!.Host);
        Assert.Equal(2525, config.Smtp.Port);
        Assert.Equal(SmtpTlsMode.StartTls, config.Smtp.Tls);
        Assert.Equal("charter@example.com", config.Email.FromAddress);
        Assert.Equal("Charter", config.Email.FromName);
        Assert.Equal(EmailConfig.DefaultMaxPerRecipientPerHour, config.Email.MaxPerRecipientPerHour);
    }

    [Fact]
    public void ReadsTheWholeSenderIdentity()
    {
        var config = ConfigTestEnvironment.Valid(
            ("CHARTER_EMAIL_PROVIDER", "smtp"),
            ("CHARTER_SMTP_URL", "smtps://mailer:secret@smtp.example.com"),
            ("CHARTER_EMAIL_FROM", "charter@example.com"),
            ("CHARTER_EMAIL_FROM_NAME", "Acme Charter"),
            ("CHARTER_EMAIL_REPLY_TO", "support@example.com"),
            ("CHARTER_EMAIL_MAX_PER_HOUR", "4"));

        Assert.Equal("charter@example.com", config.Email.FromAddress);
        Assert.Equal("Acme Charter", config.Email.FromName);
        Assert.Equal("support@example.com", config.Email.ReplyTo);
        Assert.Equal(4, config.Email.MaxPerRecipientPerHour);
        Assert.Equal(SmtpTlsMode.Implicit, config.Smtp!.Tls);
        Assert.True(config.Smtp.ImplicitTls);
        Assert.Equal(SmtpConfig.DefaultImplicitTlsPort, config.Smtp.Port);
    }

    [Theory]
    [InlineData("smtp://smtp.example.com", "none", SmtpTlsMode.None)]
    [InlineData("smtp://smtp.example.com", "implicit", SmtpTlsMode.Implicit)]
    [InlineData("smtps://smtp.example.com", "starttls", SmtpTlsMode.StartTls)]
    public void TheTlsModeOverridesTheScheme(string url, string mode, SmtpTlsMode expected)
    {
        var config = ConfigTestEnvironment.Valid(
            ("CHARTER_SMTP_URL", url),
            ("CHARTER_SMTP_TLS", mode),
            ("CHARTER_EMAIL_FROM", "charter@example.com"));

        Assert.Equal(expected, config.Smtp!.Tls);
    }

    [Fact]
    public void SmtpWithoutAUrlIsAStartupError()
    {
        // Section 4.1: never fail lazily on first use. Choosing a provider and not configuring it is
        // exactly the mistake that would otherwise surface as a silently unsent invitation.
        var result = CharterConfigParser.Parse(
            ConfigTestEnvironment.With(("CHARTER_EMAIL_PROVIDER", "smtp")));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, problem => problem.Variable == "CHARTER_SMTP_URL");
    }

    [Fact]
    public void ResendIsNamedButNotAvailable()
    {
        // Change spec 001, implementation discipline: build the seam, ship one implementation. The
        // token is recognised so the error can say what to do, rather than "unrecognised value".
        var result = CharterConfigParser.Parse(
            ConfigTestEnvironment.With(("CHARTER_EMAIL_PROVIDER", "resend")));

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors, problem => problem.Variable == "CHARTER_EMAIL_PROVIDER");
        Assert.Contains("not available in this build", error.Text, StringComparison.Ordinal);
        Assert.Contains("smtp", error.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TurningTheProviderOffWithAUrlStillSetWarnsRatherThanFails()
    {
        var result = CharterConfigParser.Parse(ConfigTestEnvironment.With(
            ("CHARTER_EMAIL_PROVIDER", "none"),
            ("CHARTER_SMTP_URL", "smtp://mailer:secret@smtp.example.com")));

        Assert.True(result.IsValid);
        Assert.False(result.Config!.Email.Enabled);
        Assert.Contains(
            result.Warnings,
            problem => problem.Text.Contains("no mail will be sent", StringComparison.Ordinal));
    }

    [Fact]
    public void GuessesTheSenderAndSaysSo()
    {
        // A guessed sender is usually rejected by the receiving side. Finding that out from a bounce
        // is worse than reading one line at startup, so it is a warning rather than silence.
        var result = CharterConfigParser.Parse(
            ConfigTestEnvironment.With(("CHARTER_SMTP_URL", "smtp://mailer:secret@smtp.example.com")));

        Assert.True(result.IsValid);
        Assert.Equal("charter@example.com", result.Config!.Email.FromAddress);
        Assert.Contains(result.Warnings, problem => problem.Variable == "CHARTER_EMAIL_FROM");
    }

    [Fact]
    public void PrefersAnAddressUsedAsTheLoginWhenGuessing()
    {
        var result = CharterConfigParser.Parse(ConfigTestEnvironment.With(
            ("CHARTER_SMTP_URL", "smtp://charter%40example.com:secret@smtp.mailprovider.net")));

        Assert.Equal("charter@example.com", result.Config!.Email.FromAddress);
    }

    [Theory]
    [InlineData("Charter <charter@example.com>")]
    [InlineData("charter@localhost")]
    [InlineData("charter.example.com")]
    [InlineData("one@example.com,two@example.com")]
    public void RejectsASenderThatIsNotOneDeliverableAddress(string from)
    {
        var result = CharterConfigParser.Parse(ConfigTestEnvironment.With(
            ("CHARTER_SMTP_URL", "smtp://smtp.example.com"),
            ("CHARTER_EMAIL_FROM", from)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, problem => problem.Variable == "CHARTER_EMAIL_FROM");
    }

    [Fact]
    public void RejectsARateLimitOutsideTheAllowedRange()
    {
        var result = CharterConfigParser.Parse(
            ConfigTestEnvironment.With(("CHARTER_EMAIL_MAX_PER_HOUR", "0")));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, problem => problem.Variable == "CHARTER_EMAIL_MAX_PER_HOUR");
    }

    [Fact]
    public void WarnsWhenCredentialsWouldCrossAnUnencryptedConnection()
    {
        var result = CharterConfigParser.Parse(ConfigTestEnvironment.With(
            ("CHARTER_SMTP_URL", "smtp://mailer:secret@relay.internal"),
            ("CHARTER_SMTP_TLS", "none"),
            ("CHARTER_EMAIL_FROM", "charter@example.com")));

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, problem => problem.Variable == "CHARTER_SMTP_TLS");
    }
}
