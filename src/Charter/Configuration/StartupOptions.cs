using System.Globalization;
using System.Text;
using Charter.Logging;
using Serilog.Events;

namespace Charter.Configuration;

/// <summary>
/// The subset of Charter's configuration needed to stand up the host: port, logging pipeline,
/// telemetry export and the database connection.
/// </summary>
/// <remarks>
/// <para>
/// Section 4.1: all configuration comes from flat, conventional environment variables; a hand-written
/// parser loads and validates it once at startup into an immutable record registered as a singleton;
/// and validation reports every problem at once rather than failing lazily on first use.
/// </para>
/// <para>
/// This type deliberately covers only what the host needs to boot: Serilog, the listening port and
/// the database connection are all needed before a container exists to resolve anything from. The
/// full <see cref="CharterConfig"/> of section 4.2 sits alongside it and parses the same variables
/// through the same section parsers, so the two cannot drift; <see cref="CharterConfig.ToStartupOptions"/>
/// projects this subset out of it once the full config has been validated.
/// </para>
/// <para>
/// One deliberate difference: <c>DATABASE_URL</c> is optional here and required in
/// <see cref="CharterConfig"/> (section 4.2). The host boots without it so that <c>/ready</c> can
/// report the missing variable, rather than the process dying before it can say anything at all.
/// </para>
/// </remarks>
public sealed record StartupOptions
{
    /// <summary>HTTP port. <c>PORT</c>, default 8080 (PaaS convention).</summary>
    public required int Port { get; init; }

    /// <summary>Console sink formatting. <c>LOGGING_MODE</c>.</summary>
    public required LoggingMode LoggingMode { get; init; }

    /// <summary>Minimum level for every sink. <c>CHARTER_LOG_LEVEL</c>.</summary>
    public required LogEventLevel MinimumLogLevel { get; init; }

    /// <summary>Seq ingestion URL. Enables the Seq sink when set. <c>CHARTER_SEQ_URL</c>.</summary>
    public string? SeqUrl { get; init; }

    /// <summary><c>CHARTER_SEQ_API_KEY</c>.</summary>
    public string? SeqApiKey { get; init; }

    /// <summary>Standard <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>. Enables OTLP logs, traces and metrics.</summary>
    public string? OtlpEndpoint { get; init; }

    /// <summary>Parsed <c>OTEL_EXPORTER_OTLP_HEADERS</c>, in <c>key=value,key2=value2</c> form.</summary>
    public required IReadOnlyDictionary<string, string> OtlpHeaders { get; init; }

    /// <summary>Standard <c>OTEL_EXPORTER_OTLP_PROTOCOL</c>: <c>grpc</c> or <c>http/protobuf</c>.</summary>
    public required string OtlpProtocol { get; init; }

    /// <summary>Standard <c>OTEL_SERVICE_NAME</c>, default <c>charter</c>.</summary>
    public required string ServiceName { get; init; }

    /// <summary>Npgsql connection string derived from <c>DATABASE_URL</c> (section 4.3).</summary>
    public string? DatabaseConnectionString { get; init; }

    /// <summary>
    /// <c>CHARTER_LOG_INCLUDE_TRANSCRIPTS</c>. Off by default: transcript bodies in structured log
    /// properties export repository source into the operator's log platform (section 19.2).
    /// </summary>
    public required bool IncludeTranscripts { get; init; }

    /// <summary>True when an OTLP collector endpoint is configured.</summary>
    public bool OtlpEnabled => !string.IsNullOrWhiteSpace(OtlpEndpoint);

    /// <summary>True when Seq is configured.</summary>
    public bool SeqEnabled => !string.IsNullOrWhiteSpace(SeqUrl);

    /// <summary>Reads and validates the host configuration from the process environment.</summary>
    /// <exception cref="ConfigException">
    /// One or more variables are invalid. Every problem found is reported together.
    /// </exception>
    public static StartupOptions FromEnvironment()
        => FromEnvironment(name => Environment.GetEnvironmentVariable(name));

    /// <summary>Reads and validates the host configuration from an arbitrary variable source.</summary>
    /// <exception cref="ConfigException">
    /// One or more variables are invalid. Every problem found is reported together.
    /// </exception>
    public static StartupOptions FromEnvironment(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        // The same accumulating reader and the same section parsers the full CharterConfig uses, so
        // the boot subset and the full config cannot disagree about a value or about its message.
        var reader = new EnvReader(read);

        var port = reader.Int("PORT", 8080, 1, 65535, "a TCP port between 1 and 65535");
        var logging = LoggingConfig.Parse(reader);
        var telemetry = TelemetryConfig.Parse(reader);
        var database = DatabaseConfig.Parse(reader, required: false);

        // Section 4.1: every problem at once, not the first one found.
        var blocking = reader.Problems
            .Where(problem => problem.Severity == ConfigSeverity.Error)
            .Select(problem => problem.Text)
            .ToList();

        if (blocking.Count > 0)
        {
            throw new ConfigException(blocking);
        }

        return new StartupOptions
        {
            Port = port,
            LoggingMode = logging.Mode,
            MinimumLogLevel = logging.MinimumLevel,
            SeqUrl = logging.SeqUrl,
            SeqApiKey = logging.SeqApiKey?.Reveal(),
            OtlpEndpoint = telemetry.OtlpEndpoint,
            OtlpHeaders = telemetry.OtlpHeaders,
            OtlpProtocol = telemetry.OtlpProtocol,
            ServiceName = telemetry.ServiceName,
            DatabaseConnectionString = database?.ConnectionString.Reveal(),
            IncludeTranscripts = logging.IncludeTranscripts,
        };
    }

    /// <summary>
    /// A redacted summary. The connection string carries the database password and
    /// <see cref="SeqApiKey"/> is a credential, so neither may appear in a log line, a crash dump or
    /// an error page (section 20b.2).
    /// </summary>
    public override string ToString()
    {
        var text = new StringBuilder("StartupOptions { ");
        text.Append(CultureInfo.InvariantCulture, $"Port = {Port}, ");
        text.Append(CultureInfo.InvariantCulture, $"LoggingMode = {LoggingMode}, ");
        text.Append(CultureInfo.InvariantCulture, $"MinimumLogLevel = {MinimumLogLevel}, ");
        text.Append(CultureInfo.InvariantCulture, $"SeqUrl = {SeqUrl ?? "(unset)"}, ");
        text.Append(CultureInfo.InvariantCulture, $"SeqApiKey = {Redact(SeqApiKey)}, ");
        text.Append(CultureInfo.InvariantCulture, $"OtlpEndpoint = {OtlpEndpoint ?? "(unset)"}, ");
        text.Append(CultureInfo.InvariantCulture, $"OtlpHeaders = {OtlpHeaders.Count} header(s), ");
        text.Append(CultureInfo.InvariantCulture, $"OtlpProtocol = {OtlpProtocol}, ");
        text.Append(CultureInfo.InvariantCulture, $"ServiceName = {ServiceName}, ");
        text.Append(CultureInfo.InvariantCulture, $"DatabaseConnectionString = {Redact(DatabaseConnectionString)}, ");
        text.Append(CultureInfo.InvariantCulture, $"IncludeTranscripts = {IncludeTranscripts} }}");
        return text.ToString();
    }

    private static string Redact(string? value)
        => value is null ? "(unset)" : Secret.Placeholder;

    /// <summary>Parses the W3C-Baggage-shaped <c>OTEL_EXPORTER_OTLP_HEADERS</c> value.</summary>
    internal static IReadOnlyDictionary<string, string> ParseHeaders(string? raw)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return headers;
        }

        foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = pair[..separator].Trim();
            var value = pair[(separator + 1)..].Trim();
            if (key.Length > 0)
            {
                headers[key] = Uri.UnescapeDataString(value);
            }
        }

        return headers;
    }
}
