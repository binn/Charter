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

/// <summary>Command-line configuration for the Charter Agent daemon.</summary>
public sealed record AgentOptions
{
    /// <summary>Control-plane base URL the agent dials out to. <c>--server</c>.</summary>
    public required Uri Server { get; init; }

    /// <summary>Single-use, short-TTL pairing token from the admin UI. <c>--token</c>.</summary>
    public required string Token { get; init; }

    /// <summary><c>--mode docker|native</c>.</summary>
    public required AgentExecutionMode Mode { get; init; }

    /// <summary>Maximum jobs claimed at once. <c>--concurrency</c>, defaults conservatively.</summary>
    public int Concurrency { get; init; } = 1;

    /// <summary>Human-readable name shown in the runners list. <c>--name</c>.</summary>
    public string Name { get; init; } = Environment.MachineName;
}
