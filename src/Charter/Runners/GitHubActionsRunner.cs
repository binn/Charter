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
/// <para>
/// <see cref="SessionToken"/> is not an exception to that. It is not a credential: it grants nothing
/// without <c>secrets.CHARTER_SESSION_SECRET</c>, which is not readable from a payload. What it does
/// is name the one session this run is allowed to exchange for, which a per-repository secret cannot.
/// </para>
/// </remarks>
public sealed record GitHubActionsClientPayload
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    /// <summary>
    /// The session-scoping half of the credential exchange (section 7.4).
    /// </summary>
    /// <remarks>
    /// Minted per session by <see cref="RunnerSessionTokens.DispatchTokenFor"/> and delivered only in
    /// the dispatch for that session, so a run started for one session has no way to produce another's.
    /// </remarks>
    [JsonPropertyName("session_token")]
    public required string SessionToken { get; init; }

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

    /// <summary>The branch the workflow's shim commits and pushes the session's work on.</summary>
    [JsonPropertyName("branch")]
    public required string Branch { get; init; }

    /// <summary>
    /// The requester, for the authorship of that commit (sections 7.3, 24).
    /// </summary>
    /// <remarks>
    /// The one piece of personal data in the payload, and it is here because it ends up in the
    /// repository's git history either way: a commit has an author, and Charter's answer to who that
    /// is has to be the person who asked rather than a machine account. Anyone who can read
    /// <c>client_payload</c> can already read the repository the commit lands in.
    /// </remarks>
    [JsonPropertyName("requester_name")]
    public required string RequesterName { get; init; }

    [JsonPropertyName("requester_email")]
    public required string RequesterEmail { get; init; }

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
    private readonly RunnerSessionTokens? _tokens;

    /// <param name="github">The dispatch seam.</param>
    /// <param name="options">What this backend advertises and which image it asks for.</param>
    /// <param name="logger">Section 19.</param>
    /// <param name="tokens">
    /// Mints the per-session dispatch token the workflow forwards to the credential exchange. Optional
    /// in the same way <c>DockerRunner</c>'s is — <c>AddCharterRunners</c> runs before
    /// <c>AddCharterOrchestration</c> registers it — and a dispatch without it fails loudly rather
    /// than sending a payload the exchange will refuse.
    /// </param>
    public GitHubActionsRunner(
        IGitHubRepositoryDispatcher github,
        GitHubActionsRunnerOptions options,
        ILogger<GitHubActionsRunner> logger,
        RunnerSessionTokens? tokens = null)
    {
        ArgumentNullException.ThrowIfNull(github);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _github = github;
        _options = options;
        _logger = logger;
        _tokens = tokens;
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

        if (_tokens is null)
        {
            return RunnerDispatchResult.Refused(
                "The GitHub Actions runner cannot dispatch a session because no signing key is "
                + "registered, so the per-session token the credential exchange requires cannot be "
                + "minted. Set CHARTER_SECRET_KEY.");
        }

        var payload = BuildPayload(dispatch, _options, _tokens.DispatchTokenFor(dispatch.SessionId));

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

    /// <summary>
    /// Section 11: cancel the workflow run this session started, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The repository is read out of the reference, and the reference reached the journal from the
    /// execution plane, so it is checked against the repository the session actually belongs to before
    /// a single call is made. Without that check, cancelling one session issued a write against
    /// whatever repository the run URL named — with the instance's own credential, against any other
    /// repository connected to it — and reported success while the real run kept running and kept
    /// spending. <see cref="RunnerRunReference"/> now refuses such a URL at the callback, but rows
    /// written before it existed are still in the journal, so this is the gate that has to hold.
    /// </para>
    /// <para>
    /// Every path that does not end in GitHub confirming a cancellation returns
    /// <see cref="RunnerCancelResult.NothingToStop"/>. A cancel that reports success for work that is
    /// still running is the more dangerous half of getting this wrong.
    /// </para>
    /// </remarks>
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

        if (string.IsNullOrWhiteSpace(cancellation.RepoFullName)
            || !string.Equals(repo, cancellation.RepoFullName, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Refusing to cancel session {SessionId} through a run reference naming {NamedRepo}: the "
                + "session belongs to {SessionRepo}. No call was made to GitHub.",
                cancellation.SessionId,
                repo,
                cancellation.RepoFullName ?? "(unknown)");

            return RunnerCancelResult.NothingToStop(
                "The run reference recorded for this session names a repository the session does not "
                + "belong to, so Charter will not act on it. The session is settled here, but if a "
                + "workflow run is still going it has not been stopped — check the repository's Actions "
                + "tab and cancel it there.");
        }

        var cancelled = await _github.CancelWorkflowRunAsync(repo, runId, cancellationToken);

        return cancelled
            ? RunnerCancelResult.Confirmed
            : RunnerCancelResult.NothingToStop("The workflow run had already finished.");
    }

    /// <summary>Builds the client payload. Separated so tests can assert the contract without HTTP.</summary>
    /// <param name="dispatch">The session being dispatched.</param>
    /// <param name="options">Backend defaults for image and timeout.</param>
    /// <param name="sessionToken">
    /// <see cref="RunnerSessionTokens.DispatchTokenFor"/> for this session. The only thing in the
    /// payload that is session-scoped rather than repository-scoped.
    /// </param>
    public static GitHubActionsClientPayload BuildPayload(
        RunnerDispatch dispatch,
        GitHubActionsRunnerOptions options,
        string sessionToken)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionToken);

        return new GitHubActionsClientPayload
        {
            SessionId = dispatch.SessionId.ToString(),
            SessionToken = sessionToken,
            CallbackUrl = dispatch.CallbackUrl.ToString().TrimEnd('/'),
            Repo = dispatch.RepoFullName,
            BaseBranch = dispatch.BaseBranch,
            BaseCommitSha = dispatch.BaseCommitSha,
            Branch = string.IsNullOrWhiteSpace(dispatch.Branch)
                ? Charter.VersionControl.ChangeRequestPublisher.BranchFor(dispatch.SessionId)
                : dispatch.Branch,
            RequesterName = dispatch.Requester?.DisplayName ?? string.Empty,
            RequesterEmail = dispatch.Requester?.Email ?? string.Empty,
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
