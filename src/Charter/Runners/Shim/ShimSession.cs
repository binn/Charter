using System.Text.Json.Nodes;
using Charter.Adapters;
using Charter.Domain;
using Charter.Runners.SchemaChanges;
using Charter.VersionControl;

namespace Charter.Runners.Shim;

/// <summary>One process the shim starts inside the sandbox.</summary>
public sealed record ShimProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string? StandardInput = null,
    IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>How it went.</summary>
public sealed record ShimProcessResult(int ExitCode, string? Error = null);

/// <summary>Starting processes, behind a seam so a session can be exercised without one.</summary>
public interface IShimProcessRunner
{
    /// <summary>
    /// Runs to completion, handing each line of standard output to <paramref name="onStandardOutputLine"/>
    /// as it arrives — not at the end. Section 11: a five to twenty minute silent gap reads as broken.
    /// </summary>
    Task<ShimProcessResult> RunAsync(
        ShimProcessRequest request,
        Func<string, CancellationToken, ValueTask>? onStandardOutputLine,
        CancellationToken cancellationToken);
}

/// <summary>Where the shim streams events (sections 2.2, 33.4).</summary>
public interface IShimEventSink
{
    ValueTask PublishAsync(ShimOutboundEvent outboundEvent, CancellationToken cancellationToken);

    ValueTask ReportResultAsync(ShimResult result, CancellationToken cancellationToken);
}

/// <summary>
/// Where the shim gets the spec.
/// </summary>
/// <remarks>
/// Fetched from the control plane rather than passed on the command line: section 16 makes
/// refinement a sanitisation boundary, and the agent must see the refined, human-approved spec — not
/// anything that could have been substituted into a dispatch payload readable by anyone with repo
/// read access.
/// </remarks>
public interface IShimSpecSource
{
    ValueTask<string> LoadAsync(Uri specUrl, CancellationToken cancellationToken);
}

/// <summary>How a session ended.</summary>
public enum ShimSessionState
{
    Completed,

    Failed,

    Cancelled,

    /// <summary>The agent tried to write outside its path scope (section 7.3).</summary>
    ScopeViolation,

    /// <summary>The image lacks a declared requirement (section 32.1). Never installed around.</summary>
    ToolchainMissing,

    /// <summary>A lockfile-only dependency install failed (section 16.2).</summary>
    InstallFailed,

    /// <summary>
    /// The agent's work could not be committed or pushed. Deliberately not the same outcome as an
    /// agent that changed nothing: one needs an operator, the other needs nobody.
    /// </summary>
    PublishFailed,

    /// <summary>
    /// The agent generated a destructive migration (section 15). The session halts and an engineer
    /// authors the migration by hand; the agent's intent is in the transcript.
    /// </summary>
    DestructiveMigration,
}

/// <summary>The terminal report the shim posts to <c>{callback_url}/result</c>.</summary>
public sealed record ShimResult(ShimSessionState State, string? Message = null, int ExitCode = 0)
{
    /// <summary>The wire spelling the shipped workflow's result step uses.</summary>
    public string Wire => State switch
    {
        ShimSessionState.Completed => "completed",
        ShimSessionState.Cancelled => "cancelled",
        _ => "failed",
    };
}

/// <summary>Everything one shim run needs. Nothing here is secret; credentials arrive separately.</summary>
public sealed record ShimRunRequest
{
    public required Guid SessionId { get; init; }

    /// <summary>The repository checkout. Every write is confined to it (section 7.3).</summary>
    public required string WorkspaceRoot { get; init; }

    public required string AdapterId { get; init; }

    /// <summary>Provider-qualified. The shim passes the CLI whichever form the adapter wants.</summary>
    public required string Model { get; init; }

    public required Uri SpecUrl { get; init; }

    public IReadOnlyList<string> AllowPaths { get; init; } = [];

    public IReadOnlyList<string> DenyPaths { get; init; } = [];

    /// <summary>What the session declared it needs (section 27.3).</summary>
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = [];

    /// <summary>What this host actually has, probed rather than assumed (section 32.2).</summary>
    public IReadOnlyList<string> ProbedCapabilities { get; init; } = [];

    public string? RunnerImage { get; init; }

    /// <summary>Section 16.2: opt-in, per repository, never a default.</summary>
    public bool AllowInstallScripts { get; init; }

    /// <summary>
    /// The branch the session's work is published on. Defaults to the convention
    /// <see cref="Charter.VersionControl.ChangeRequestPublisher.BranchFor"/> computes, so a runner
    /// that has never spoken to the control plane lands where the control plane will look.
    /// </summary>
    public string? Branch { get; init; }

    /// <summary>The remote the session branch is pushed to.</summary>
    public string Remote { get; init; } = "origin";

    /// <summary>
    /// Where to clone from when the workspace is not already a checkout.
    /// </summary>
    /// <remarks>
    /// Only <see cref="DockerRunner"/> needs this: a workflow job and a Charter Agent both hand the
    /// shim a checkout. Ignored when there is one.
    /// </remarks>
    public string? CloneUrl { get; init; }

    /// <summary>The revision the session was planned against (section 17).</summary>
    public string? BaseCommit { get; init; }

    /// <summary>
    /// The short-TTL, single-repository token git uses to clone and push (section 7.4).
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="AgentEnvironment"/> on purpose: the agent process is given the
    /// credentials its adapter declares, and a repository write token is not one of them.
    /// </remarks>
    public string? GitToken { get; init; }

    /// <summary>
    /// The requester, for the commit's authorship (sections 7.3, 24). Null commits with whatever the
    /// checkout is already configured with rather than with an identity invented in the sandbox.
    /// </summary>
    public ShimCommitIdentity? Requester { get; init; }

    /// <summary>
    /// False stops after the agent, leaving the work uncommitted.
    /// </summary>
    /// <remarks>
    /// Only for a workspace that is deliberately not a checkout. A session that runs with this off
    /// can never open a change request, which is the whole of the Phase 1 loop, so it is not a
    /// setting any backend turns on.
    /// </remarks>
    public bool Publish { get; init; } = true;

    /// <summary>Where the work lands, whether or not the dispatch spelled it out.</summary>
    public string BranchOrConvention => string.IsNullOrWhiteSpace(Branch)
        ? Charter.VersionControl.ChangeRequestPublisher.BranchFor(SessionId)
        : Branch;

    /// <summary>
    /// Section 15's rules. Defaults to the repository's <c>.charter/policies/migrations.yml</c>, or
    /// the shipped rules when it has none.
    /// </summary>
    public MigrationPolicy? MigrationPolicy { get; init; }

    /// <summary>
    /// Section 8's named validation commands. Null reads them from the checkout's
    /// <c>.charter/config.yml</c>, which is where they live and what every backend gets.
    /// </summary>
    public IReadOnlyList<ShimCheck>? Checks { get; init; }

    /// <summary>
    /// Environment for the agent process — the credential variables the adapter's <c>auth</c> block
    /// names, and nothing else. The runner never sees the control plane's environment (section 7.4).
    /// </summary>
    public IReadOnlyDictionary<string, string> AgentEnvironment { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// The session execution shim of section 32.1 — what runs <em>inside</em> the sandbox, and what all
/// three backends invoke.
/// </summary>
/// <remarks>
/// <para>
/// The order of the steps is the security model, not a convenience. Toolchain verification comes
/// first because a session that cannot run must not have installed anything (sections 16.1, 32.1).
/// Dependency installs come next, lockfile-only and with install scripts off (section 16.2). Only
/// then does the agent start, and every write it reports is checked against the path scope before the
/// event is published (section 7.3).
/// </para>
/// <para>
/// A scope violation ends the run. It is not filtered, not warned about, and not left to the reviewer
/// to notice: the agent asked to write somewhere it was told it could not, and the only safe reading
/// of that is that the session should stop.
/// </para>
/// </remarks>
public sealed class ShimSession
{
    private readonly IAdapterCatalog _adapters;
    private readonly IShimProcessRunner _processes;
    private readonly IShimEventSink _sink;
    private readonly IShimSpecSource _specs;

    public ShimSession(
        IAdapterCatalog adapters,
        IShimProcessRunner processes,
        IShimEventSink sink,
        IShimSpecSource specs)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(specs);

        _adapters = adapters;
        _processes = processes;
        _sink = sink;
        _specs = specs;
    }

    public async Task<ShimResult> RunAsync(ShimRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var adapter = _adapters.Get(request.AdapterId);
        var scope = ShimPathScope.Create(request.WorkspaceRoot, request.AllowPaths, request.DenyPaths);
        var translator = new ShimEventTranslator(new AdapterEventClassifier(adapter), scope);

        try
        {
            var result = await ExecuteAsync(request, adapter, scope, translator, cancellationToken);
            await _sink.ReportResultAsync(result, CancellationToken.None);
            return result;
        }
        catch (OperationCanceledException)
        {
            // Section 11: cancel must actually kill the runner, and the control plane must be told,
            // so the terminal report goes out on an uncancelled token.
            var cancelled = new ShimResult(ShimSessionState.Cancelled, "The session was cancelled.");
            await _sink.ReportResultAsync(cancelled, CancellationToken.None);
            return cancelled;
        }
    }

    private async Task<ShimResult> ExecuteAsync(
        ShimRunRequest request,
        AdapterDocument adapter,
        ShimPathScope scope,
        ShimEventTranslator translator,
        CancellationToken cancellationToken)
    {
        // 0. A checkout to work in. A no-op for every backend that already cloned one, which is two
        // of the three; a container that starts empty gets one here.
        if (request.Publish
            && await new ShimCheckout(_processes).EnsureAsync(
                request.WorkspaceRoot,
                request.CloneUrl,
                request.BaseCommit,
                request.GitToken,
                cancellationToken) is { } problem)
        {
            await PublishAsync(translator, EventTypes.Error, new JsonObject
            {
                ["reason"] = "checkout_failed",
                ["message"] = problem,
            }, cancellationToken);

            return new ShimResult(ShimSessionState.PublishFailed, problem);
        }

        // 1. Section 32.1: never install a language runtime. Fail fast, actionably, first.
        var toolchain = ShimToolchain.Verify(
            request.RequiredCapabilities,
            request.ProbedCapabilities,
            request.RunnerImage);

        if (!toolchain.Satisfied)
        {
            await PublishAsync(translator, EventTypes.Error, new JsonObject
            {
                ["reason"] = "toolchain_missing",
                ["missing"] = ToArray(toolchain.Missing),
                ["message"] = toolchain.Message,
            }, cancellationToken);

            return new ShimResult(ShimSessionState.ToolchainMissing, toolchain.Message);
        }

        // 1b. The repository's own checks (section 8), and whether this image can run them at all.
        // Read here rather than after the agent because the answer to "this image has no .NET" must
        // arrive before a model spends an hour producing work that could never have been validated.
        var checkWarnings = new List<string>();
        var checks = request.Checks ?? ShimChecks.Load(request.WorkspaceRoot, checkWarnings);

        foreach (var warning in checkWarnings)
        {
            await PublishAsync(translator, EventTypes.Error, new JsonObject
            {
                ["reason"] = "charter_config_warning",
                ["message"] = warning,
            }, cancellationToken);
        }

        var checkToolchains = ShimChecks.VerifyToolchains(checks, request.ProbedCapabilities, request.RunnerImage);

        if (!checkToolchains.Satisfied)
        {
            await PublishAsync(translator, EventTypes.Error, new JsonObject
            {
                ["reason"] = "toolchain_missing",
                ["stage"] = "checks",
                ["missing"] = ToArray(checkToolchains.Missing),
                ["message"] = checkToolchains.Message,
            }, cancellationToken);

            return new ShimResult(ShimSessionState.ToolchainMissing, checkToolchains.Message);
        }

        // 2. Section 16.2: lockfile-only, install scripts off unless this repository opted in.
        var plan = ShimDependencyInstalls.Plan(request.WorkspaceRoot, request.AllowInstallScripts);

        foreach (var warning in plan.Warnings)
        {
            await PublishAsync(translator, EventTypes.Error, new JsonObject
            {
                ["reason"] = "dependency_warning",
                ["message"] = warning,
            }, cancellationToken);
        }

        foreach (var step in plan.Steps)
        {
            await PublishAsync(translator, EventTypes.Command, new JsonObject
            {
                ["ecosystem"] = step.Ecosystem,
                ["command"] = step.Display,
                ["lockfile"] = step.Lockfile,
                ["lockfile_enforced"] = step.LockfileEnforced,
                ["install_scripts_disabled"] = step.InstallScriptsDisabled,
            }, cancellationToken);

            var install = await _processes.RunAsync(
                new ShimProcessRequest(step.Command, step.Arguments, request.WorkspaceRoot),
                null,
                cancellationToken);

            if (install.ExitCode != 0)
            {
                var message = $"'{step.Display}' failed with exit code {install.ExitCode}. "
                    + (install.Error ?? "The dependency install did not complete.");

                await PublishAsync(translator, EventTypes.Error, new JsonObject
                {
                    ["reason"] = "install_failed",
                    ["command"] = step.Display,
                    ["exit_code"] = install.ExitCode,
                }, cancellationToken);

                return new ShimResult(ShimSessionState.InstallFailed, message, install.ExitCode);
            }
        }

        // 3. The agent sees the refined, approved spec and nothing the requester typed (section 16).
        var prompt = await _specs.LoadAsync(request.SpecUrl, cancellationToken);
        // Charter's canonical identifier goes in whole: the adapter's `model_format` decides whether
        // this CLI wants anthropic/claude-opus-5 or claude-opus-5 (section 12b). Stripping the
        // provider here instead would silently break every adapter that wants the qualified form.
        var invocation = adapter.BuildInvocation(prompt, request.Model);

        await PublishAsync(translator, EventTypes.SessionStarted, new JsonObject
        {
            ["adapter"] = adapter.Id,
            ["model"] = request.Model,
            ["structured_events"] = adapter.IsStructured,
        }, cancellationToken);

        // 4. Stream. A scope violation or a destructive migration stops the run, so the agent process
        // is cancelled from here.
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        PathScopeDecision? violation = null;
        ShimMigrationFinding? halting = null;

        var migrationWarnings = new List<string>();
        var migrations = new ShimMigrationGuard(
            request.WorkspaceRoot,
            request.MigrationPolicy ?? SchemaChanges.MigrationPolicy.Load(request.WorkspaceRoot, migrationWarnings));

        var agent = await _processes.RunAsync(
            new ShimProcessRequest(
                invocation.Arguments[0],
                [.. invocation.Arguments.Skip(1)],
                request.WorkspaceRoot,
                invocation.StandardInput,
                request.AgentEnvironment),
            async (line, token) =>
            {
                var outcome = translator.Translate(line);

                if (outcome.Violation is not null)
                {
                    violation ??= outcome.Violation;
                    await run.CancelAsync();
                    return;
                }

                if (outcome.Event is not null)
                {
                    await _sink.PublishAsync(outcome.Event, token);
                }

                foreach (var path in outcome.Paths ?? [])
                {
                    // Section 15: classify structurally, and stop the run on a destructive migration
                    // rather than letting a bad one reach review.
                    if (migrations.Inspect(path) is { } destructive)
                    {
                        halting ??= destructive;
                        await run.CancelAsync();
                        return;
                    }
                }
            },
            run.Token);

        foreach (var finding in migrations.Findings)
        {
            await PublishAsync(translator, EventTypes.CheckResult, new JsonObject
            {
                ["check"] = "migration_classification",
                ["path"] = finding.Path,
                ["class"] = finding.Classification.Class.ToString().ToLowerInvariant(),
                ["outcome"] = finding.Classification.Outcome.ToString(),
                ["label"] = MigrationClassification.SchemaChangeLabel,
                ["summary"] = finding.Classification.Summary,
            }, CancellationToken.None);
        }

        if (halting is not null)
        {
            await PublishAsync(translator, EventTypes.Error, new JsonObject
            {
                ["reason"] = "destructive_migration",
                ["path"] = halting.Path,
                ["message"] = halting.Classification.Summary,
            }, CancellationToken.None);

            return new ShimResult(
                ShimSessionState.DestructiveMigration,
                halting.Classification.Summary,
                agent.ExitCode);
        }

        if (violation is not null)
        {
            await PublishAsync(translator, EventTypes.Error, new JsonObject
            {
                ["reason"] = "path_scope_violation",
                ["path"] = violation.Path,
                ["refusal"] = violation.Refusal.ToString(),
                ["pattern"] = violation.Pattern,
                ["message"] = violation.Explanation,
            }, CancellationToken.None);

            return new ShimResult(ShimSessionState.ScopeViolation, violation.Explanation, agent.ExitCode);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (agent.ExitCode != 0)
        {
            return new ShimResult(
                ShimSessionState.Failed,
                agent.Error ?? $"The agent exited with code {agent.ExitCode}.",
                agent.ExitCode);
        }

        // 5. Section 8: the repository's own checks, against the work the agent just did. A failing
        // one is reported and does not stop the push — see ShimCheckRunner for why.
        var checkOutcomes = await RunChecksAsync(request, checks, translator, cancellationToken);

        // 6. Commit, push, and say so. Without this step nothing the agent did ever leaves the
        // sandbox: ChangeRequestPublisher reads `branch_pushed` and there is no other producer of it.
        var published = request.Publish
            ? await CommitAndPushAsync(request, scope, translator, prompt, cancellationToken)
            : null;

        if (published is { Outcome: ShimPublishOutcome.ScopeViolation })
        {
            return new ShimResult(ShimSessionState.ScopeViolation, published.Message, agent.ExitCode);
        }

        if (published is { Outcome: ShimPublishOutcome.Failed })
        {
            return new ShimResult(ShimSessionState.PublishFailed, published.Message);
        }

        await PublishAsync(translator, EventTypes.SessionEnded, new JsonObject
        {
            ["state"] = "completed",
            ["malformed_lines"] = translator.MalformedLines,
            ["published"] = published is { Outcome: ShimPublishOutcome.Published },

            // Section 14 needs both numbers: what was verified, and what could not be. A session that
            // completed with a red check completed — and the recap must be able to say so.
            ["checks_passed"] = checkOutcomes.Count(outcome => outcome.Passed),
            ["checks_failed"] = checkOutcomes.Count(outcome => !outcome.Passed),
        }, CancellationToken.None);

        return new ShimResult(ShimSessionState.Completed, published?.Message);
    }

    /// <summary>
    /// Runs the repository's checks and puts each outcome on the transcript.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Skipped when the agent changed nothing, because there is then nothing to validate and a full
    /// test run against an untouched tree is minutes of a requester's time spent proving something
    /// nobody asked. Section 6's <c>NoChangesNeeded</c> is the outcome in that case, and it is not
    /// improved by a green build.
    /// </para>
    /// <para>
    /// The event is <see cref="EventTypes.CheckResult"/> with a <c>passed</c> boolean, which is the
    /// shape the transcript already reads to colour a row and the change request already reads to
    /// decide what to say about the change (sections 11, 12).
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<ShimCheckOutcome>> RunChecksAsync(
        ShimRunRequest request,
        IReadOnlyList<ShimCheck> checks,
        ShimEventTranslator translator,
        CancellationToken cancellationToken)
    {
        if (checks.Count == 0 || (request.Publish && !await HasChangesAsync(request, cancellationToken)))
        {
            return [];
        }

        return await new ShimCheckRunner(_processes).RunAsync(
            checks,
            request.WorkspaceRoot,
            async (outcome, token) => await PublishAsync(translator, EventTypes.CheckResult, new JsonObject
            {
                ["check"] = outcome.Check.Name,
                ["command"] = outcome.Check.Display,
                ["passed"] = outcome.Passed,
                ["status"] = outcome.Status.ToString().ToLowerInvariant(),
                ["exit_code"] = outcome.ExitCode,
                ["duration_ms"] = outcome.DurationMs,
                ["summary"] = outcome.Summary,
                ["output"] = outcome.Detail,
            }, token),
            cancellationToken);
    }

    /// <summary>True when there is anything in the workspace worth validating.</summary>
    private async Task<bool> HasChangesAsync(ShimRunRequest request, CancellationToken cancellationToken)
    {
        var status = await new ShimGit(_processes, request.WorkspaceRoot)
            .RunAsync(["status", "--porcelain"], null, cancellationToken);

        // A workspace git cannot read is not evidence that nothing changed, so the checks run.
        return !status.Succeeded || status.Output.Trim().Length > 0;
    }

    /// <summary>
    /// Publishes the session's work and reports it in the shape the control plane already reads.
    /// </summary>
    /// <remarks>
    /// The three outcomes reach the transcript as three different things, because they mean three
    /// different things to whoever reads it. Work published becomes
    /// <see cref="ChangeRequestEventTypes.BranchPushed"/>, which is the event
    /// <see cref="ChangeRequestPublisher"/> opens a change request off. Nothing to commit becomes
    /// <see cref="ChangeRequestEventTypes.NoChanges"/>, section 6's <c>NoChangesNeeded</c>. A git
    /// failure becomes an error and stops the session, so a broken credential never reads to a
    /// requester as an agent that decided there was nothing to do.
    /// </remarks>
    private async Task<ShimPublishResult> CommitAndPushAsync(
        ShimRunRequest request,
        ShimPathScope scope,
        ShimEventTranslator translator,
        string spec,
        CancellationToken cancellationToken)
    {
        var publisher = new ShimPublisher(_processes, scope);

        var result = await publisher.PublishAsync(
            new ShimPublishRequest
            {
                WorkspaceRoot = request.WorkspaceRoot,
                Branch = request.BranchOrConvention,
                Remote = request.Remote,
                Author = request.Requester,
                Message = ShimCommitMessage.Build(spec),
            },
            cancellationToken);

        switch (result.Outcome)
        {
            case ShimPublishOutcome.Published:
                await PublishAsync(translator, ChangeRequestEventTypes.BranchPushed, new JsonObject
                {
                    ["branch"] = result.Branch,
                    ["revision"] = result.Revision,
                    ["remote"] = request.Remote,
                    ["files"] = ToArray(result.Paths),
                    ["message"] = result.Message,
                }, CancellationToken.None);
                break;

            case ShimPublishOutcome.NoChanges:
                await PublishAsync(translator, ChangeRequestEventTypes.NoChanges, new JsonObject
                {
                    ["reason"] = "nothing_to_commit",
                    ["message"] = result.Message,
                }, CancellationToken.None);
                break;

            case ShimPublishOutcome.ScopeViolation:
                await PublishAsync(translator, EventTypes.Error, new JsonObject
                {
                    ["reason"] = "path_scope_violation",
                    ["stage"] = "commit",
                    ["path"] = result.Violation?.Path,
                    ["refusal"] = result.Violation?.Refusal.ToString(),
                    ["pattern"] = result.Violation?.Pattern,
                    ["message"] = result.Message,
                }, CancellationToken.None);
                break;

            default:
                await PublishAsync(translator, EventTypes.Error, new JsonObject
                {
                    ["reason"] = "publish_failed",
                    ["branch"] = request.BranchOrConvention,
                    ["message"] = result.Message,
                }, CancellationToken.None);
                break;
        }

        return result;
    }

    private async ValueTask PublishAsync(
        ShimEventTranslator translator,
        string type,
        JsonObject payload,
        CancellationToken cancellationToken)
        => await _sink.PublishAsync(translator.Synthesize(type, payload), cancellationToken);

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
