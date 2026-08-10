using System.Globalization;
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
/// This type deliberately covers only what the host needs to boot. The full <c>CharterConfig</c> of
/// section 4.2 - GitHub App credentials, model selection, budgets, OAuth providers - is a later
/// deliverable and belongs alongside this parser, not in place of it.
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

        var problems = new List<string>();

        var port = 8080;
        var rawPort = Trimmed(read("PORT"));
        if (rawPort is not null &&
            (!int.TryParse(rawPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) ||
             port is < 1 or > 65535))
        {
            problems.Add($"PORT must be a TCP port between 1 and 65535, got '{rawPort}'");
            port = 8080;
        }

        var loggingMode = LoggingMode.Default;
        var rawLoggingMode = Trimmed(read("LOGGING_MODE"));
        if (rawLoggingMode is not null)
        {
            loggingMode = rawLoggingMode.ToUpperInvariant() switch
            {
                "DEFAULT" => LoggingMode.Default,
                "JSON" => LoggingMode.Json,
                "RAILWAY_JSON" => LoggingMode.RailwayJson,
                _ => Invalid(),
            };

            LoggingMode Invalid()
            {
                problems.Add(
                    $"LOGGING_MODE must be one of DEFAULT, JSON, RAILWAY_JSON, got '{rawLoggingMode}'");
                return LoggingMode.Default;
            }
        }

        var minimumLevel = LogEventLevel.Information;
        var rawLevel = Trimmed(read("CHARTER_LOG_LEVEL"));
        if (rawLevel is not null && !Enum.TryParse(rawLevel, ignoreCase: true, out minimumLevel))
        {
            problems.Add(
                "CHARTER_LOG_LEVEL must be one of verbose, debug, information, warning, error, fatal, " +
                $"got '{rawLevel}'");
            minimumLevel = LogEventLevel.Information;
        }

        string? connectionString = null;
        var rawDatabaseUrl = Trimmed(read("DATABASE_URL"));
        if (rawDatabaseUrl is not null)
        {
            try
            {
                connectionString = DatabaseUrl.ToNpgsql(rawDatabaseUrl);
            }
            catch (ConfigException ex)
            {
                problems.AddRange(ex.Problems);
            }
        }

        var otlpProtocol = Trimmed(read("OTEL_EXPORTER_OTLP_PROTOCOL")) ?? "grpc";
        if (otlpProtocol is not ("grpc" or "http/protobuf"))
        {
            problems.Add(
                $"OTEL_EXPORTER_OTLP_PROTOCOL must be grpc or http/protobuf, got '{otlpProtocol}'");
            otlpProtocol = "grpc";
        }

        var includeTranscripts = false;
        var rawIncludeTranscripts = Trimmed(read("CHARTER_LOG_INCLUDE_TRANSCRIPTS"));
        if (rawIncludeTranscripts is not null && !bool.TryParse(rawIncludeTranscripts, out includeTranscripts))
        {
            problems.Add(
                $"CHARTER_LOG_INCLUDE_TRANSCRIPTS must be true or false, got '{rawIncludeTranscripts}'");
            includeTranscripts = false;
        }

        if (problems.Count > 0)
        {
            throw new ConfigException(problems);
        }

        return new StartupOptions
        {
            Port = port,
            LoggingMode = loggingMode,
            MinimumLogLevel = minimumLevel,
            SeqUrl = Trimmed(read("CHARTER_SEQ_URL")),
            SeqApiKey = Trimmed(read("CHARTER_SEQ_API_KEY")),
            OtlpEndpoint = Trimmed(read("OTEL_EXPORTER_OTLP_ENDPOINT")),
            OtlpHeaders = ParseHeaders(Trimmed(read("OTEL_EXPORTER_OTLP_HEADERS"))),
            OtlpProtocol = otlpProtocol,
            ServiceName = Trimmed(read("OTEL_SERVICE_NAME")) ?? "charter",
            DatabaseConnectionString = connectionString,
            IncludeTranscripts = includeTranscripts,
        };
    }

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
