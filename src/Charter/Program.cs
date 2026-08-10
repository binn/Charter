using Charter.Configuration;
using Charter.Diagnostics;
using Charter.Logging;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;

// Section 4.1: validate everything once at startup and, if invalid, print *all* problems at once and
// exit non-zero. This runs before the logging pipeline exists, so it writes to stderr directly.
StartupOptions options;
try
{
    options = StartupOptions.FromEnvironment();
}
catch (ConfigException ex)
{
    await Console.Error.WriteLineAsync("Charter cannot start. Fix the following configuration problems:");
    foreach (var problem in ex.Problems)
    {
        await Console.Error.WriteLineAsync($"  - {problem}");
    }

    return 1;
}

Log.Logger = CharterLogging.CreateLogger(options);

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Section 4.1: no appsettings.json, and no Section__Nested__Key convention. Clearing the default
    // providers makes that structural rather than a rule someone has to remember.
    builder.Configuration.Sources.Clear();
    builder.Configuration.AddEnvironmentVariables();
    builder.Configuration.AddCommandLine(args);

    // Section 2.3: one HTTP port.
    builder.WebHost.UseUrls($"http://0.0.0.0:{options.Port}");

    // Section 31: graceful shutdown - drain in-flight work before the process goes away.
    builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(30));

    builder.Services.AddSerilog();
    builder.Services.AddSingleton(options);
    builder.Services.AddProblemDetails();

    builder.Services
        .AddOpenTelemetry()
        .ConfigureResource(resource => resource
            .AddService(options.ServiceName, serviceVersion: BuildInfo.Version)
            .AddAttributes([new KeyValuePair<string, object>("charter.commit_sha", BuildInfo.CommitSha)]))
        .WithTracing(tracing =>
        {
            tracing
                .AddSource(CharterTelemetry.ActivitySourceName)
                .AddAspNetCoreInstrumentation(instrumentation =>
                    instrumentation.Filter = context => !IsProbe(context.Request.Path))
                .AddHttpClientInstrumentation()
                .AddNpgsql();

            if (options.OtlpEnabled)
            {
                tracing.AddOtlpExporter();
            }
        })
        .WithMetrics(metrics =>
        {
            metrics
                .AddMeter(CharterTelemetry.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation();

            if (options.OtlpEnabled)
            {
                metrics.AddOtlpExporter();
            }
        });

    var app = builder.Build();

    app.UseSerilogRequestLogging(logging =>
    {
        // Probes fire every few seconds on every PaaS. Logging them at Information drowns everything.
        logging.GetLevel = (context, _, exception) => exception is not null
            ? LogEventLevel.Error
            : IsProbe(context.Request.Path)
                ? LogEventLevel.Verbose
                : LogEventLevel.Information;
    });

    // Section 3.1: the SPA is served from the same origin as the API. In Development,
    // Microsoft.AspNetCore.SpaProxy starts Vite and proxies unmatched requests to it, so HMR works
    // from `dotnet run` alone. In production these are the files Vite emitted into wwwroot.
    app.UseDefaultFiles();
    app.UseStaticFiles();

    // Section 31: /health and /ready. Liveness never touches a dependency - a database blip must not
    // make the platform kill an otherwise healthy container.
    app.MapGet("/health", () => Results.Json(new HealthResponse(
        Status: "ok",
        Version: BuildInfo.Version,
        Commit: BuildInfo.CommitSha)));

    app.MapGet("/ready", async (
        StartupOptions startup,
        ILogger<Program> logger,
        CancellationToken cancellationToken) =>
    {
        if (startup.DatabaseConnectionString is null)
        {
            CharterTelemetry.ReadinessChecks.Add(1, new KeyValuePair<string, object?>("result", "unconfigured"));
            return Results.Json(
                new ReadinessResponse("not_ready", "DATABASE_URL is not configured"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            await using var connection = new NpgsqlConnection(startup.DatabaseConnectionString);
            await connection.OpenAsync(timeout.Token);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            _ = await command.ExecuteScalarAsync(timeout.Token);

            CharterTelemetry.ReadinessChecks.Add(1, new KeyValuePair<string, object?>("result", "ready"));
            return Results.Json(new ReadinessResponse("ready", null));
        }
        catch (Exception ex) when (ex is NpgsqlException or OperationCanceledException or TimeoutException)
        {
            CharterTelemetry.ReadinessChecks.Add(1, new KeyValuePair<string, object?>("result", "unreachable"));
            logger.LogWarning(ex, "Readiness check failed: Postgres is not reachable");
            return Results.Json(
                new ReadinessResponse("not_ready", "the database is not reachable"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    });

    // Section 24: AGPL section 13 compliance. The UI footer renders a persistent Source link from
    // this, pointing at the exact commit this instance is running.
    app.MapGet("/api/instance", (StartupOptions startup) => Results.Json(new InstanceResponse(
        Version: BuildInfo.Version,
        Commit: BuildInfo.CommitSha,
        BuildDate: BuildInfo.BuildDate,
        SourceUrl: BuildInfo.SourceLink,
        License: "AGPL-3.0-only",
        ServiceName: startup.ServiceName)));

    // Client-side routing: anything not matched above is the SPA's problem.
    app.MapFallbackToFile("index.html");

    Log.Information(
        "Charter {Version} ({Commit}) listening on port {Port}; logging mode {LoggingMode}, Seq {SeqEnabled}, OTLP {OtlpEnabled}",
        BuildInfo.Version,
        BuildInfo.CommitSha,
        options.Port,
        options.LoggingMode,
        options.SeqEnabled,
        options.OtlpEnabled);

    if (options.DatabaseConnectionString is null)
    {
        Log.Warning("DATABASE_URL is not set. /ready will report not_ready until it is configured.");
    }

    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Charter terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static bool IsProbe(PathString path)
    => path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
       || path.StartsWithSegments("/ready", StringComparison.OrdinalIgnoreCase);

internal sealed record HealthResponse(string Status, string Version, string Commit);

internal sealed record ReadinessResponse(string Status, string? Reason);

internal sealed record InstanceResponse(
    string Version,
    string Commit,
    string BuildDate,
    string SourceUrl,
    string License,
    string ServiceName);
