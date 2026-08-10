namespace Charter.Configuration;

/// <summary>
/// Every environment variable in section 4.2, parsed and validated once at startup.
/// </summary>
/// <remarks>
/// <para>
/// Section 4.1: no <c>appsettings.json</c>, no <c>Section__Nested__Key</c> convention. A hand-written
/// parser loads flat environment variables into this immutable record, which is registered as a
/// singleton by <see cref="ConfigurationServiceCollectionExtensions.AddCharterConfig"/>. Validation
/// happens once, reports every problem together, and the process exits non-zero rather than
/// discovering a missing variable on first use.
/// </para>
/// <para>
/// Secrets are wrapped in <see cref="Secret"/>, so the compiler-generated <c>ToString()</c> of this
/// record and of every section below prints a placeholder rather than a key. Reading a secret is an
/// explicit <see cref="Secret.Reveal"/> call.
/// </para>
/// </remarks>
public sealed record CharterConfig
{
    /// <summary><c>PORT</c>, default 8080 (PaaS convention).</summary>
    public required int Port { get; init; }

    /// <summary><c>CHARTER_BASE_URL</c>. Public URL, for webhooks and links.</summary>
    public required Uri BaseUrl { get; init; }

    /// <summary>
    /// <c>CHARTER_MODE</c>. Section 7.2: personal is not a permission shortcut, it is an Organization
    /// with one Member holding every role.
    /// </summary>
    public required CharterMode Mode { get; init; }

    /// <summary>
    /// <c>CHARTER_RUNNER</c>. Backends the dispatcher may route to, by capability match
    /// (sections 2.2, 27.3). Comma-separated in the environment; deduplicated and order-preserved here.
    /// </summary>
    public required IReadOnlyList<RunnerBackend> Runners { get; init; }

    /// <summary>Postgres, from <c>DATABASE_URL</c> (section 4.3).</summary>
    public required DatabaseConfig Database { get; init; }

    /// <summary>The two instance keys (section 20b.2).</summary>
    public required KeyConfig Keys { get; init; }

    /// <summary>Model selection and instance-level fallback credentials (section 20b).</summary>
    public required ModelConfig Models { get; init; }

    /// <summary>The GitHub App this instance authenticates as.</summary>
    public required GitHubConfig GitHub { get; init; }

    /// <summary>The Serilog pipeline (sections 19, 19.1).</summary>
    public required LoggingConfig Logging { get; init; }

    /// <summary>OpenTelemetry export (section 19.2).</summary>
    public required TelemetryConfig Telemetry { get; init; }

    /// <summary>OAuth providers and SAML (section 21).</summary>
    public required AuthConfig Auth { get; init; }

    /// <summary>
    /// Outbound email (section 22; change spec 001, part C). Always present: <c>none</c> is a
    /// supported configuration, not an absent one.
    /// </summary>
    public required EmailConfig Email { get; init; }

    /// <summary>The SMTP endpoint, or <c>null</c> when email is off or delivered another way.</summary>
    public SmtpConfig? Smtp => Email.Smtp;

    /// <summary>Default spend limits (section 34.9).</summary>
    public required BudgetConfig Budgets { get; init; }

    /// <summary>
    /// Object storage, or <c>null</c> when <c>CHARTER_STORAGE_ENDPOINT</c> is unset. Charter runs
    /// without it and refuses - naming these variables - to onboard a repo whose project type needs
    /// artifact storage (sections 4.2, 27.5).
    /// </summary>
    public StorageConfig? Storage { get; init; }

    /// <summary>The daily release check (section 28).</summary>
    public required UpdateCheckConfig UpdateCheck { get; init; }

    /// <summary>
    /// <c>CHARTER_ALLOW_REPO_CREATION</c>, default false. Section 26.10 treats repo creation as a
    /// privilege escalation, so it is opt-in.
    /// </summary>
    public required bool AllowRepoCreation { get; init; }

    /// <summary>
    /// <c>CHARTER_DEMO</c>, default false. Seeds a fake organisation and disables outbound calls so
    /// someone can evaluate Charter without a GitHub App or a token (section 30.6).
    /// </summary>
    public required bool DemoMode { get; init; }

    /// <summary>True when object storage is configured.</summary>
    public bool StorageEnabled => Storage is not null;

    /// <summary>True when email notifications can be delivered.</summary>
    public bool SmtpEnabled => Email.Enabled;

    /// <summary>False in demo mode, which disables every outbound call (section 30.6).</summary>
    public bool OutboundCallsAllowed => !DemoMode;

    /// <summary>
    /// Whether the release check should run: enabled, and not suppressed by demo mode.
    /// </summary>
    public bool ShouldCheckForUpdates => UpdateCheck.Enabled && OutboundCallsAllowed;

    /// <summary>True when <paramref name="backend"/> is enabled by <c>CHARTER_RUNNER</c>.</summary>
    public bool SupportsRunner(RunnerBackend backend) => Runners.Contains(backend);

    /// <summary>Parses and validates the whole of section 4.2 from the process environment.</summary>
    /// <exception cref="ConfigException">
    /// One or more variables are invalid. Every blocking problem found is reported together.
    /// </exception>
    public static CharterConfig FromEnvironment()
        => FromEnvironment(name => Environment.GetEnvironmentVariable(name));

    /// <summary>Parses and validates the whole of section 4.2 from an arbitrary variable source.</summary>
    /// <exception cref="ConfigException">
    /// One or more variables are invalid. Every blocking problem found is reported together.
    /// </exception>
    public static CharterConfig FromEnvironment(Func<string, string?> read)
        => CharterConfigParser.Parse(read).ConfigOrThrow();

    /// <summary>
    /// Projects the boot subset the host needs before the container is built.
    /// </summary>
    /// <remarks>
    /// <see cref="StartupOptions"/> exists because the logging pipeline, the listening port and the
    /// database connection are needed before anything else can be configured, and it parses the same
    /// variables with the same messages. Deriving it here rather than parsing twice keeps the two
    /// from drifting once the host boots from the full config.
    /// </remarks>
    public StartupOptions ToStartupOptions() => new()
    {
        Port = Port,
        LoggingMode = Logging.Mode,
        MinimumLogLevel = Logging.MinimumLevel,
        SeqUrl = Logging.SeqUrl,
        SeqApiKey = Logging.SeqApiKey?.Reveal(),
        OtlpEndpoint = Telemetry.OtlpEndpoint,
        OtlpHeaders = Telemetry.OtlpHeaders,
        OtlpProtocol = Telemetry.OtlpProtocol,
        ServiceName = Telemetry.ServiceName,
        DatabaseConnectionString = Database.ConnectionString.Reveal(),
        IncludeTranscripts = Logging.IncludeTranscripts,
    };
}
