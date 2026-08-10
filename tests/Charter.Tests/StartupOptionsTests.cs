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
}
