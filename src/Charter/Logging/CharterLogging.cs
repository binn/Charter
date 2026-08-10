using Charter.Configuration;
using Charter.Diagnostics;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Sinks.OpenTelemetry;

namespace Charter.Logging;

/// <summary>
/// Builds Charter's Serilog pipeline (section 19).
/// </summary>
/// <remarks>
/// Three sinks, independently enabled: Seq when <c>CHARTER_SEQ_URL</c> is set, the console always,
/// and OTLP when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set. Everything is configured in code from
/// <see cref="StartupOptions"/> - there is no <c>appsettings.json</c> to read a Serilog section from.
/// </remarks>
public static class CharterLogging
{
    private const string DefaultConsoleTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";

    public static Serilog.ILogger CreateLogger(StartupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var configuration = new LoggerConfiguration()
            .MinimumLevel.Is(options.MinimumLogLevel)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("service.name", options.ServiceName)
            .Enrich.WithProperty("service.version", BuildInfo.Version)
            .Enrich.WithProperty("charter.commit_sha", BuildInfo.CommitSha);

        ConfigureConsole(configuration, options.LoggingMode);

        // Seq is the primary structured log target: it picks up Serilog's @tr/@sp trace and span ids
        // natively, so one query pulls a whole session and links back to its OTLP trace.
        if (options.SeqEnabled)
        {
            configuration.WriteTo.Seq(options.SeqUrl!, apiKey: options.SeqApiKey);
        }

        if (options.OtlpEnabled)
        {
            configuration.WriteTo.OpenTelemetry(sink =>
            {
                sink.Endpoint = ResolveLogsEndpoint(options);
                sink.Protocol = options.OtlpProtocol == "http/protobuf"
                    ? OtlpProtocol.HttpProtobuf
                    : OtlpProtocol.Grpc;
                sink.ResourceAttributes = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["service.name"] = options.ServiceName,
                    ["service.version"] = BuildInfo.Version,
                    ["charter.commit_sha"] = BuildInfo.CommitSha,
                };

                foreach (var header in options.OtlpHeaders)
                {
                    sink.Headers[header.Key] = header.Value;
                }
            });
        }

        return configuration.CreateLogger();
    }

    private static void ConfigureConsole(LoggerConfiguration configuration, LoggingMode mode)
    {
        switch (mode)
        {
            case LoggingMode.Json:
                configuration.WriteTo.Console(new CompactJsonFormatter());
                break;

            case LoggingMode.RailwayJson:
                configuration.WriteTo.Console(new RailwayJsonFormatter());
                break;

            case LoggingMode.Default:
            default:
                configuration.WriteTo.Console(outputTemplate: DefaultConsoleTemplate);
                break;
        }
    }

    /// <summary>
    /// The OTLP/HTTP log signal lives at <c>/v1/logs</c>; OTLP/gRPC posts to the base endpoint.
    /// </summary>
    private static string ResolveLogsEndpoint(StartupOptions options)
    {
        var endpoint = options.OtlpEndpoint!.TrimEnd('/');
        if (options.OtlpProtocol != "http/protobuf")
        {
            return endpoint;
        }

        return endpoint.EndsWith("/v1/logs", StringComparison.OrdinalIgnoreCase)
            ? endpoint
            : endpoint + "/v1/logs";
    }
}
