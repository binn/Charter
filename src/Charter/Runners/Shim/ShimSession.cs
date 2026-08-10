using System.Text.Json.Nodes;
using Charter.Adapters;
using Charter.Domain;
using Charter.Runners.SchemaChanges;

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
    /// Section 15's rules. Defaults to the repository's <c>.charter/policies/migrations.yml</c>, or
    /// the shipped rules when it has none.
    /// </summary>
    public MigrationPolicy? MigrationPolicy { get; init; }

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
        var model = ModelIdentifier.Parse(request.Model);
        var invocation = adapter.BuildInvocation(prompt, model.Name);

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

        await PublishAsync(translator, EventTypes.SessionEnded, new JsonObject
        {
            ["state"] = "completed",
            ["malformed_lines"] = translator.MalformedLines,
        }, CancellationToken.None);

        return new ShimResult(ShimSessionState.Completed);
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
