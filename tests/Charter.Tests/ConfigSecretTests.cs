using System.Text;
using Charter.Configuration;

namespace Charter.Tests;

/// <summary>
/// Secret hygiene: no configuration secret may appear in a string representation of the config, and
/// key entropy is measured decoded rather than counted in characters (sections 4.2, 20b.2).
/// </summary>
public class ConfigSecretTests
{
    private const string StorageSecret = "storage-secret-do-not-log";
    private const string WebhookSecret = "webhook-secret-do-not-log";
    private const string SeqApiKey = "seq-key-do-not-log";
    private const string OAuthSecret = "oauth-secret-do-not-log";
    private const string SmtpPassword = "smtp-password-do-not-log";
    private const string DatabasePassword = "database-password-do-not-log";
    private const string AnthropicKey = "sk-ant-do-not-log";
    private const string OtlpHeaderValue = "otlp-token-do-not-log";

    private static CharterConfig ConfigWithEverySecret() => ConfigTestEnvironment.Valid(
        ("DATABASE_URL", $"postgres://charter:{DatabasePassword}@db.internal:5432/charter"),
        ("ANTHROPIC_API_KEY", AnthropicKey),
        ("GITHUB_WEBHOOK_SECRET", WebhookSecret),
        ("CHARTER_SEQ_URL", "http://seq:5341"),
        ("CHARTER_SEQ_API_KEY", SeqApiKey),
        ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4317"),
        ("OTEL_EXPORTER_OTLP_HEADERS", $"authorization={OtlpHeaderValue}"),
        ("CHARTER_OAUTH_GITHUB_ID", "gh-id"),
        ("CHARTER_OAUTH_GITHUB_SECRET", OAuthSecret),
        ("CHARTER_SMTP_URL", $"smtp://mailer:{SmtpPassword}@smtp.example.com:2525"),
        ("CHARTER_STORAGE_ENDPOINT", "https://minio.internal:9000"),
        ("CHARTER_STORAGE_BUCKET", "charter-artifacts"),
        ("CHARTER_STORAGE_ACCESS_KEY", "storage-access"),
        ("CHARTER_STORAGE_SECRET_KEY", StorageSecret));

    [Fact]
    public void ToStringEmitsNoSecretValue()
    {
        var config = ConfigWithEverySecret();

        var rendered = config.ToString();

        string[] secrets =
        [
            ConfigTestEnvironment.SecretKey,
            ConfigTestEnvironment.CredentialKey,
            DatabasePassword,
            AnthropicKey,
            WebhookSecret,
            ConfigTestEnvironment.PrivateKeyPem,
            SeqApiKey,
            OtlpHeaderValue,
            OAuthSecret,
            SmtpPassword,
            StorageSecret,
        ];

        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, rendered, StringComparison.Ordinal);
        }

        // The value is still there to be read deliberately - the point is that printing does not.
        Assert.Equal(AnthropicKey, config.Models.AnthropicApiKey!.Reveal());
        Assert.Contains(Secret.Placeholder, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void EverySectionRedactsIndependently()
    {
        // Sections get logged on their own, not only through the root record.
        var config = ConfigWithEverySecret();

        string[] rendered =
        [
            config.Keys.ToString(),
            config.Database.ToString(),
            config.Models.ToString(),
            config.GitHub.ToString(),
            config.Logging.ToString(),
            config.Telemetry.ToString(),
            config.Auth.ToString(),
            config.Smtp!.ToString(),
            config.Storage!.ToString(),
        ];

        var all = string.Join(Environment.NewLine, rendered);

        Assert.DoesNotContain(ConfigTestEnvironment.SecretKey, all, StringComparison.Ordinal);
        Assert.DoesNotContain(DatabasePassword, all, StringComparison.Ordinal);
        Assert.DoesNotContain(WebhookSecret, all, StringComparison.Ordinal);
        Assert.DoesNotContain(SeqApiKey, all, StringComparison.Ordinal);
        Assert.DoesNotContain(OAuthSecret, all, StringComparison.Ordinal);
        Assert.DoesNotContain(SmtpPassword, all, StringComparison.Ordinal);
        Assert.DoesNotContain(StorageSecret, all, StringComparison.Ordinal);
        Assert.DoesNotContain(OtlpHeaderValue, all, StringComparison.Ordinal);
    }

    [Fact]
    public void StringInterpolationOfASecretRedactsIt()
    {
        var secret = new Secret("hunter2");

        Assert.Equal(Secret.Placeholder, $"{secret}");
        Assert.Equal(Secret.Placeholder, secret.ToString());
        Assert.Equal("hunter2", secret.Reveal());
    }

    [Theory]
    // openssl rand -base64 32: 44 characters, 32 bytes.
    [InlineData("bm90LXJlYWxseS1yYW5kb20tYnV0LWV4YWN0bHktMzI=", 32, true)]
    // 32 characters of base64 alphabet decode to 24 bytes - the trap section 4.2 calls out.
    [InlineData("abcdefghijklmnopqrstuvwxyz012345", 24, true)]
    // Not base64: measured as UTF-8.
    [InlineData("this-is-a-32-plus-byte-secret-value!!!", 38, false)]
    [InlineData("hunter2", 7, false)]
    public void MeasuresEntropyInDecodedBytes(string value, int expectedBytes, bool base64)
    {
        var secret = new Secret(value);

        Assert.Equal(expectedBytes, secret.EntropyBytes);
        Assert.Equal(base64, secret.IsBase64);
    }

    [Fact]
    public void DecodesBase64Text()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello pem"));

        Assert.True(Secret.TryDecodeBase64Text(encoded, out var decoded));
        Assert.Equal("hello pem", decoded);
        Assert.False(Secret.TryDecodeBase64Text("-----BEGIN RSA PRIVATE KEY-----", out _));
    }

    [Fact]
    public void NoPublicPropertyOfASecretExposesTheValue()
    {
        // Serilog destructuring (`{@Config}`) captures public properties rather than calling
        // ToString, so a property carrying the raw value would defeat the redaction above.
        var secret = new Secret("hunter2");

        var exposed = typeof(Secret)
            .GetProperties()
            .Select(property => property.GetValue(secret)?.ToString())
            .Where(value => value is not null && value.Contains("hunter2", StringComparison.Ordinal));

        Assert.Empty(exposed);
    }

    [Fact]
    public void SecretsCompareByValue()
    {
        Assert.Equal(new Secret("same"), new Secret("same"));
        Assert.NotEqual(new Secret("same"), new Secret("different"));
    }
}
