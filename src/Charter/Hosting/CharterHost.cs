using Charter.Adapters;
using Charter.Api;
using Charter.Auth;
using Charter.Configuration;
using Charter.Configuration.Preflight;
using Charter.Data;
using Charter.Deployments;
using Charter.Diagnostics;
using Charter.GitHub;
using Charter.Models;
using Charter.Onboarding;
using Charter.Recaps;
using Charter.Refinement;
using Charter.Runners;
using Charter.Runners.Agent;
using Charter.Teaching;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;

namespace Charter.Hosting;

/// <summary>
/// Everything the running application is made of: the service graph, the migration step, and the
/// middleware pipeline, in the order the host applies them.
/// </summary>
/// <remarks>
/// <para>
/// <c>Program.cs</c> is deliberately a shell — parse, build, migrate, configure, run — and every
/// decision it used to make inline lives here instead. The reason is a class of defect that 1850
/// passing tests could not see: a suite that assembles its own <see cref="IServiceCollection"/> is
/// testing a graph nobody deploys. A missing <c>AddCharter*</c> call, or an endpoint whose parameter
/// no registration provides, is invisible to such a test and fatal to the process. With composition
/// behind these methods, <c>Boot*Tests</c> and <c>Host*Tests</c> compose <em>exactly</em> what
/// production composes.
/// </para>
/// <para>
/// The one deliberate difference from the code this replaces: the two registrations that used to
/// read the process environment themselves — <c>AddCharterDeployments()</c> and
/// <c>AddCharterAdapters()</c> — take an explicit variable source. It defaults to
/// <see cref="Environment.GetEnvironmentVariable(string)"/>, which is precisely what their no-arg
/// overloads did, so the host is unchanged; but a test can now sweep the section 18 provider matrix
/// without mutating process-global state that another test class is reading concurrently.
/// </para>
/// </remarks>
public static class CharterHost
{
    /// <summary>
    /// Builds the host builder: configuration sources, the listening port, and the whole service
    /// graph.
    /// </summary>
    /// <param name="args">The process arguments, for the command line configuration source.</param>
    /// <param name="config">The validated section 4.2 configuration.</param>
    /// <param name="options">The boot subset projected from <paramref name="config"/>.</param>
    /// <param name="environment">
    /// Variable source for the subsystems that read section 18 and section 12b configuration
    /// directly. Defaults to the process environment.
    /// </param>
    public static WebApplicationBuilder CreateBuilder(
        string[] args,
        CharterConfig config,
        StartupOptions options,
        Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(args);

        // Section 4.1 applies to the service graph as much as to configuration: validate everything
        // at startup and refuse to run rather than failing lazily on first use. These default to on
        // only in Development, which is precisely backwards for the failures they catch - a missing
        // registration or a captive dependency reaches production, not a developer's machine. Both
        // have already shipped here once: a subsystem the host never registered, and a scoped
        // service resolved from the root provider, which under lazy validation silently pins a
        // request-scoped DbContext to the lifetime of the process.
        //
        // The cost is a one-off sweep of the graph at boot. A container that dies immediately with a
        // named cause is a better outcome than one that serves traffic and fails on a code path
        // nobody exercises until a requester does.
        builder.Host.UseDefaultServiceProvider(provider =>
        {
            provider.ValidateOnBuild = true;
            provider.ValidateScopes = true;
        });

        // Section 4.1: no appsettings.json, and no Section__Nested__Key convention. Clearing the
        // default providers makes that structural rather than a rule someone has to remember.
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddEnvironmentVariables();
        builder.Configuration.AddCommandLine(args);

        // Section 2.3: one HTTP port.
        builder.WebHost.UseUrls($"http://0.0.0.0:{options.Port}");

        ConfigureServices(builder.Services, config, options, environment);

        return builder;
    }

    /// <summary>
    /// Registers every service the application runs on, in the order the host registers them.
    /// </summary>
    /// <remarks>
    /// Order matters and is load-bearing: credentials need both the key config and the DbContext,
    /// and the GitHub seams must be registered before <c>AddCharterOrchestration</c>'s
    /// "unconfigured" <c>TryAdd</c> fallbacks so the real implementations win.
    /// </remarks>
    public static IServiceCollection ConfigureServices(
        IServiceCollection services,
        CharterConfig config,
        StartupOptions options,
        Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(options);

        var read = environment ?? Environment.GetEnvironmentVariable;

        // Section 31: graceful shutdown - drain in-flight work before the process goes away.
        services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(30));

        services.AddSerilog();
        services.AddSingleton(options);
        services.AddProblemDetails();

        // Each subsystem owns its own registrations, so this stays a list of decisions rather than a
        // wiring dump. Order matters: credentials need both the key config and the DbContext.
        services.AddCharterConfig(config);
        services.AddCharterPreflight();
        services.AddCharterData(config.Database.ConnectionString.Reveal());

        // Section 30.6, and it has to be here rather than lower down. Its email registrations must
        // beat the TryAdds inside AddCharterNotifications (reached from AddCharterApi), and its
        // seeder has to be registered before AddCharterAuth's so that setup mode sees a populated
        // database rather than printing a setup token nobody will use. On an instance without
        // CHARTER_DEMO this call registers nothing at all.
        services.AddCharterDemoMode(config);

        services.AddCharterAuth();
        services.AddCharterGitHub();
        services.AddCharterDeployments(DeploymentOptions.Parse(read));
        services.AddCharterOnboarding();
        services.AddCharterApi();

        // Before AddCharterModels and the three control-plane subsystems below it: every one of them
        // registers its own options record with TryAdd, so the projection of CHARTER_MODEL_REFINE and
        // CHARTER_MODEL_TEACH has to be registered first to win. Without this call those two
        // variables parse, validate, and reach nothing (sections 4.2, 20b).
        services.AddCharterModelSelection(config);

        services.AddCharterModels();

        // After AddCharterData and AddCharterModels: the estimator reads the ledger for what similar
        // work in this repository actually cost, and IModelPriceCatalog for what the selected model
        // costs (sections 34.4, 20b.6).
        services.AddCharterBudgets();
        services.AddCharterRefinement();
        services.AddCharterRecap();
        services.AddCharterTeaching();
        services.AddCharterCredentials();
        services.AddCharterAdapters(AdapterSources.FromEnvironment(read));
        services.AddCharterRunners(config);
        services.AddCharterOrchestration();

        ConfigureTelemetry(services, options);

        return services;
    }

    /// <summary>Traces and metrics (section 19.2), exported only when a collector is configured.</summary>
    private static void ConfigureTelemetry(IServiceCollection services, StartupOptions options)
    {
        services
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
    }

    /// <summary>Applies pending EF Core migrations (section 2.3).</summary>
    /// <remarks>
    /// <para>
    /// A PaaS offers no pre-deploy hook, so boot is the only place these can run, and serving against
    /// a stale schema is worse than failing to start.
    /// </para>
    /// <para>
    /// This runs before the host starts, and therefore before the section 30.1 preflight checks. That
    /// ordering is deliberate - "migrations applied" is a question about work this step does - but it
    /// means an unreachable database surfaces here first, as a raw Npgsql stack trace, rather than as
    /// the named check with remediation that section 30.1 promises. The catch below restores the
    /// promise: same failure, same exit code, a sentence an operator can act on.
    /// </para>
    /// </remarks>
    public static async Task MigrateAsync(WebApplication app, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        await using var scope = app.Services.CreateAsyncScope();

        var database = scope.ServiceProvider.GetRequiredService<CharterDbContext>();
        var config = scope.ServiceProvider.GetRequiredService<CharterConfig>();

        try
        {
            var pending = (await database.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();

            if (pending.Length > 0)
            {
                Log.Information("Applying {Count} pending migration(s): {Migrations}", pending.Length, pending);
                await database.Database.MigrateAsync(cancellationToken);
                Log.Information("Migrations applied");
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new PreflightException(
                "Charter cannot start. Preflight found the following blocking problems (section 30.1):"
                + Environment.NewLine
                + $"  [FAIL] database: cannot reach {config.Database.Host}:{config.Database.Port} to apply "
                + $"migrations: {ex.Message} -> check DATABASE_URL, that Postgres is running, that this "
                + "container can reach it, and that the database role may create tables",
                ex);
        }
    }

    /// <summary>
    /// Installs the middleware pipeline and maps every route, in the order the host applies them.
    /// </summary>
    /// <remarks>
    /// The sequence is the contract. <c>UseWebSockets</c> must precede the agent plane or the upgrade
    /// never becomes a socket; the setup gate must precede routing or an unclaimed instance runs an
    /// endpoint's filters before refusing it; and the SPA fallback must come last or it swallows the
    /// API.
    /// </remarks>
    public static WebApplication ConfigurePipeline(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseSerilogRequestLogging(logging =>
        {
            // Probes fire every few seconds on every PaaS. Logging them at Information drowns
            // everything.
            logging.GetLevel = (context, _, exception) => exception is not null
                ? LogEventLevel.Error
                : IsProbe(context.Request.Path)
                    ? LogEventLevel.Verbose
                    : LogEventLevel.Information;
        });

        // Section 30.1: an unclaimed instance serves only the setup route, so a self-hosted Charter
        // cannot be claimed by whoever finds it first. This must run before anything else routes.
        // Section 33.1: the agent dials out over a WebSocket. Without this the upgrade never
        // becomes a socket and /api/agent/connect answers 400.
        app.UseWebSockets();

        app.UseCharterSetupMode();
        app.UseAuthentication();
        app.UseAuthorization();

        // Section 3.1: the SPA is served from the same origin as the API. In Development,
        // Microsoft.AspNetCore.SpaProxy starts Vite and proxies unmatched requests to it, so HMR
        // works from `dotnet run` alone. In production these are the files Vite emitted into wwwroot.
        app.UseDefaultFiles();
        app.UseStaticFiles();

        MapProbes(app);

        app.MapCharterApi();
        app.MapCharterGitHub();
        app.MapCharterRunnerCallbacks();
        app.MapCharterAgentPlane();
        app.MapCharterHubs();

        // Client-side routing: anything not matched above is the SPA's problem.
        app.MapFallbackToFile("index.html");

        return app;
    }

    /// <summary>
    /// The three routes the host owns itself: liveness, readiness and the AGPL instance descriptor.
    /// </summary>
    /// <remarks>
    /// Section 31: liveness never touches a dependency — a database blip must not make the platform
    /// kill an otherwise healthy container.
    /// </remarks>
    private static void MapProbes(WebApplication app)
    {
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
    }

    /// <summary>The line an operator reads to know what is running and where it reports to.</summary>
    public static void LogStartupSummary(StartupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

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
    }

    /// <summary>True for the platform probe paths, which are logged and traced differently.</summary>
    internal static bool IsProbe(PathString path)
        => path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
           || path.StartsWithSegments("/ready", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Body of <c>GET /health</c>.</summary>
internal sealed record HealthResponse(string Status, string Version, string Commit);

/// <summary>Body of <c>GET /ready</c>.</summary>
internal sealed record ReadinessResponse(string Status, string? Reason);

/// <summary>Body of <c>GET /api/instance</c> — the AGPL section 13 descriptor (section 24).</summary>
internal sealed record InstanceResponse(
    string Version,
    string Commit,
    string BuildDate,
    string SourceUrl,
    string License,
    string ServiceName);
