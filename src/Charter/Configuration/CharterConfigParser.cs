namespace Charter.Configuration;

/// <summary>
/// The outcome of parsing section 4.2: a config when the environment is usable, and every problem
/// found either way.
/// </summary>
/// <param name="Config">The parsed config, or <c>null</c> when a blocking problem was found.</param>
/// <param name="Problems">Every problem, blocking or not, in the order the variables were read.</param>
public sealed record ConfigParseResult(CharterConfig? Config, IReadOnlyList<ConfigProblem> Problems)
{
    /// <summary>Problems that stop Charter starting.</summary>
    public IReadOnlyList<ConfigProblem> Errors
        => [.. Problems.Where(problem => problem.Severity == ConfigSeverity.Error)];

    /// <summary>Problems worth printing that do not stop Charter starting.</summary>
    public IReadOnlyList<ConfigProblem> Warnings
        => [.. Problems.Where(problem => problem.Severity == ConfigSeverity.Warning)];

    /// <summary>True when Charter can start.</summary>
    public bool IsValid => Config is not null;

    /// <summary>Returns the config, or throws with every blocking problem attached.</summary>
    /// <exception cref="ConfigException">The environment is not usable.</exception>
    public CharterConfig ConfigOrThrow()
        => Config ?? throw new ConfigException([.. Errors.Select(problem => problem.Text)]);

    /// <summary>
    /// The problem list as it should reach stdout: every error, then every warning.
    /// </summary>
    /// <remarks>
    /// This runs before the logging pipeline exists, so it produces plain text for the host to write
    /// to stderr on its way to exiting non-zero (section 4.1).
    /// </remarks>
    public string Describe()
    {
        var lines = new List<string>();

        if (Errors.Count > 0)
        {
            lines.Add("Charter cannot start. Fix the following configuration problems:");
            lines.AddRange(Errors.Select(problem => $"  - {problem.Text}"));
        }

        if (Warnings.Count > 0)
        {
            lines.Add("Configuration warnings:");
            lines.AddRange(Warnings.Select(problem => $"  - {problem.Text}"));
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// The hand-written parser section 4.1 calls for: reads flat environment variables, validates
/// everything, and reports every problem at once instead of failing lazily on first use.
/// </summary>
public static class CharterConfigParser
{
    /// <summary>Parses section 4.2 from the process environment.</summary>
    public static ConfigParseResult Parse()
        => Parse(name => Environment.GetEnvironmentVariable(name));

    /// <summary>Parses section 4.2 from an arbitrary variable source.</summary>
    /// <param name="read">Returns the raw value of a variable, or <c>null</c> when it is unset.</param>
    public static ConfigParseResult Parse(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        var reader = new EnvReader(read);

        // Read in the order section 4.2 lists the variables, so the printed problem list matches the
        // table an operator is reading while fixing it.
        var port = reader.Int("PORT", 8080, 1, 65535, "a TCP port between 1 and 65535");

        var baseUrl = reader.Url(
            "CHARTER_BASE_URL",
            "an absolute http(s) URL for this instance, used for webhooks and links, " +
            "for example https://charter.example.com",
            required: true);

        var mode = reader.Choice<CharterMode>("CHARTER_MODE", CharterMode.Personal,
        [
            ("personal", CharterMode.Personal),
            ("organization", CharterMode.Organization),
        ]);

        var runners = ParseRunners(reader);
        var database = DatabaseConfig.Parse(reader, required: true);
        var keys = KeyConfig.Parse(reader);
        var models = ModelConfig.Parse(reader);
        var github = GitHubConfig.Parse(reader);
        var logging = LoggingConfig.Parse(reader);
        var telemetry = TelemetryConfig.Parse(reader);
        var auth = AuthConfig.Parse(reader, mode);
        var smtp = SmtpConfig.Parse(reader);
        var budgets = BudgetConfig.Parse(reader);
        var storage = StorageConfig.Parse(reader);
        var updateCheck = UpdateCheckConfig.Parse(reader);
        var allowRepoCreation = reader.Bool("CHARTER_ALLOW_REPO_CREATION", false);
        var demoMode = reader.Bool("CHARTER_DEMO", false);

        if (reader.HasErrors || baseUrl is null || database is null || keys is null || github is null)
        {
            return new ConfigParseResult(null, reader.Problems);
        }

        var config = new CharterConfig
        {
            Port = port,
            BaseUrl = baseUrl,
            Mode = mode,
            Runners = runners,
            Database = database,
            Keys = keys,
            Models = models,
            GitHub = github,
            Logging = logging,
            Telemetry = telemetry,
            Auth = auth,
            Smtp = smtp,
            Budgets = budgets,
            Storage = storage,
            UpdateCheck = updateCheck,
            AllowRepoCreation = allowRepoCreation,
            DemoMode = demoMode,
        };

        return new ConfigParseResult(config, reader.Problems);
    }

    /// <summary>
    /// <c>CHARTER_RUNNER</c> is a comma-separated subset of the backends: the dispatcher routes by
    /// capability match, so several may be enabled at once (sections 2.2, 27.3).
    /// </summary>
    private static IReadOnlyList<RunnerBackend> ParseRunners(EnvReader reader)
    {
        IReadOnlyList<RunnerBackend> fallback = [RunnerBackend.GitHubActions];

        var raw = reader.Optional("CHARTER_RUNNER");
        if (raw is null)
        {
            return fallback;
        }

        var enabled = new List<RunnerBackend>();
        var unrecognised = new List<string>();

        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            RunnerBackend? backend = token.ToUpperInvariant() switch
            {
                "AGENT" => RunnerBackend.Agent,
                "GITHUB-ACTIONS" => RunnerBackend.GitHubActions,
                "DOCKER" => RunnerBackend.Docker,
                _ => null,
            };

            if (backend is null)
            {
                unrecognised.Add(token);
            }
            else if (!enabled.Contains(backend.Value))
            {
                enabled.Add(backend.Value);
            }
        }

        if (unrecognised.Count > 0)
        {
            reader.Error(
                "CHARTER_RUNNER",
                "CHARTER_RUNNER must be a comma-separated subset of " +
                $"{string.Join(", ", ConfigNames.Runners)}, for example 'github-actions,docker'. " +
                $"Unrecognised: {string.Join(", ", unrecognised)}.");
        }
        else if (enabled.Count == 0)
        {
            reader.Error(
                "CHARTER_RUNNER",
                "CHARTER_RUNNER names no backend. Set it to a comma-separated subset of " +
                $"{string.Join(", ", ConfigNames.Runners)}, or leave it unset for github-actions.");
        }

        return enabled.Count > 0 ? enabled : fallback;
    }
}
