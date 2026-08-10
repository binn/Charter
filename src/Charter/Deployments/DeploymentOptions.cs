using Charter.Configuration;

namespace Charter.Deployments;

/// <summary>Which deployment provider an instance runs, if any.</summary>
public enum DeploymentProviderKind
{
    /// <summary>
    /// No provider. Previews still work: everything arrives through the generic webhook of section 18,
    /// which is the configuration a Render, Fly or Coolify self-hoster runs.
    /// </summary>
    None,

    /// <summary>Railway PR Environments. The only Phase 1 implementation.</summary>
    Railway,
}

/// <summary>
/// How Charter binds previews (section 18) and when they expire (section 27.7).
/// </summary>
public sealed record DeploymentOptions
{
    /// <summary>The Phase 1 default lifetime Charter imposes when a platform states none.</summary>
    public const int DefaultPreviewTtlHours = 72;

    /// <summary><c>CHARTER_DEPLOYMENT_PROVIDER</c>, default <c>none</c>.</summary>
    public required DeploymentProviderKind Provider { get; init; }

    /// <summary>
    /// <c>CHARTER_PREVIEW_TTL_HOURS</c>, default 72. Zero means Charter imposes no expiry.
    /// </summary>
    /// <remarks>
    /// Section 27.7 calls expired previews the number one source of confusion in tools like this:
    /// somebody opens a link from a three-day-old notification, gets a dead host, and reports the
    /// product as broken. The countdown has to be visible from first render, which means an expiry has
    /// to exist the moment a preview becomes ready — so where a platform states no lifetime, the
    /// operator states one here and Charter renders <c>expiring</c> and <c>expired</c> from it.
    /// </remarks>
    public required TimeSpan PreviewTtl { get; init; }

    /// <summary>Railway's settings, present only when <see cref="Provider"/> is Railway.</summary>
    public RailwayOptions? Railway { get; init; }

    /// <summary>Everything wrong or noteworthy about the deployment configuration.</summary>
    /// <remarks>
    /// Carried rather than thrown away so the host can log the warnings once the logging pipeline
    /// exists. <see cref="DeploymentStartupWarnings"/> is what makes the production-base warning loud
    /// rather than a doc footnote.
    /// </remarks>
    public IReadOnlyList<ConfigProblem> Problems { get; init; } = [];

    /// <summary>How often the lifecycle service reconciles deployments and sweeps expiries.</summary>
    /// <remarks>
    /// No environment variable, deliberately: section 4.2 is the complete list of what an operator
    /// configures, and a poll interval is a tuning knob nobody should have to think about.
    /// </remarks>
    public TimeSpan ReconcileInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How often the configured provider is asked directly about a change request with no ready
    /// preview.
    /// </summary>
    /// <remarks>
    /// Slower than the reconcile interval on purpose. A webhook and a bot comment are things that
    /// might arrive; polling is the thing that always can — but a preview that is not ready is not
    /// made ready by asking more often, and every ask is a request against somebody's API quota.
    /// </remarks>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How long a reachability probe waits before calling a preview unreachable.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Whether Charter probes preview URLs at all. The card's reachability dot needs it.</summary>
    public bool ProbeReachability { get; init; } = true;

    /// <summary>Problems that must stop startup.</summary>
    public IReadOnlyList<ConfigProblem> Errors
        => [.. Problems.Where(problem => problem.Severity == ConfigSeverity.Error)];

    /// <summary>Problems worth shouting about that do not stop startup.</summary>
    public IReadOnlyList<ConfigProblem> Warnings
        => [.. Problems.Where(problem => problem.Severity == ConfigSeverity.Warning)];

    /// <summary>The expiry to stamp on a preview that becomes ready now, or null for none.</summary>
    /// <param name="now">When the preview became ready.</param>
    /// <param name="providerLifetime">What the provider said, which always wins.</param>
    public DateTimeOffset? ExpiryFor(DateTimeOffset now, TimeSpan? providerLifetime = null)
    {
        var lifetime = providerLifetime ?? PreviewTtl;
        return lifetime <= TimeSpan.Zero ? null : now + lifetime;
    }

    /// <summary>Reads section 18's configuration from the process environment.</summary>
    public static DeploymentOptions FromEnvironment()
        => Parse(name => Environment.GetEnvironmentVariable(name));

    /// <summary>Reads section 18's configuration from an arbitrary variable source.</summary>
    /// <param name="read">Returns the raw value of a variable, or <c>null</c> when it is unset.</param>
    public static DeploymentOptions Parse(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        var reader = new EnvReader(read);

        var provider = reader.Choice<DeploymentProviderKind>(
            "CHARTER_DEPLOYMENT_PROVIDER",
            DeploymentProviderKind.None,
            [
                ("none", DeploymentProviderKind.None),
                ("railway", DeploymentProviderKind.Railway),
            ]);

        var ttlHours = reader.Int(
            "CHARTER_PREVIEW_TTL_HOURS",
            DefaultPreviewTtlHours,
            0,
            24 * 365,
            "a whole number of hours between 0 and 8760, where 0 means previews never expire");

        var railway = RailwayOptions.Parse(reader, provider == DeploymentProviderKind.Railway);

        return new DeploymentOptions
        {
            Provider = provider,
            PreviewTtl = TimeSpan.FromHours(ttlHours),
            Railway = railway,
            Problems = reader.Problems,
        };
    }

    /// <summary>The configuration for an instance that binds previews through the webhook alone.</summary>
    public static DeploymentOptions WebhookOnly { get; } = new()
    {
        Provider = DeploymentProviderKind.None,
        PreviewTtl = TimeSpan.FromHours(DefaultPreviewTtlHours),
    };
}

/// <summary>
/// Railway PR Environments (section 18).
/// </summary>
/// <remarks>
/// A PR environment replicates every service, database and variable from the base environment into an
/// isolated ephemeral environment with fresh URLs. That is what makes it a good preview and what makes
/// the base environment a security decision rather than a convenience: replicate production and every
/// preview holds production credentials.
/// </remarks>
public sealed record RailwayOptions
{
    /// <summary>Railway's public GraphQL endpoint.</summary>
    public const string DefaultApiUrl = "https://backboard.railway.com/graphql/v2";

    /// <summary>Environment names that mean "this is the real thing".</summary>
    /// <remarks>
    /// Matched exactly after lower-casing, plus any name containing <c>prod</c>. Deliberately broad:
    /// a false positive costs an operator one log line, and a false negative costs them their
    /// production database credentials in an environment anybody who can open a change request can
    /// reach.
    /// </remarks>
    public static IReadOnlyList<string> ProductionNames { get; } =
        ["production", "prod", "prd", "live", "main", "master", "default"];

    /// <summary><c>CHARTER_RAILWAY_TOKEN</c>. A Railway account or team token.</summary>
    public required Secret Token { get; init; }

    /// <summary><c>CHARTER_RAILWAY_PROJECT_ID</c>. The project previews are created in.</summary>
    public required string ProjectId { get; init; }

    /// <summary>
    /// <c>CHARTER_RAILWAY_BASE_ENVIRONMENT</c>. The environment PR environments are replicated from.
    /// </summary>
    /// <remarks>
    /// Required rather than defaulted, and warned about loudly when it looks like production. Section
    /// 18: base previews off a staging environment, not production, so preview secrets are never real
    /// ones. Defaulting this would default it to the environment every Railway project starts with,
    /// which is called <c>production</c>.
    /// </remarks>
    public required string BaseEnvironment { get; init; }

    /// <summary><c>CHARTER_RAILWAY_API_URL</c>, default Railway's public GraphQL endpoint.</summary>
    public required Uri ApiUrl { get; init; }

    /// <summary>True when <see cref="BaseEnvironment"/> reads like a production environment.</summary>
    public bool BaseLooksLikeProduction => LooksLikeProduction(BaseEnvironment);

    /// <summary>
    /// How long a change request may go without an environment before Charter says something.
    /// </summary>
    /// <remarks>
    /// Railway will not deploy a change request branch from an account outside the workspace unless it
    /// has been invited (section 18). There is no error anywhere when that happens — the environment
    /// is simply never created — so the only detectable signal is absence that has gone on too long.
    /// A tuning knob, not a setting: no environment variable.
    /// </remarks>
    public TimeSpan MissingEnvironmentGrace { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Whether a name reads as production rather than staging.</summary>
    public static bool LooksLikeProduction(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var normalised = name.Trim().ToLowerInvariant();

        return normalised.Contains("prod", StringComparison.Ordinal)
               || ProductionNames.Contains(normalised, StringComparer.Ordinal);
    }

    /// <summary>The warning text for a base environment that looks like production.</summary>
    public static string ProductionBaseWarning(string name)
        => $"CHARTER_RAILWAY_BASE_ENVIRONMENT is '{name}', which looks like a production environment. " +
           "A Railway PR environment replicates every service, database and variable from its base, so " +
           "every preview would carry real credentials and could write to real data — and anybody who " +
           "can open a change request can reach one. Point this at a staging environment (section 18).";

    internal static RailwayOptions? Parse(EnvReader reader, bool required)
    {
        var token = required
            ? reader.RequiredSecret("CHARTER_RAILWAY_TOKEN", "a Railway account or team API token")
            : reader.OptionalSecret("CHARTER_RAILWAY_TOKEN");

        var projectId = required
            ? reader.Required("CHARTER_RAILWAY_PROJECT_ID", "the Railway project previews are created in")
            : reader.Optional("CHARTER_RAILWAY_PROJECT_ID");

        var baseEnvironment = required
            ? reader.Required(
                "CHARTER_RAILWAY_BASE_ENVIRONMENT",
                "the Railway environment PR environments are replicated from. Section 18: base " +
                "previews off a staging environment, not production, so preview secrets are never " +
                "real ones")
            : reader.Optional("CHARTER_RAILWAY_BASE_ENVIRONMENT");

        var apiUrl = reader.Url(
            "CHARTER_RAILWAY_API_URL",
            $"an absolute http(s) URL for Railway's GraphQL API, for example {DefaultApiUrl}",
            required: false) ?? new Uri(DefaultApiUrl);

        if (baseEnvironment is not null && LooksLikeProduction(baseEnvironment))
        {
            reader.Warn("CHARTER_RAILWAY_BASE_ENVIRONMENT", ProductionBaseWarning(baseEnvironment));
        }

        if (!required)
        {
            // Set but unused reads as configured to an operator staring at a dashboard with no
            // previews on it, so say which variable turns it on.
            foreach (var (variable, isSet) in new[]
                     {
                         ("CHARTER_RAILWAY_TOKEN", token is not null),
                         ("CHARTER_RAILWAY_PROJECT_ID", projectId is not null),
                         ("CHARTER_RAILWAY_BASE_ENVIRONMENT", baseEnvironment is not null),
                     })
            {
                if (isSet)
                {
                    reader.Warn(
                        variable,
                        $"{variable} is set but CHARTER_DEPLOYMENT_PROVIDER is not 'railway', so " +
                        "Charter will not poll Railway, read its comments or tear its environments " +
                        "down. Previews still arrive through POST /api/deployments/{prSha}.");
                }
            }

            return null;
        }

        if (token is null || projectId is null || baseEnvironment is null)
        {
            return null;
        }

        return new RailwayOptions
        {
            Token = token,
            ProjectId = projectId,
            BaseEnvironment = baseEnvironment,
            ApiUrl = apiUrl,
        };
    }
}
