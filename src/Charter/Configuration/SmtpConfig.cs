namespace Charter.Configuration;

/// <summary>
/// Which email provider this instance delivers through (change spec 001, part C.1).
/// </summary>
/// <remarks>
/// The seam is declared here in full; only <see cref="Smtp"/> is implemented. <see cref="Resend"/>
/// is named so an operator's configuration file does not have to change when it lands, and so
/// setting it produces "not available in this build" rather than the silence of an unrecognised
/// value. SMTP first is the widest coverage per unit of code - SES, Postmark, Mailgun and a
/// self-hosted relay are all one implementation - and it is the only one that can be exercised
/// without a vendor account.
/// </remarks>
public enum EmailProviderKind
{
    /// <summary>Email is off. Charter degrades rather than failing (change spec 001, part C.1).</summary>
    None,

    /// <summary>A mail server reached over SMTP.</summary>
    Smtp,

    /// <summary>Reserved. Declared so the seam is visible; not implemented in this build.</summary>
    Resend,
}

/// <summary>How the SMTP connection is secured.</summary>
public enum SmtpTlsMode
{
    /// <summary>Plaintext. Only defensible for a relay on localhost or a private network.</summary>
    None,

    /// <summary>Connect in the clear, then upgrade with <c>STARTTLS</c> before authenticating.</summary>
    StartTls,

    /// <summary>TLS from the first byte, the historical <c>smtps</c> behaviour on port 465.</summary>
    Implicit,
}

/// <summary>
/// Email delivery, from the <c>CHARTER_EMAIL_*</c> and <c>CHARTER_SMTP_URL</c> variables
/// (sections 4.2, 22; change spec 001, part C.2).
/// </summary>
/// <remarks>
/// <para>
/// Part C.1 requires that <c>none</c> degrades cleanly rather than failing: an instance with no mail
/// server still has to be able to add a colleague. So "no email" is a valid, fully-supported
/// configuration and never a startup error - it only changes what the UI offers, which is why
/// <see cref="Enabled"/> exists rather than a nullable section that callers have to remember to
/// check.
/// </para>
/// <para>
/// Section 4.1 wants every problem reported at once, so this parses the whole block even when the
/// first variable in it is wrong.
/// </para>
/// </remarks>
public sealed record EmailConfig
{
    /// <summary>Default cap on messages to one recipient, per category, per hour.</summary>
    public const int DefaultMaxPerRecipientPerHour = 20;

    /// <summary>What a display name defaults to when nobody set one.</summary>
    public const string DefaultFromName = "Charter";

    /// <summary><c>CHARTER_EMAIL_PROVIDER</c>.</summary>
    public required EmailProviderKind Provider { get; init; }

    /// <summary>The SMTP block, or <c>null</c> when the provider is not <c>smtp</c>.</summary>
    public SmtpConfig? Smtp { get; init; }

    /// <summary>
    /// <c>CHARTER_EMAIL_FROM</c>. Null only when email is off.
    /// </summary>
    public string? FromAddress { get; init; }

    /// <summary><c>CHARTER_EMAIL_FROM_NAME</c>, default <c>Charter</c>.</summary>
    public string FromName { get; init; } = DefaultFromName;

    /// <summary><c>CHARTER_EMAIL_REPLY_TO</c>, or <c>null</c>.</summary>
    public string? ReplyTo { get; init; }

    /// <summary>
    /// <c>CHARTER_EMAIL_MAX_PER_HOUR</c>. Change spec 001 C.3 requires outbound mail to be
    /// rate-limited per recipient so a notification storm cannot happen.
    /// </summary>
    public required int MaxPerRecipientPerHour { get; init; }

    /// <summary>True when a message can actually be delivered.</summary>
    public bool Enabled => Provider is EmailProviderKind.Smtp && Smtp is not null && FromAddress is not null;

    /// <summary>The provider token as it is written in configuration.</summary>
    public string ProviderToken => Provider switch
    {
        EmailProviderKind.Smtp => "smtp",
        EmailProviderKind.Resend => "resend",
        _ => "none",
    };

    internal static EmailConfig Parse(EnvReader reader)
    {
        var provider = ParseProvider(reader);
        var maxPerHour = reader.Int(
            "CHARTER_EMAIL_MAX_PER_HOUR",
            DefaultMaxPerRecipientPerHour,
            1,
            10_000,
            "a message count between 1 and 10000");

        if (provider is not EmailProviderKind.Smtp)
        {
            // Not an error. An operator who set a URL and then turned the provider off has almost
            // certainly not noticed that no mail will be sent, so say so without blocking the boot.
            if (reader.Optional("CHARTER_SMTP_URL") is not null)
            {
                reader.Warn(
                    "CHARTER_EMAIL_PROVIDER",
                    "CHARTER_SMTP_URL is set but CHARTER_EMAIL_PROVIDER is none, so no mail will be " +
                    "sent. Charter will surface invitation links in the UI instead.");
            }

            return new EmailConfig
            {
                Provider = EmailProviderKind.None,
                MaxPerRecipientPerHour = maxPerHour,
            };
        }

        var smtp = SmtpConfig.Parse(reader, required: true);
        var fromName = reader.Optional("CHARTER_EMAIL_FROM_NAME") ?? DefaultFromName;
        var replyTo = ParseAddress(reader, "CHARTER_EMAIL_REPLY_TO", reader.Optional("CHARTER_EMAIL_REPLY_TO"));
        var from = ParseFromAddress(reader, smtp);

        return new EmailConfig
        {
            Provider = EmailProviderKind.Smtp,
            Smtp = smtp,
            FromAddress = from,
            FromName = fromName,
            ReplyTo = replyTo,
            MaxPerRecipientPerHour = maxPerHour,
        };
    }

    /// <summary>
    /// Defaults to <c>smtp</c> when a URL is present, so an existing instance that only ever set
    /// <c>CHARTER_SMTP_URL</c> keeps sending mail without a second variable.
    /// </summary>
    private static EmailProviderKind ParseProvider(EnvReader reader)
    {
        var fallback = reader.Optional("CHARTER_SMTP_URL") is null
            ? EmailProviderKind.None
            : EmailProviderKind.Smtp;

        var provider = reader.Choice("CHARTER_EMAIL_PROVIDER", fallback,
        [
            ("none", EmailProviderKind.None),
            ("smtp", EmailProviderKind.Smtp),
            ("resend", EmailProviderKind.Resend),
        ]);

        if (provider is EmailProviderKind.Resend)
        {
            reader.Error(
                "CHARTER_EMAIL_PROVIDER",
                "CHARTER_EMAIL_PROVIDER=resend is not available in this build. Set it to smtp and " +
                "point CHARTER_SMTP_URL at your provider's SMTP endpoint - Resend, SES, Postmark and " +
                "Mailgun all offer one - or set it to none to run without email.");

            return EmailProviderKind.None;
        }

        return provider;
    }

    /// <summary>
    /// The envelope sender. Guessed from the SMTP endpoint when unset, with a warning: a guessed
    /// sender is usually rejected by the receiving side, and finding that out from a bounce is worse
    /// than reading one line at startup.
    /// </summary>
    private static string? ParseFromAddress(EnvReader reader, SmtpConfig? smtp)
    {
        var configured = reader.Optional("CHARTER_EMAIL_FROM");
        if (configured is not null)
        {
            return ParseAddress(reader, "CHARTER_EMAIL_FROM", configured);
        }

        if (smtp is null)
        {
            return null;
        }

        var guess = GuessFromAddress(smtp);
        reader.Warn(
            "CHARTER_EMAIL_FROM",
            $"CHARTER_EMAIL_FROM is not set, so Charter will send as '{guess}'. Most mail providers " +
            "reject a sender they have not been configured for; set CHARTER_EMAIL_FROM to an address " +
            "on a domain you control.");

        return guess;
    }

    private static string GuessFromAddress(SmtpConfig smtp)
    {
        if (smtp.Username is { } username && username.Contains('@', StringComparison.Ordinal))
        {
            return username;
        }

        var domain = smtp.Host;
        foreach (var label in new[] { "smtp.", "mail.", "email." })
        {
            if (domain.StartsWith(label, StringComparison.OrdinalIgnoreCase) &&
                domain.Count(character => character == '.') > 1)
            {
                domain = domain[label.Length..];
                break;
            }
        }

        return $"charter@{domain}";
    }

    private static string? ParseAddress(EnvReader reader, string variable, string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        if (!IsDeliverableAddress(raw))
        {
            reader.Invalid(variable, "a single email address, for example charter@example.com", raw);
            return null;
        }

        return raw;
    }

    /// <summary>
    /// Deliberately narrow. This is not an RFC 5322 validator - it exists to reject the shapes that
    /// break an SMTP envelope or smuggle a second header, and to catch the operator who pasted a
    /// name and an address into one variable.
    /// </summary>
    internal static bool IsDeliverableAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            return false;
        }

        if (value.Contains('<', StringComparison.Ordinal) || value.Contains('>', StringComparison.Ordinal) ||
            value.Contains(',', StringComparison.Ordinal) || value.Contains(';', StringComparison.Ordinal))
        {
            return false;
        }

        var at = value.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at != value.LastIndexOf('@') || at == value.Length - 1)
        {
            return false;
        }

        var domain = value[(at + 1)..];
        return domain.Contains('.', StringComparison.Ordinal) &&
               !domain.StartsWith('.') &&
               !domain.EndsWith('.');
    }
}

/// <summary>
/// The SMTP endpoint, from <c>CHARTER_SMTP_URL</c> in <c>smtp://user:pass@host:port</c> form
/// (sections 4.2, 22).
/// </summary>
/// <remarks>
/// <c>smtps://</c> is accepted as well and selects implicit TLS. The port defaults per scheme -
/// 587 for <c>smtp</c> (submission with STARTTLS), 465 for <c>smtps</c> - because a URL without a
/// port is common and guessing 25 would send mail unauthenticated.
/// </remarks>
public sealed record SmtpConfig
{
    /// <summary>Default port for <c>smtp://</c>: RFC 6409 submission.</summary>
    public const int DefaultSubmissionPort = 587;

    /// <summary>Default port for <c>smtps://</c>: implicit TLS.</summary>
    public const int DefaultImplicitTlsPort = 465;

    /// <summary>Mail server host.</summary>
    public required string Host { get; init; }

    /// <summary>Mail server port.</summary>
    public required int Port { get; init; }

    /// <summary>Login, when the URL carried credentials.</summary>
    public string? Username { get; init; }

    /// <summary>Password, when the URL carried credentials.</summary>
    public Secret? Password { get; init; }

    /// <summary><c>CHARTER_SMTP_TLS</c>, defaulted from the URL scheme.</summary>
    public required SmtpTlsMode Tls { get; init; }

    /// <summary>True for implicit TLS: encrypted from the first byte rather than via STARTTLS.</summary>
    public bool ImplicitTls => Tls is SmtpTlsMode.Implicit;

    /// <summary>True when the URL carried a login.</summary>
    public bool HasCredentials => Username is not null;

    /// <summary>
    /// Host and port only. The one representation of this endpoint that is safe to log - the URL
    /// itself carries the password, and a redacted URL is one string interpolation away from not
    /// being redacted.
    /// </summary>
    public string Endpoint => $"{Host}:{Port}";

    internal static SmtpConfig? Parse(EnvReader reader, bool required)
    {
        const string expectation =
            "an smtp:// or smtps:// URL, for example smtp://charter:password@smtp.example.com:587";

        var raw = required
            ? reader.Required("CHARTER_SMTP_URL", expectation)
            : reader.Optional("CHARTER_SMTP_URL");

        if (raw is null)
        {
            return null;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("smtp" or "smtps") ||
            string.IsNullOrEmpty(uri.Host))
        {
            reader.Invalid("CHARTER_SMTP_URL", expectation, raw);
            return null;
        }

        var implicitTls = string.Equals(uri.Scheme, "smtps", StringComparison.Ordinal);
        var tls = reader.Choice(
            "CHARTER_SMTP_TLS",
            implicitTls ? SmtpTlsMode.Implicit : SmtpTlsMode.StartTls,
            [
                ("none", SmtpTlsMode.None),
                ("starttls", SmtpTlsMode.StartTls),
                ("implicit", SmtpTlsMode.Implicit),
            ]);

        var userInfo = uri.UserInfo.Split(':', 2);
        var hasCredentials = !string.IsNullOrEmpty(uri.UserInfo);

        if (tls is SmtpTlsMode.None && hasCredentials)
        {
            reader.Warn(
                "CHARTER_SMTP_TLS",
                "CHARTER_SMTP_TLS=none sends the SMTP password in the clear. Only do this for a " +
                "relay you reach over a private network.");
        }

        return new SmtpConfig
        {
            Host = uri.Host,
            Port = uri.Port > 0
                ? uri.Port
                : implicitTls ? DefaultImplicitTlsPort : DefaultSubmissionPort,
            Username = hasCredentials ? Uri.UnescapeDataString(userInfo[0]) : null,
            Password = hasCredentials && userInfo.Length > 1
                ? Secret.From(Uri.UnescapeDataString(userInfo[1]))
                : null,
            Tls = tls,
        };
    }
}
