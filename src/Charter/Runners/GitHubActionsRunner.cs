using System.Text.Json;
using System.Text.Json.Serialization;
using Charter.Domain;
using Microsoft.Extensions.Logging;

namespace Charter.Runners;

/// <summary>
/// The <c>client_payload</c> of the <c>repository_dispatch</c> that starts a session.
/// </summary>
/// <remarks>
/// <para>
/// This is a contract with <c>.github/workflows/agent-session.yml</c> in the <em>target</em>
/// repository, not with Charter's own workflows: every property below is read by name there.
/// <c>RunnerGitHubActionsTests</c> pins the names against the shipped workflow file so a rename
/// cannot silently start dispatching sessions that never run.
/// </para>
/// <para>
/// <strong>No credential ever appears here.</strong> <c>client_payload</c> is readable by anyone with
/// repository read access and is retained in the GitHub events API. The workflow's first step
/// exchanges a per-repository bearer secret for a short-TTL installation token at
/// <c>{callback_url}/credentials</c> instead (sections 7.4, 33.5).
/// </para>
/// </remarks>
public sealed record GitHubActionsClientPayload
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    /// <summary>
    /// The base the job calls back on. The workflow appends <c>/credentials</c>, <c>/events</c> and
    /// <c>/result</c> to it, so it must have no trailing slash.
    /// </summary>
    [JsonPropertyName("callback_url")]
    public required string CallbackUrl { get; init; }

    [JsonPropertyName("repo")]
    public required string Repo { get; init; }

    [JsonPropertyName("base_branch")]
    public required string BaseBranch { get; init; }

    [JsonPropertyName("base_commit_sha")]
    public required string BaseCommitSha { get; init; }

    [JsonPropertyName("adapter")]
    public required string Adapter { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("runner_image")]
    public required string RunnerImage { get; init; }

    /// <summary>
    /// A string, not a number: the workflow evaluates
    /// <c>fromJSON(client_payload.timeout_minutes || '60')</c>, and GitHub's <c>fromJSON</c> takes a
    /// string. Sending <c>60</c> as a JSON number makes the workflow fail before the first step.
    /// </summary>
    [JsonPropertyName("timeout_minutes")]
    public required string TimeoutMinutes { get; init; }

    [JsonPropertyName("spec_url")]
    public required string SpecUrl { get; init; }

    /// <summary>Section 7.3. Sent for the shim to enforce; the control plane never relies on it.</summary>
    [JsonPropertyName("path_scope")]
    public required GitHubActionsPathScope PathScope { get; init; }

    /// <summary>Idempotency for the operator reading the events API, and for the runner's own logs.</summary>
    [JsonPropertyName("dispatch_key")]
    public required string DispatchKey { get; init; }
}

/// <summary>The <c>path_scope</c> object of the client payload.</summary>
public sealed record GitHubActionsPathScope
{
    [JsonPropertyName("allow")]
    public required IReadOnlyList<string> Allow { get; init; }

    [JsonPropertyName("deny")]
    public required IReadOnlyList<string> Deny { get; init; }
}

/// <summary>
/// The GitHub REST calls the Actions backend needs, kept behind a seam.
/// </summary>
/// <remarks>
/// Implemented over the GitHub App installation token in <c>Charter.GitHub</c>. Declared here so the
/// runner has no compile-time dependency on the GitHub client, and so tests can dispatch a session
/// without a network call.
/// </remarks>
public interface IGitHubRepositoryDispatcher
{
    /// <summary><c>POST /repos/{repo}/dispatches</c>.</summary>
    Task RepositoryDispatchAsync(
        string repoFullName,
        string eventType,
        string clientPayloadJson,
        CancellationToken cancellationToken = default);

    /// <summary><c>POST /repos/{repo}/actions/runs/{runId}/cancel</c>. False when the run is already over.</summary>
    Task<bool> CancelWorkflowRunAsync(
        string repoFullName,
        long runId,
        CancellationToken cancellationToken = default);
}

/// <summary>What the Actions backend advertises and which image it asks for (sections 32.1, 32.5).</summary>
public sealed record GitHubActionsRunnerOptions
{
    /// <summary>The <c>repository_dispatch</c> type the shipped workflow listens for.</summary>
    public const string EventType = "charter-session";

    /// <summary>The workflow's own fallback, repeated here so a dispatch is explicit about it.</summary>
    public const string DefaultRunnerImage = "ghcr.io/binn/charter-runner-fullstack:1";

    /// <summary>
    /// What a GitHub-hosted job can offer. <c>runs-on: ubuntu-latest</c> in the shipped workflow, with
    /// the fullstack image on the <c>container:</c> key, so: Linux, .NET and Node. Nothing
    /// hardware-attached, which is exactly the section 27.3 argument for the Charter Agent.
    /// </summary>
    public IReadOnlyList<string> Capabilities { get; init; } = ["linux", "dotnet:10", "node:22"];

    public string DefaultImage { get; init; } = DefaultRunnerImage;

    /// <summary>Wall-clock cap (section 27.5). Also the workflow's <c>timeout-minutes</c>.</summary>
    public int DefaultTimeoutMinutes { get; init; } = 60;
}

/// <summary>
/// The zero-infrastructure default backend (section 2.2): a <c>repository_dispatch</c> triggers
/// <c>.github/workflows/agent-session.yml</c> in the target repository, and events stream back to a
/// Charter webhook.
/// </summary>
/// <remarks>
/// Dispatch is fire-and-forget by construction — <c>repository_dispatch</c> returns 204 with no run
/// id — so the run URL only becomes known when the workflow's first callback reports
/// <c>session_started</c>. That is why the external reference is recorded from an event rather than
/// from the dispatch, and why cancellation takes it as a parameter instead of looking it up in a
/// field: there is no field to look it up in after a restart (section 2.3).
/// </remarks>
public sealed class GitHubActionsRunner : IAgentRunner
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly IGitHubRepositoryDispatcher _github;
    private readonly GitHubActionsRunnerOptions _options;
    private readonly ILogger<GitHubActionsRunner> _logger;

    public GitHubActionsRunner(
        IGitHubRepositoryDispatcher github,
        GitHubActionsRunnerOptions options,
        ILogger<GitHubActionsRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(github);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _github = github;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public RunnerKind Kind => RunnerKind.GitHubActions;

    /// <inheritdoc />
    public ValueTask<RunnerDescriptor> DescribeAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new RunnerDescriptor(
            RunnerKind.GitHubActions,
            "GitHub Actions",
            RunnerCapability.ExpandAll(_options.Capabilities)));

    /// <inheritdoc />
    public async Task<RunnerDispatchResult> DispatchAsync(
        RunnerDispatch dispatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispatch);

        var payload = BuildPayload(dispatch, _options);

        await _github.RepositoryDispatchAsync(
            dispatch.RepoFullName,
            GitHubActionsRunnerOptions.EventType,
            JsonSerializer.Serialize(payload, PayloadJson),
            cancellationToken);

        _logger.LogInformation(
            "Dispatched session {SessionId} to GitHub Actions in {Repo} at {BaseCommitSha}",
            dispatch.SessionId,
            dispatch.RepoFullName,
            dispatch.BaseCommitSha);

        // No run id yet: repository_dispatch answers 204 with an empty body. The run URL arrives with
        // the workflow's session_started callback and is recorded on the session's event stream.
        return RunnerDispatchResult.Ok();
    }

    /// <inheritdoc />
    public async Task<RunnerCancelResult> CancelAsync(
        RunnerCancellation cancellation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cancellation);

        if (!TryParseRunReference(cancellation.ExternalReference, out var repo, out var runId))
        {
            return RunnerCancelResult.NothingToStop(
                "The workflow run had not reported itself yet, so there is nothing to cancel on GitHub. "
                + "The session is settled here and the run, if it starts, will be rejected.");
        }

        var cancelled = await _github.CancelWorkflowRunAsync(repo, runId, cancellationToken);

        return cancelled
            ? RunnerCancelResult.Confirmed
            : RunnerCancelResult.NothingToStop("The workflow run had already finished.");
    }

    /// <summary>Builds the client payload. Separated so tests can assert the contract without HTTP.</summary>
    public static GitHubActionsClientPayload BuildPayload(
        RunnerDispatch dispatch,
        GitHubActionsRunnerOptions options)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(options);

        return new GitHubActionsClientPayload
        {
            SessionId = dispatch.SessionId.ToString(),
            CallbackUrl = dispatch.CallbackUrl.ToString().TrimEnd('/'),
            Repo = dispatch.RepoFullName,
            BaseBranch = dispatch.BaseBranch,
            BaseCommitSha = dispatch.BaseCommitSha,
            Adapter = dispatch.AdapterId,
            Model = dispatch.Model,
            RunnerImage = string.IsNullOrWhiteSpace(dispatch.RunnerImage)
                ? options.DefaultImage
                : dispatch.RunnerImage,
            TimeoutMinutes = (dispatch.TimeoutMinutes > 0
                ? dispatch.TimeoutMinutes
                : options.DefaultTimeoutMinutes).ToString(System.Globalization.CultureInfo.InvariantCulture),
            SpecUrl = dispatch.SpecUrl.ToString(),
            PathScope = new GitHubActionsPathScope
            {
                Allow = dispatch.PathScope.Allow,
                Deny = dispatch.PathScope.Deny,
            },
            DispatchKey = dispatch.DispatchKey,
        };
    }

    /// <summary>
    /// Pulls <c>owner/name</c> and the run id out of the run URL the workflow reports as
    /// <c>CHARTER_RUN_URL</c>: <c>https://github.com/owner/name/actions/runs/123456</c>.
    /// </summary>
    internal static bool TryParseRunReference(string? reference, out string repoFullName, out long runId)
    {
        repoFullName = string.Empty;
        runId = 0;

        if (string.IsNullOrWhiteSpace(reference)
            || !Uri.TryCreate(reference, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 5
            || !string.Equals(segments[^3], "actions", StringComparison.Ordinal)
            || !string.Equals(segments[^2], "runs", StringComparison.Ordinal)
            || !long.TryParse(segments[^1], System.Globalization.CultureInfo.InvariantCulture, out runId))
        {
            return false;
        }

        repoFullName = $"{segments[0]}/{segments[1]}";
        return true;
    }
}

/// <summary>
/// The fallback used when no GitHub client is registered.
/// </summary>
/// <remarks>
/// Refuses with the variable names to set rather than throwing a null reference three layers down.
/// Section 4.1's fail-loud rule applies to a missing dependency as much as to a missing variable.
/// </remarks>
public sealed class UnconfiguredGitHubRepositoryDispatcher : IGitHubRepositoryDispatcher
{
    /// <inheritdoc />
    public Task RepositoryDispatchAsync(
        string repoFullName,
        string eventType,
        string clientPayloadJson,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "The GitHub Actions runner cannot dispatch a session because no GitHub App client is "
            + "registered. Set GITHUB_APP_ID, GITHUB_APP_PRIVATE_KEY and GITHUB_WEBHOOK_SECRET, or "
            + "set CHARTER_RUNNER to a backend that does not need GitHub.");

    /// <inheritdoc />
    public Task<bool> CancelWorkflowRunAsync(
        string repoFullName,
        long runId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}
