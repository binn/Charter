namespace Charter.Configuration;

/// <summary>
/// <c>CHARTER_MODE</c>. Note section 7.2: personal mode is <em>not</em> a mode - it is an Organization
/// with one Member holding every role, on the same authorisation code path. This value selects the
/// shape of onboarding and which auth options are offered, never a permission shortcut.
/// </summary>
public enum CharterMode
{
    /// <summary>One person, one organisation, every role. The default.</summary>
    Personal,

    /// <summary>Several people with distinct roles; SAML becomes available (section 21).</summary>
    Organization,
}

/// <summary>Execution backends enabled by <c>CHARTER_RUNNER</c> (sections 2.2, 27.3).</summary>
public enum RunnerBackend
{
    /// <summary>The Charter Agent daemon (section 33). Available from Phase 2 (section 23).</summary>
    Agent,

    /// <summary>GitHub Actions workflow dispatch. The default.</summary>
    GitHubActions,

    /// <summary>A local Docker daemon.</summary>
    Docker,
}

/// <summary>Release channel followed by the update check (section 28).</summary>
public enum UpdateChannel
{
    /// <summary>Published stable releases only. The default.</summary>
    Stable,

    /// <summary>Includes pre-releases.</summary>
    Prerelease,
}

/// <summary>Wire names for the enums above, as an operator writes them in the environment.</summary>
public static class ConfigNames
{
    /// <summary>Accepted <c>CHARTER_MODE</c> values, in the order they are listed in errors.</summary>
    public static IReadOnlyList<string> Modes { get; } = ["personal", "organization"];

    /// <summary>Accepted <c>CHARTER_RUNNER</c> tokens.</summary>
    public static IReadOnlyList<string> Runners { get; } = ["agent", "github-actions", "docker"];

    /// <summary>Accepted <c>CHARTER_UPDATE_CHANNEL</c> values.</summary>
    public static IReadOnlyList<string> UpdateChannels { get; } = ["stable", "prerelease"];

    /// <summary>Accepted <c>LOGGING_MODE</c> values (section 19.1).</summary>
    public static IReadOnlyList<string> LoggingModes { get; } = ["DEFAULT", "JSON", "RAILWAY_JSON"];

    /// <summary>The environment token for <paramref name="mode"/>.</summary>
    public static string ToWireName(this CharterMode mode) => mode switch
    {
        CharterMode.Personal => "personal",
        CharterMode.Organization => "organization",
        _ => mode.ToString(),
    };

    /// <summary>The environment token for <paramref name="backend"/>.</summary>
    public static string ToWireName(this RunnerBackend backend) => backend switch
    {
        RunnerBackend.Agent => "agent",
        RunnerBackend.GitHubActions => "github-actions",
        RunnerBackend.Docker => "docker",
        _ => backend.ToString(),
    };

    /// <summary>The environment token for <paramref name="channel"/>.</summary>
    public static string ToWireName(this UpdateChannel channel) => channel switch
    {
        UpdateChannel.Stable => "stable",
        UpdateChannel.Prerelease => "prerelease",
        _ => channel.ToString(),
    };
}
