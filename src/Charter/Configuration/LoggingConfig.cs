using Charter.Logging;
using Serilog.Events;

namespace Charter.Configuration;

/// <summary>
/// The Serilog pipeline, configured entirely in code from configuration (sections 19, 19.1).
/// </summary>
public sealed record LoggingConfig
{
    /// <summary>Console sink formatting. <c>LOGGING_MODE</c>.</summary>
    public required LoggingMode Mode { get; init; }

    /// <summary>Minimum level for every sink. <c>CHARTER_LOG_LEVEL</c>.</summary>
    public required LogEventLevel MinimumLevel { get; init; }

    /// <summary>Seq ingestion URL. Enables the Seq sink when set. <c>CHARTER_SEQ_URL</c>.</summary>
    public string? SeqUrl { get; init; }

    /// <summary><c>CHARTER_SEQ_API_KEY</c>.</summary>
    public Secret? SeqApiKey { get; init; }

    /// <summary>
    /// <c>CHARTER_LOG_INCLUDE_TRANSCRIPTS</c>. Off by default: transcript bodies in structured log
    /// properties export repository source into the operator's log platform (section 19.2).
    /// </summary>
    public required bool IncludeTranscripts { get; init; }

    /// <summary>True when Seq is configured. Section 19 calls it the primary structured target.</summary>
    public bool SeqEnabled => !string.IsNullOrWhiteSpace(SeqUrl);

    internal static LoggingConfig Parse(EnvReader reader)
    {
        var mode = reader.Choice<LoggingMode>("LOGGING_MODE", LoggingMode.Default,
        [
            ("DEFAULT", LoggingMode.Default),
            ("JSON", LoggingMode.Json),
            ("RAILWAY_JSON", LoggingMode.RailwayJson),
        ]);

        var minimumLevel = LogEventLevel.Information;
        var rawLevel = reader.Optional("CHARTER_LOG_LEVEL");
        if (rawLevel is not null && !Enum.TryParse(rawLevel, ignoreCase: true, out minimumLevel))
        {
            reader.Invalid(
                "CHARTER_LOG_LEVEL",
                "one of verbose, debug, information, warning, error, fatal",
                rawLevel);
            minimumLevel = LogEventLevel.Information;
        }

        return new LoggingConfig
        {
            Mode = mode,
            MinimumLevel = minimumLevel,
            SeqUrl = reader.Optional("CHARTER_SEQ_URL"),
            SeqApiKey = reader.OptionalSecret("CHARTER_SEQ_API_KEY"),
            IncludeTranscripts = reader.Bool("CHARTER_LOG_INCLUDE_TRANSCRIPTS", false),
        };
    }
}

/// <summary>
/// OpenTelemetry export (section 19.2). Standard <c>OTEL_*</c> variables are honoured directly rather
/// than re-prefixed, so an operator's existing collector configuration works unchanged.
/// </summary>
public sealed record TelemetryConfig
{
    /// <summary><c>OTEL_EXPORTER_OTLP_ENDPOINT</c>. Enables OTLP logs, traces and metrics when set.</summary>
    public string? OtlpEndpoint { get; init; }

    /// <summary>Parsed <c>OTEL_EXPORTER_OTLP_HEADERS</c>, in <c>key=value,key2=value2</c> form.</summary>
    public required IReadOnlyDictionary<string, string> OtlpHeaders { get; init; }

    /// <summary><c>OTEL_EXPORTER_OTLP_PROTOCOL</c>: <c>grpc</c> or <c>http/protobuf</c>.</summary>
    public required string OtlpProtocol { get; init; }

    /// <summary><c>OTEL_SERVICE_NAME</c>, default <c>charter</c>.</summary>
    public required string ServiceName { get; init; }

    /// <summary>True when an OTLP collector endpoint is configured.</summary>
    public bool OtlpEnabled => !string.IsNullOrWhiteSpace(OtlpEndpoint);

    internal static TelemetryConfig Parse(EnvReader reader)
    {
        var protocol = reader.Optional("OTEL_EXPORTER_OTLP_PROTOCOL") ?? "grpc";
        if (protocol is not ("grpc" or "http/protobuf"))
        {
            reader.Invalid("OTEL_EXPORTER_OTLP_PROTOCOL", "grpc or http/protobuf", protocol);
            protocol = "grpc";
        }

        return new TelemetryConfig
        {
            OtlpEndpoint = reader.Optional("OTEL_EXPORTER_OTLP_ENDPOINT"),
            OtlpHeaders = StartupOptions.ParseHeaders(reader.Optional("OTEL_EXPORTER_OTLP_HEADERS")),
            OtlpProtocol = protocol,
            ServiceName = reader.Optional("OTEL_SERVICE_NAME") ?? "charter",
        };
    }
}
