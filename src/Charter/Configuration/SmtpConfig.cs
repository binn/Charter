namespace Charter.Configuration;

/// <summary>
/// Outbound email, from <c>CHARTER_SMTP_URL</c> in <c>smtp://user:pass@host:port</c> form
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

    /// <summary>True for <c>smtps://</c>: TLS from the first byte rather than via STARTTLS.</summary>
    public required bool ImplicitTls { get; init; }

    internal static SmtpConfig? Parse(EnvReader reader)
    {
        const string expectation =
            "an smtp:// or smtps:// URL, for example smtp://charter:password@smtp.example.com:587";

        var raw = reader.Optional("CHARTER_SMTP_URL");
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
        var userInfo = uri.UserInfo.Split(':', 2);
        var hasCredentials = !string.IsNullOrEmpty(uri.UserInfo);

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
            ImplicitTls = implicitTls,
        };
    }
}
