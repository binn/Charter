using Charter.Configuration;
using Charter.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Charter.Tests;

/// <summary>
/// One environment Charter could plausibly be deployed with, and the two host artifacts built from
/// it.
/// </summary>
/// <remarks>
/// <para>
/// Every case goes through <see cref="CharterConfigParser"/> and <see cref="CharterHost"/> — the same
/// parser and the same composition the process uses. Nothing here hand-assembles a service
/// collection, because a hand-assembled graph is the reason three defects reached a deployment with
/// 1850 tests passing behind them.
/// </para>
/// <para>
/// The variable source is a dictionary rather than the process environment. Two registrations read
/// section 18 and section 12b configuration directly, and mutating <c>Environment</c> for those would
/// make this suite unsafe to run beside any other test class.
/// </para>
/// </remarks>
internal sealed class BootScenario
{
    private BootScenario(string name, (string Key, string? Value)[] overrides)
    {
        Name = name;
        Values = ConfigTestEnvironment.Required();

        foreach (var (key, value) in overrides)
        {
            if (value is null)
            {
                Values.Remove(key);
            }
            else
            {
                Values[key] = value;
            }
        }
    }

    public string Name { get; }

    private Dictionary<string, string> Values { get; }

    /// <summary>The variable source, exactly as the host reads the process environment.</summary>
    public Func<string, string?> Environment => name => Values.GetValueOrDefault(name);

    /// <summary>The parsed configuration. Fails the test if the scenario is not a valid one.</summary>
    public CharterConfig Config()
    {
        var parsed = CharterConfigParser.Parse(Environment);

        Assert.True(
            parsed.IsValid,
            $"the '{Name}' scenario is not a configuration Charter would accept: "
            + string.Join("; ", parsed.Errors.Select(problem => problem.Text)));

        return parsed.Config!;
    }

    // ---------------------------------------------------------------------------------------------
    // The matrix. Each axis is varied against the default, and the last few cases cross the axes,
    // because the defect that took down every default install only appeared in one combination.
    // ---------------------------------------------------------------------------------------------

    private const string SmtpUrl = "smtp://mailer:p%40ss@smtp.example.com:2525";

    private static readonly (string Key, string? Value)[] Railway =
    [
        ("CHARTER_DEPLOYMENT_PROVIDER", "railway"),
        ("CHARTER_RAILWAY_TOKEN", "railway-token"),
        ("CHARTER_RAILWAY_PROJECT_ID", "proj_123"),
        ("CHARTER_RAILWAY_BASE_ENVIRONMENT", "staging"),
    ];

    private static readonly (string Key, string? Value)[] NoOptionalCredentials =
    [
        ("ANTHROPIC_API_KEY", null),
        ("OPENROUTER_API_KEY", null),
    ];

    private static readonly (string Key, string? Value)[] EveryOptionalCredential =
    [
        ("ANTHROPIC_API_KEY", "sk-ant-instance-key"),
        ("OPENROUTER_API_KEY", "sk-or-instance-key"),
        ("CHARTER_OAUTH_GITHUB_ID", "gh-id"),
        ("CHARTER_OAUTH_GITHUB_SECRET", "gh-secret"),
        ("CHARTER_OAUTH_GOOGLE_ID", "google-id"),
        ("CHARTER_OAUTH_GOOGLE_SECRET", "google-secret"),
        ("CHARTER_OAUTH_DISCORD_ID", "discord-id"),
        ("CHARTER_OAUTH_DISCORD_SECRET", "discord-secret"),
        ("CHARTER_OAUTH_SLACK_ID", "slack-id"),
        ("CHARTER_OAUTH_SLACK_SECRET", "slack-secret"),
        ("CHARTER_SEQ_URL", "http://seq:5341"),
        ("CHARTER_SEQ_API_KEY", "seq-key"),
        ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4317"),
        ("CHARTER_STORAGE_ENDPOINT", "https://minio.internal:9000"),
        ("CHARTER_STORAGE_BUCKET", "charter-artifacts"),
        ("CHARTER_STORAGE_ACCESS_KEY", "storage-access"),
        ("CHARTER_STORAGE_SECRET_KEY", "storage-secret"),
    ];

    private static readonly Dictionary<string, (string Key, string? Value)[]> Definitions =
        new(StringComparer.Ordinal)
        {
            // The default configuration. CHARTER_RUNNER unset means github-actions, which is the
            // combination the agent-plane defect took down and the one nothing was exercising.
            ["default"] = [],

            ["runner-github-actions-explicit"] = [("CHARTER_RUNNER", "github-actions")],
            ["runner-agent"] = [("CHARTER_RUNNER", "agent")],
            ["runner-docker"] = [("CHARTER_RUNNER", "docker")],
            ["runner-agent-and-github-actions"] = [("CHARTER_RUNNER", "agent,github-actions")],
            ["runner-docker-and-github-actions"] = [("CHARTER_RUNNER", "docker,github-actions")],
            ["runner-all-three"] = [("CHARTER_RUNNER", "agent,github-actions,docker")],

            ["deployment-none-explicit"] = [("CHARTER_DEPLOYMENT_PROVIDER", "none")],
            ["deployment-railway"] = Railway,

            ["email-none-explicit"] = [("CHARTER_EMAIL_PROVIDER", "none")],
            ["email-smtp"] = [("CHARTER_EMAIL_PROVIDER", "smtp"), ("CHARTER_SMTP_URL", SmtpUrl)],

            ["mode-personal"] = [("CHARTER_MODE", "personal")],
            ["mode-organization"] = [("CHARTER_MODE", "organization")],

            ["without-optional-credentials"] = NoOptionalCredentials,
            ["with-every-optional-credential"] = EveryOptionalCredential,

            // The crossings. A per-axis sweep would have missed the agent-plane defect: it needed the
            // default runner and a mapped route at the same time.
            ["docker-railway-smtp-organization"] =
            [
                ("CHARTER_RUNNER", "docker"),
                ("CHARTER_MODE", "organization"),
                ("CHARTER_EMAIL_PROVIDER", "smtp"),
                ("CHARTER_SMTP_URL", SmtpUrl),
                .. Railway,
            ],

            ["agent-railway-organization-without-credentials"] =
            [
                ("CHARTER_RUNNER", "agent"),
                ("CHARTER_MODE", "organization"),
                .. Railway,
                .. NoOptionalCredentials,
            ],

            ["github-actions-railway-personal-everything"] =
            [
                ("CHARTER_RUNNER", "github-actions"),
                ("CHARTER_MODE", "personal"),
                ("CHARTER_EMAIL_PROVIDER", "smtp"),
                ("CHARTER_SMTP_URL", SmtpUrl),
                .. Railway,
                .. EveryOptionalCredential,
            ],
        };

    public static IEnumerable<string> Names => Definitions.Keys;

    public static BootScenario Named(string name) => new(name, Definitions[name]);
}

/// <summary>
/// That the service graph the host composes validates and resolves, and that every route it maps can
/// be constructed, in every configuration Charter documents.
/// </summary>
/// <remarks>
/// <para>
/// Two of the three defects this suite exists for were configuration-shaped. The preview subsystem
/// was never registered and appeared to work because an endpoint resolved a type another module had
/// registered — <c>ValidateOnBuild</c> plus resolving the real graph is what makes that visible. The
/// agent plane registered its services only under <c>CHARTER_RUNNER=agent</c> while mapping its routes
/// unconditionally, so the <em>default</em> configuration died at pipeline construction with an error
/// naming a parameter rather than a cause — building endpoints, not just the container, is what makes
/// that visible.
/// </para>
/// <para>
/// Neither test touches a database. Composition and route construction are pure; the boot that talks
/// to Postgres is <c>BootEndToEndTests</c>.
/// </para>
/// </remarks>
public class BootMatrixTests
{
    public static TheoryData<string> Scenarios => [.. BootScenario.Names];

    /// <summary>
    /// Builds the host the way the process does, with the container's own validation turned all the
    /// way up.
    /// </summary>
    private static WebApplication BuildHost(BootScenario scenario)
    {
        var config = scenario.Config();
        var builder = CharterHost.CreateBuilder([], config, config.ToStartupOptions(), scenario.Environment);

        // Section 4.1's rule applied to the container: every dependency of every registration is
        // checked once, at build, rather than on the first request that happens to need it. A scoped
        // service captured by a singleton is the other half of the same class of defect.
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });

        return builder.Build();
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task TheServiceGraphValidatesAndEveryHostedServiceResolves(string name)
    {
        var scenario = BootScenario.Named(name);

        // Build() throws AggregateException listing every unsatisfiable registration when
        // ValidateOnBuild is on. That is the assertion: an exception here is a container the process
        // could not have started with.
        await using var app = BuildHost(scenario);

        await using var scope = app.Services.CreateAsyncScope();

        // ValidateOnBuild cannot see through a factory lambda, and most of Charter's registrations are
        // factory lambdas. Resolving the hosted services runs them: it is the same construction the
        // host performs at StartAsync, and it reaches the singletons underneath.
        var hosted = app.Services.GetServices<IHostedService>().ToArray();
        Assert.NotEmpty(hosted);
        Assert.All(hosted, service => Assert.NotNull(service));

        // The types the endpoints inject. A request that reaches any of these after a deploy is too
        // late to find out.
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<StartupOptions>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<CharterConfig>());
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task EveryMappedEndpointCanBeConstructedAndThePipelineBuilds(string name)
    {
        var scenario = BootScenario.Named(name);

        await using var app = BuildHost(scenario);

        CharterHost.ConfigurePipeline(app);

        // Minimal-API handlers are compiled lazily, when the data source is first enumerated. That is
        // where "Failure to infer one or more parameters" is thrown, and it is the exact moment the
        // agent-plane defect took the default install down. Enumerating every data source compiles
        // every handler.
        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .ToArray();

        Assert.NotEmpty(endpoints);

        // And the middleware chain itself, which is the other thing built before the first request.
        var pipeline = ((IApplicationBuilder)app).Build();
        Assert.NotNull(pipeline);

        // Routes the SPA and every platform probe depend on, in every configuration.
        var routes = endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => "/" + endpoint.RoutePattern.RawText!.TrimStart('/'))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("/health", routes);
        Assert.Contains("/ready", routes);
        Assert.Contains("/api/instance", routes);
        Assert.Contains("/hub/requests/negotiate", routes);
    }

    [Fact]
    public async Task TheAgentPlaneRoutesFollowTheRunnerTheInstanceIsConfiguredWith()
    {
        // The precise shape of the defect: services registered conditionally, routes mapped
        // unconditionally. Asserting both directions keeps the fix from being "map them always" or
        // "register them always", either of which would put the two back out of step.
        var withAgent = await RoutesFor(BootScenario.Named("runner-agent"));
        var withoutAgent = await RoutesFor(BootScenario.Named("default"));

        Assert.Contains(Charter.Agent.Protocol.AgentProtocol.PairPath, withAgent);
        Assert.Contains(Charter.Agent.Protocol.AgentProtocol.ConnectPath, withAgent);
        Assert.Contains("/api/agent/agents", withAgent);

        Assert.DoesNotContain(Charter.Agent.Protocol.AgentProtocol.PairPath, withoutAgent);
        Assert.DoesNotContain(Charter.Agent.Protocol.AgentProtocol.ConnectPath, withoutAgent);
        Assert.DoesNotContain("/api/agent/agents", withoutAgent);

        // The rest of the API is unaffected either way - the absence is scoped to the plane.
        Assert.Contains("/health", withoutAgent);
    }

    [Fact]
    public async Task ThePreviewSubsystemIsInTheGraphTheHostComposesRatherThanOnlyInTests()
    {
        // AddCharterDeployments() was missing from the host and the suite never noticed, because
        // every deployment test registered it itself. Resolving it out of the *host's* graph is the
        // difference.
        await using var app = BuildHost(BootScenario.Named("default"));
        await using var scope = app.Services.CreateAsyncScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<Charter.Deployments.DeploymentIngestor>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<Charter.Deployments.PreviewArtifactPublisher>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<Charter.Deployments.PreviewExpiry>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<Charter.Deployments.DeploymentProviderRegistry>());

        // And with railway configured, the provider the operator asked for is the one in the registry.
        await using var railway = BuildHost(BootScenario.Named("deployment-railway"));
        await using var railwayScope = railway.Services.CreateAsyncScope();

        var registry = railwayScope.ServiceProvider
            .GetRequiredService<Charter.Deployments.DeploymentProviderRegistry>();

        Assert.NotNull(registry.Configured);
        Assert.Equal("railway", registry.Configured!.Id);
    }

    private static async Task<HashSet<string>> RoutesFor(BootScenario scenario)
    {
        await using var app = BuildHost(scenario);

        CharterHost.ConfigurePipeline(app);

        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => "/" + endpoint.RoutePattern.RawText!.TrimStart('/'))
            .ToHashSet(StringComparer.Ordinal);
    }
}
