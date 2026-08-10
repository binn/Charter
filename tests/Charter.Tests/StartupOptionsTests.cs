using Charter.Configuration;
using Charter.Logging;
using Serilog.Events;

namespace Charter.Tests;

/// <summary>
/// Covers the startup configuration rules in sections 4.1 and 19.1: validate once, report every
/// problem at once, and never fall back silently on a bad logging mode.
/// </summary>
public class StartupOptionsTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] pairs)
    {
        var values = pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return name => values.GetValueOrDefault(name);
    }

    [Fact]
    public void AppliesDocumentedDefaults()
    {
        var options = StartupOptions.FromEnvironment(Env());

        Assert.Equal(8080, options.Port);
        Assert.Equal(LoggingMode.Default, options.LoggingMode);
        Assert.Equal(LogEventLevel.Information, options.MinimumLogLevel);
        Assert.Equal("charter", options.ServiceName);
        Assert.False(options.IncludeTranscripts);
        Assert.False(options.SeqEnabled);
        Assert.False(options.OtlpEnabled);
        Assert.Null(options.DatabaseConnectionString);
    }

    [Theory]
    [InlineData("DEFAULT", LoggingMode.Default)]
    [InlineData("json", LoggingMode.Json)]
    [InlineData("Railway_Json", LoggingMode.RailwayJson)]
    public void ParsesLoggingMode(string raw, LoggingMode expected)
    {
        var options = StartupOptions.FromEnvironment(Env(("LOGGING_MODE", raw)));

        Assert.Equal(expected, options.LoggingMode);
    }

    [Fact]
    public void RejectsAnUnknownLoggingModeRatherThanFallingBack()
    {
        var error = Assert.Throws<ConfigException>(
            () => StartupOptions.FromEnvironment(Env(("LOGGING_MODE", "LOGFMT"))));

        Assert.Contains("DEFAULT, JSON, RAILWAY_JSON", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsEveryProblemAtOnce()
    {
        // Section 4.1: print *all* problems at once and exit non-zero. An operator fixing a
        // misconfigured instance one restart at a time is the failure this rule exists to prevent.
        var error = Assert.Throws<ConfigException>(() => StartupOptions.FromEnvironment(Env(
            ("PORT", "not-a-port"),
            ("LOGGING_MODE", "LOGFMT"),
            ("CHARTER_LOG_LEVEL", "chatty"),
            ("DATABASE_URL", "mysql://db.internal/charter"))));

        Assert.Equal(4, error.Problems.Count);
        Assert.Contains(error.Problems, problem => problem.Contains("PORT", StringComparison.Ordinal));
        Assert.Contains(error.Problems, problem => problem.Contains("LOGGING_MODE", StringComparison.Ordinal));
        Assert.Contains(error.Problems, problem => problem.Contains("CHARTER_LOG_LEVEL", StringComparison.Ordinal));
        Assert.Contains(error.Problems, problem => problem.Contains("postgres://", StringComparison.Ordinal));
    }

    [Fact]
    public void ConvertsDatabaseUrlToAnNpgsqlConnectionString()
    {
        var options = StartupOptions.FromEnvironment(Env(
            ("DATABASE_URL", "postgres://charter:hunter2@db.internal:5432/charter?sslmode=disable")));

        Assert.NotNull(options.DatabaseConnectionString);
        Assert.Contains("Host=db.internal", options.DatabaseConnectionString, StringComparison.Ordinal);
        Assert.DoesNotContain("postgres://", options.DatabaseConnectionString, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("eight-thousand")]
    public void RejectsAnInvalidPort(string raw)
    {
        Assert.Throws<ConfigException>(() => StartupOptions.FromEnvironment(Env(("PORT", raw))));
    }

    [Fact]
    public void EnablesSeqAndOtlpIndependently()
    {
        var seqOnly = StartupOptions.FromEnvironment(Env(("CHARTER_SEQ_URL", "http://seq:5341")));
        Assert.True(seqOnly.SeqEnabled);
        Assert.False(seqOnly.OtlpEnabled);

        var otlpOnly = StartupOptions.FromEnvironment(
            Env(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4317")));
        Assert.False(otlpOnly.SeqEnabled);
        Assert.True(otlpOnly.OtlpEnabled);
    }

    [Fact]
    public void ParsesOtlpHeaders()
    {
        var headers = StartupOptions.ParseHeaders("api-key=abc123,x-tenant=acme%20corp");

        Assert.Equal(2, headers.Count);
        Assert.Equal("abc123", headers["api-key"]);
        Assert.Equal("acme corp", headers["x-tenant"]);
    }

    [Fact]
    public void RejectsANonStandardOtlpProtocol()
    {
        Assert.Throws<ConfigException>(
            () => StartupOptions.FromEnvironment(Env(("OTEL_EXPORTER_OTLP_PROTOCOL", "thrift"))));
    }

    [Fact]
    public void ToStringEmitsNoSecretValue()
    {
        // The connection string carries the database password and the Seq API key is a credential;
        // a record's generated ToString would print both into whatever logged it (section 20b.2).
        var options = StartupOptions.FromEnvironment(Env(
            ("DATABASE_URL", "postgres://charter:database-password-do-not-log@db.internal/charter"),
            ("CHARTER_SEQ_URL", "http://seq:5341"),
            ("CHARTER_SEQ_API_KEY", "seq-key-do-not-log")));

        var rendered = options.ToString();

        Assert.DoesNotContain("database-password-do-not-log", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("seq-key-do-not-log", rendered, StringComparison.Ordinal);
        Assert.Contains("Port = 8080", rendered, StringComparison.Ordinal);
        Assert.Contains("http://seq:5341", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void MatchesTheFullConfigForTheVariablesTheyShare()
    {
        // StartupOptions and CharterConfig parse the same variables through the same section
        // parsers. If that ever stops being true, the host and the app disagree about the port.
        var startup = StartupOptions.FromEnvironment(Env(
            ("PORT", "9100"),
            ("LOGGING_MODE", "JSON"),
            ("CHARTER_LOG_LEVEL", "warning"),
            ("OTEL_SERVICE_NAME", "charter-prod"),
            ("DATABASE_URL", "postgres://charter:hunter2@db.internal/charter")));

        var projected = CharterConfig.FromEnvironment(Env(
            ("PORT", "9100"),
            ("LOGGING_MODE", "JSON"),
            ("CHARTER_LOG_LEVEL", "warning"),
            ("OTEL_SERVICE_NAME", "charter-prod"),
            ("DATABASE_URL", "postgres://charter:hunter2@db.internal/charter"),
            ("CHARTER_BASE_URL", "https://charter.example.com"),
            ("CHARTER_SECRET_KEY", ConfigTestEnvironment.SecretKey),
            ("CHARTER_CREDENTIAL_KEY", ConfigTestEnvironment.CredentialKey),
            ("GITHUB_APP_ID", "123456"),
            ("GITHUB_APP_PRIVATE_KEY", ConfigTestEnvironment.PrivateKeyPem),
            ("GITHUB_WEBHOOK_SECRET", "webhook-secret-value"),
            ("ANTHROPIC_API_KEY", "sk-ant-instance-key"))).ToStartupOptions();

        Assert.Equal(startup.Port, projected.Port);
        Assert.Equal(startup.LoggingMode, projected.LoggingMode);
        Assert.Equal(startup.MinimumLogLevel, projected.MinimumLogLevel);
        Assert.Equal(startup.ServiceName, projected.ServiceName);
        Assert.Equal(startup.OtlpProtocol, projected.OtlpProtocol);
        Assert.Equal(startup.DatabaseConnectionString, projected.DatabaseConnectionString);
    }
}
