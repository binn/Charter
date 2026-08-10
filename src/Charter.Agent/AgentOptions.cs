namespace Charter.Agent;

/// <summary>How the agent executes claimed jobs on its host (section 33.2).</summary>
public enum AgentExecutionMode
{
    /// <summary>Spawn ephemeral containers through the local Docker socket.</summary>
    Docker,

    /// <summary>
    /// Run jobs directly on the host, under a dedicated unprivileged user account with a scoped
    /// working directory. Required where containers are not possible - macOS with Xcode cannot be
    /// containerised, and USB-attached embedded targets are awkward to pass through.
    /// Isolation is process-level, not container-level.
    /// </summary>
    Native,
}

public static class AgentExecutionModeExtensions
{
    /// <summary>The wire spelling: lowercase, stable, matched by the control plane.</summary>
    public static string ToWire(this AgentExecutionMode mode) =>
        mode == AgentExecutionMode.Native ? "native" : "docker";
}

/// <summary>Command-line configuration for the Charter Agent daemon.</summary>
public sealed record AgentOptions
{
    /// <summary>
    /// The value of <c>--native-user</c> that opts out of a dedicated account. Spelled out rather
    /// than implied by an empty string, so the weaker isolation is a choice someone typed.
    /// </summary>
    public const string RunAsSelf = "self";

    /// <summary>Control-plane base URL the agent dials out to. <c>--server</c>.</summary>
    public required Uri Server { get; init; }

    /// <summary>
    /// Single-use, short-TTL pairing token from the admin UI. <c>--token</c>. Null once the agent
    /// holds a credential from a previous run - a spent pairing token cannot be presented twice.
    /// </summary>
    public string? Token { get; init; }

    /// <summary><c>--mode docker|native</c>.</summary>
    public required AgentExecutionMode Mode { get; init; }

    /// <summary>Maximum jobs claimed at once. <c>--concurrency</c>, defaults conservatively.</summary>
    public int Concurrency { get; init; } = 1;

    /// <summary>Human-readable name shown in the runners list. <c>--name</c>.</summary>
    public string Name { get; init; } = Environment.MachineName;

    /// <summary>Where the agent credential is kept between runs. <c>--state-dir</c>.</summary>
    public required string StateDirectory { get; init; }

    /// <summary>Root of the scoped per-job working directories. <c>--work-dir</c>.</summary>
    public required string WorkDirectory { get; init; }

    /// <summary>
    /// The dedicated unprivileged account native jobs run under (section 33.2), or
    /// <see cref="RunAsSelf"/> to run as the agent's own user with weaker isolation.
    /// </summary>
    public string NativeUser { get; init; } = "charter-runner";

    /// <summary>Local Docker socket. Never exposed off this host. <c>--docker-socket</c>.</summary>
    public string DockerSocket { get; init; } = "/var/run/docker.sock";

    /// <summary>
    /// How often to re-probe capabilities (section 32.2). Daily by default: a Mac mini that took an
    /// Xcode update overnight must not keep advertising the old version.
    /// </summary>
    public TimeSpan ReprobeInterval { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Starting heartbeat interval. The control plane's <c>welcome</c> overrides it.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Install a newer agent build when the control plane offers one (section 33.6). Off by default:
    /// the default is to warn and let the operator upgrade deliberately.
    /// </summary>
    public bool AutoUpdate { get; init; }

    /// <summary>Debug-level logging. <c>--verbose</c>.</summary>
    public bool Verbose { get; init; }

    /// <summary>True when native isolation is process-level only, with no dedicated account.</summary>
    public bool RunsJobsAsAgentUser =>
        Mode == AgentExecutionMode.Native &&
        string.Equals(NativeUser, RunAsSelf, StringComparison.OrdinalIgnoreCase);
}
