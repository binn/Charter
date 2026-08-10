using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Charter.Data;

namespace Charter.Runners.Agent;

/// <summary>
/// What the queue row for an agent-claimable job holds.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no credential in this shape and there must never be one.</strong> A queued row
/// lives for as long as the backlog does, so a credential written here would be a live credential
/// sitting in Postgres for hours, readable by every backup, every replica and every support query.
/// Secrets are minted at claim time instead, by <see cref="AgentJobAssignmentBuilder"/>, and exist
/// only in the one frame that carries them (sections 33.5, 7.4).
/// </para>
/// <para>
/// Names are snake_case to match <c>BuildJobPayload</c>, which is the other thing written into
/// <c>jobs.payload</c>. Everything except <see cref="SessionId"/> is optional so that a row written
/// by an older control plane still grants.
/// </para>
/// </remarks>
public sealed record AgentJobPayload
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [JsonPropertyName("session_id")]
    public required Guid SessionId { get; init; }

    [JsonPropertyName("repo")]
    public string? RepoFullName { get; init; }

    [JsonPropertyName("clone_url")]
    public string? CloneUrl { get; init; }

    [JsonPropertyName("base_branch")]
    public string? BaseBranch { get; init; }

    [JsonPropertyName("base_commit_sha")]
    public string? BaseCommitSha { get; init; }

    [JsonPropertyName("adapter")]
    public string? AdapterId { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("runner_image")]
    public string? RunnerImage { get; init; }

    [JsonPropertyName("callback_url")]
    public string? CallbackUrl { get; init; }

    [JsonPropertyName("spec_url")]
    public string? SpecUrl { get; init; }

    [JsonPropertyName("allow_paths")]
    public IReadOnlyList<string> AllowPaths { get; init; } = [];

    [JsonPropertyName("deny_paths")]
    public IReadOnlyList<string> DenyPaths { get; init; } = [];

    /// <summary>Section 27.3, as the session declared it — without the agent-routing marker.</summary>
    [JsonPropertyName("require")]
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = [];

    [JsonPropertyName("timeout_minutes")]
    public int? TimeoutMinutes { get; init; }

    /// <summary>The dispatch's idempotency key, carried so a log line can be correlated to it.</summary>
    [JsonPropertyName("dispatch_key")]
    public string? DispatchKey { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Parses a payload, tolerating anything a newer or older Charter added.</summary>
    public static AgentJobPayload? TryParse(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        try
        {
            return JsonSerializer.Deserialize<AgentJobPayload>(payload, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Projects one dispatch into the row the agent will later claim.</summary>
    public static AgentJobPayload From(RunnerDispatch dispatch, string? cloneUrl = null)
    {
        ArgumentNullException.ThrowIfNull(dispatch);

        return new AgentJobPayload
        {
            SessionId = dispatch.SessionId,
            RepoFullName = dispatch.RepoFullName,
            CloneUrl = cloneUrl ?? $"https://github.com/{dispatch.RepoFullName}.git",
            BaseBranch = dispatch.BaseBranch,
            BaseCommitSha = dispatch.BaseCommitSha,
            AdapterId = dispatch.AdapterId,
            Model = dispatch.Model,
            RunnerImage = dispatch.RunnerImage,
            CallbackUrl = dispatch.CallbackUrl.ToString().TrimEnd('/'),
            SpecUrl = dispatch.SpecUrl.ToString(),
            AllowPaths = dispatch.PathScope.Allow,
            DenyPaths = dispatch.PathScope.Deny,
            RequiredCapabilities = dispatch.RequiredCapabilities,
            TimeoutMinutes = dispatch.TimeoutMinutes,
            DispatchKey = dispatch.DispatchKey,
        };
    }
}

/// <summary>
/// Turns a claimed queue row into the <see cref="JobAssignment"/> that goes on the wire, minting the
/// per-job credentials in the same breath (section 33.5).
/// </summary>
/// <remarks>
/// Separated from the connection so the shape of a grant — and, more importantly, the shape of what
/// is <em>not</em> in one — can be asserted without a socket, a database or a GitHub App.
/// </remarks>
public static class AgentJobAssignmentBuilder
{
    /// <summary>The env var the shim reads its path scope from (section 7.3).</summary>
    public const string PathScopeVariable = Shim.ShimPathScopeEnvironment.Variable;

    /// <summary>The env var the shim authenticates its callbacks with.</summary>
    public const string EventTokenVariable = Shim.ShimHttp.EventTokenVariable;

    /// <summary>
    /// Builds the assignment for one claimed job.
    /// </summary>
    /// <param name="job">The row the queue handed this agent, lease and attempt included.</param>
    /// <param name="payload">The parsed <see cref="AgentJobPayload"/> from that row.</param>
    /// <param name="secrets">
    /// Minted moments ago and never persisted. Null when no broker is configured, which produces a
    /// grant an agent will fail loudly on rather than a grant carrying a credential from elsewhere.
    /// </param>
    /// <param name="eventToken">
    /// The per-session callback token. It rides in the command environment rather than in
    /// <see cref="JobSecrets"/> because that envelope has exactly two slots by design — see the
    /// remarks on <see cref="JobSecrets"/>.
    /// </param>
    /// <param name="options">The timing and image defaults this instance hands out.</param>
    public static JobAssignment Build(
        ClaimedJob job,
        AgentJobPayload payload,
        JobSecrets? secrets,
        string? eventToken,
        AgentPlaneOptions options)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(options);

        var required = job.RequiredCapabilities
            .Where(capability => !string.Equals(capability, AgentRunner.ClaimCapability, StringComparison.Ordinal))
            .ToArray();

        var timeoutMinutes = payload.TimeoutMinutes is > 0 ? payload.TimeoutMinutes.Value : options.DefaultTimeoutMinutes;

        return new JobAssignment
        {
            JobId = job.Id.ToString("D"),
            Type = EnumDbNames<Domain.JobType>.ToDb(job.Type),
            LeaseExpiresAt = job.LeaseExpiresAt,
            Attempt = job.Attempts,
            MaxAttempts = job.MaxAttempts,
            RequiredCapabilities = required,
            Repo = payload.RepoFullName is { Length: > 0 } repo
                ? new JobRepo
                {
                    FullName = repo,
                    CloneUrl = payload.CloneUrl ?? $"https://github.com/{repo}.git",
                    DefaultBranch = payload.BaseBranch,
                    Branch = payload.BaseBranch,

                    // Section 32.3: caches and git mirrors are scoped to the repository, never shared.
                    CacheScope = repo,
                }
                : null,
            RunnerImage = string.IsNullOrWhiteSpace(payload.RunnerImage)
                ? options.DefaultRunnerImage
                : payload.RunnerImage,
            Command = BuildCommand(payload, required, eventToken, options),
            Secrets = secrets,
            Payload = JsonSerializer.SerializeToElement(
                new
                {
                    session_id = payload.SessionId.ToString("D"),
                    dispatch_key = payload.DispatchKey,
                },
                AgentJson.Options),
            TimeoutSeconds = timeoutMinutes * 60,
        };
    }

    /// <summary>
    /// The shim invocation, spelled exactly as <c>ShimCommandLine.Parse</c> reads it.
    /// </summary>
    /// <remarks>
    /// The same shim the shipped workflow runs, driven through the same documented flag surface
    /// (section 3.1). What differs is only how it was started: a workflow step there, a child process
    /// of <c>charter-agent</c> here.
    /// </remarks>
    private static JobCommand BuildCommand(
        AgentJobPayload payload,
        IReadOnlyList<string> required,
        string? eventToken,
        AgentPlaneOptions options)
    {
        var arguments = new List<string>
        {
            "run",
            "--session-id", payload.SessionId.ToString("D"),
            "--adapter", payload.AdapterId ?? "claude-code",
            "--model", payload.Model ?? string.Empty,
            "--spec-url", payload.SpecUrl ?? string.Empty,
            "--callback-url", payload.CallbackUrl ?? string.Empty,
            "--stream-events",
        };

        if (payload.RepoFullName is { Length: > 0 } repo)
        {
            arguments.AddRange(["--repo", repo]);
        }

        if (payload.BaseBranch is { Length: > 0 } branch)
        {
            arguments.AddRange(["--base-branch", branch]);
        }

        if (payload.BaseCommitSha is { Length: > 0 } sha)
        {
            arguments.AddRange(["--base-commit", sha]);
        }

        if (payload.RunnerImage is { Length: > 0 } image)
        {
            arguments.AddRange(["--runner-image", image]);
        }

        foreach (var capability in required)
        {
            arguments.AddRange(["--require", capability]);
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CHARTER_SESSION_ID"] = payload.SessionId.ToString("D"),
            [PathScopeVariable] = new JsonObject
            {
                ["allow"] = ToArray(payload.AllowPaths),
                ["deny"] = ToArray(payload.DenyPaths),
            }.ToJsonString(),
        };

        if (!string.IsNullOrWhiteSpace(eventToken))
        {
            environment[EventTokenVariable] = eventToken;
        }

        return new JobCommand
        {
            Executable = options.ShimExecutable,
            Arguments = arguments,
            Environment = environment,
            WorkingSubdirectory = "workspace",
        };
    }

    private static JsonArray ToArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }
}
