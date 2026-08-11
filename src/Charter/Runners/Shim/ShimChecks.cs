using System.Text;
using Charter.Onboarding;
using Charter.Runners.SchemaChanges;

namespace Charter.Runners.Shim;

/// <summary>How one of the repository's checks turned out.</summary>
public enum ShimCheckStatus
{
    /// <summary>Exit code zero.</summary>
    Passed,

    /// <summary>The command ran and refused. Reported, not fatal — see <see cref="ShimCheckRunner"/>.</summary>
    Failed,

    /// <summary>
    /// The check never ran: the command could not be started, or Charter refused to run it as written.
    /// </summary>
    /// <remarks>
    /// Kept distinct from <see cref="Failed"/> because they say opposite things to a reviewer. A failed
    /// check is evidence about the change; a check that never ran is the absence of evidence, and
    /// section 14's recap has a section for exactly that — <em>what it could not verify</em>.
    /// </remarks>
    NotRun,
}

/// <summary>
/// One named validation command from <c>.charter/config.yml</c> (section 8), resolved to something
/// that can actually be started.
/// </summary>
/// <param name="Name">The check's name, as the transcript and the recap refer to it.</param>
/// <param name="Run">The command line exactly as the repository wrote it.</param>
/// <param name="Command">The executable.</param>
/// <param name="Arguments">The argument vector. No shell is involved, so nothing here is quoted.</param>
/// <param name="Toolchain">
/// The capability token this check needs, when Charter can tell — <c>dotnet</c> for
/// <c>dotnet build</c>, <c>node</c> for <c>npm test</c>. Null when the executable is not a language
/// runtime Charter probes for, which is most of them.
/// </param>
/// <param name="Problem">
/// Why this check cannot be run as written, or null when it can. Set rather than thrown: one
/// unrunnable check must not stop the other three from running.
/// </param>
public sealed record ShimCheck(
    string Name,
    string Run,
    string Command,
    IReadOnlyList<string> Arguments,
    string? Toolchain,
    string? Problem)
{
    /// <summary>True when there is a command to start.</summary>
    public bool Runnable => Problem is null && Command.Length > 0;

    /// <summary>How the check reads in a transcript event.</summary>
    public string Display => Run;
}

/// <summary>The result of running one check.</summary>
/// <param name="Check">Which check.</param>
/// <param name="Status">How it went.</param>
/// <param name="ExitCode">The process's exit code, or -1 when it never started.</param>
/// <param name="DurationMs">Elapsed milliseconds. Never an estimate of anything (section 6).</param>
/// <param name="Detail">
/// The tail of what the command said, truncated, or the reason it did not run. What makes a failing
/// check actionable rather than a bare exit code.
/// </param>
public sealed record ShimCheckOutcome(
    ShimCheck Check,
    ShimCheckStatus Status,
    int ExitCode,
    long DurationMs,
    string? Detail)
{
    public bool Passed => Status == ShimCheckStatus.Passed;

    /// <summary>One line, safe to put on a change request or in front of an engineer.</summary>
    public string Summary => Status switch
    {
        ShimCheckStatus.Passed => $"The check '{Check.Name}' passed.",
        ShimCheckStatus.Failed => $"The check '{Check.Name}' failed: '{Check.Display}' exited with {ExitCode}.",
        _ => $"The check '{Check.Name}' did not run. {Detail}".TrimEnd(),
    };
}

/// <summary>
/// The repository's own checks (section 8), read out of <c>.charter/config.yml</c>.
/// </summary>
/// <remarks>
/// <para>
/// Read in the sandbox from the checkout rather than plumbed through the dispatch payload, for the
/// same reason <see cref="MigrationPolicy"/> is: the guardrails are committed to the repository, so
/// the copy that governs a session is the one on the commit the session is working from. A dispatch
/// that carried them would be a second, staler source of the same truth.
/// </para>
/// <para>
/// <strong>No shell.</strong> A check is one command with an argument vector, started directly. That
/// is a deliberate limitation and it is reported as one: a <c>run:</c> containing <c>&amp;&amp;</c>,
/// a pipe or a redirect becomes an unrunnable check with a message telling the author to split it in
/// two or move it into a script under <c>.charter/checks/</c>. Interpolating repository-authored
/// strings into a shell would put quoting bugs and the session's own environment one typo apart, and
/// section 8's <c>checks/</c> folder already exists for anything that genuinely needs a script.
/// </para>
/// </remarks>
public static class ShimChecks
{
    /// <summary>Where the checks are declared, relative to the workspace root.</summary>
    public const string ConfigPath = ".charter/config.yml";

    /// <summary>The most output kept per check, in characters.</summary>
    public const int MaxDetailLength = 2000;

    /// <summary>
    /// Executables whose absence Charter can actually detect, because
    /// <see cref="ShimCapabilityProbe"/> probes for them.
    /// </summary>
    /// <remarks>
    /// Deliberately short. A capability nothing probes for is a capability every session would be
    /// judged to be missing, so gating on one would fail every repository whose checks run
    /// <c>make</c> — the exact false alarm that teaches an operator to distrust the message.
    /// </remarks>
    private static readonly Dictionary<string, string> ToolchainsByExecutable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dotnet"] = "dotnet",
        ["node"] = "node",
        ["npm"] = "node",
        ["npx"] = "node",
        ["pnpm"] = "node",
        ["yarn"] = "node",
        ["python"] = "python",
        ["python3"] = "python",
        ["pytest"] = "python",
        ["uv"] = "uv",
        ["git"] = "git",
        ["xcodebuild"] = "xcode",
    };

    private static readonly string[] ShellOperators = ["&&", "||", "|", ";", ">", "<", "$(", "`", "&", "\n"];

    /// <summary>Reads the repository's checks. Answers an empty list when it declares none.</summary>
    /// <param name="workspaceRoot">The session checkout.</param>
    /// <param name="warnings">
    /// Collects anything the parser did not recognise. Section 8: unknown keys warn, never fail.
    /// </param>
    /// <param name="readFile">How the config is read. Injected so a test needs no directory.</param>
    public static IReadOnlyList<ShimCheck> Load(
        string workspaceRoot,
        ICollection<string>? warnings = null,
        Func<string, string?>? readFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        var read = readFile ?? ReadIfPresent;
        var yaml = read(Path.Combine(workspaceRoot, ConfigPath));

        if (string.IsNullOrWhiteSpace(yaml))
        {
            return [];
        }

        // The same parser the onboarding surface uses, so a repository cannot have its checks read
        // one way in the wizard and another way in the sandbox.
        var configWarnings = new List<string>();
        var config = CharterConfigDocument.Parse(yaml, configWarnings);

        foreach (var warning in configWarnings)
        {
            warnings?.Add(warning);
        }

        return [.. config.Checks.Select(check => Resolve(check.Name, check.Run))];
    }

    /// <summary>Resolves one declared check into a command, or into a reason it cannot be run.</summary>
    public static ShimCheck Resolve(string name, string run)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(run);

        var declared = run.Trim();

        foreach (var operatorText in ShellOperators)
        {
            if (!declared.Contains(operatorText, StringComparison.Ordinal))
            {
                continue;
            }

            return new ShimCheck(
                name,
                declared,
                string.Empty,
                [],
                null,
                $"Charter runs each check as a single command with no shell, and '{name}' uses "
                + $"'{operatorText}'. Split it into separate checks, or put it in a script under "
                + ".charter/checks/ and point run: at that.");
        }

        var tokens = Tokenize(declared);

        if (tokens.Count == 0)
        {
            return new ShimCheck(name, declared, string.Empty, [], null, $"The check '{name}' has no command to run.");
        }

        ToolchainsByExecutable.TryGetValue(Path.GetFileNameWithoutExtension(tokens[0]), out var toolchain);

        return new ShimCheck(name, declared, tokens[0], [.. tokens.Skip(1)], toolchain, null);
    }

    /// <summary>
    /// Section 32.1, applied to the checks: the image either has what they need or the session stops.
    /// </summary>
    /// <remarks>
    /// Run before the agent starts, not after. The rule is that a session never installs a language
    /// runtime, and the useful moment to discover that a repository's <c>dotnet build</c> cannot run
    /// is before a model has spent an hour and a budget producing work nobody can validate.
    /// </remarks>
    public static ShimToolchainVerdict VerifyToolchains(
        IReadOnlyList<ShimCheck> checks,
        IReadOnlyList<string> probed,
        string? image = null)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(probed);

        var advertised = new HashSet<string>(RunnerCapability.ExpandAll(probed), StringComparer.Ordinal);
        var missing = new List<string>();
        var reasons = new List<string>();

        foreach (var check in checks)
        {
            if (check.Toolchain is not { Length: > 0 } toolchain
                || advertised.Contains(toolchain)
                || missing.Contains(toolchain, StringComparer.Ordinal))
            {
                continue;
            }

            missing.Add(toolchain);
            reasons.Add($"the check '{check.Name}' runs '{check.Display}', which needs {RunnerCapability.Describe(toolchain)}");
        }

        if (missing.Count == 0)
        {
            return new ShimToolchainVerdict(true, [], null);
        }

        var used = string.IsNullOrWhiteSpace(image) ? "this runner image" : $"the image '{image}'";

        return new ShimToolchainVerdict(
            false,
            missing,
            $"This repository's checks cannot run here: {Join(reasons)}, and {used} does not provide it. "
            + "A session never installs a language runtime, so it stops here rather than changing the "
            + "image underneath itself. Set runner_image in .charter/config.yml to an image that has it, "
            + "or build one from the documented Dockerfiles in runners/.");
    }

    /// <summary>Splits a command line on whitespace, respecting single and double quotes.</summary>
    internal static IReadOnlyList<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        var open = false;

        foreach (var character in commandLine)
        {
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(character);
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                open = true;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (current.Length > 0 || open)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    open = false;
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0 || open)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static string Join(IReadOnlyList<string> reasons) => reasons.Count switch
    {
        0 => string.Empty,
        1 => reasons[0],
        _ => $"{string.Join(", ", reasons.Take(reasons.Count - 1))} and {reasons[^1]}",
    };

    private static string? ReadIfPresent(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// Runs the repository's checks after the agent has exited, and reports each one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A failing check reports; it does not block the push.</strong> That is a decision, and this
/// is the reasoning. Charter has no merge button (section 7.4) — a change request with a red check
/// cannot ship, because the merge gate is branch protection and CODEOWNERS on the provider, outside
/// Charter entirely. So the question is not whether broken work can merge; it is whether a human ever
/// gets to see it. Discarding a session's work because <c>dotnet test</c> came back non-zero throws
/// away everything that was spent producing it and leaves an engineer with nothing to read, no branch
/// to take over (section 7.5) and no evidence about what went wrong. It also makes Charter unusable
/// against any repository whose main branch is already red, or that has one flaky test.
/// </para>
/// <para>
/// So the branch is pushed, the change request opens, and every check's outcome is on the transcript
/// and in the change request body where a reviewer reads it first. This is exactly what section 14's
/// recap needs for its <em>what it could not verify</em> section, which cannot exist if a session that
/// failed to verify itself never reaches review.
/// </para>
/// <para>
/// The one thing that <em>does</em> stop a session is a check that cannot run at all for want of a
/// toolchain, and that is caught before the agent starts rather than here (section 32.1).
/// </para>
/// </remarks>
public sealed class ShimCheckRunner
{
    private readonly IShimProcessRunner _processes;
    private readonly TimeProvider _clock;

    public ShimCheckRunner(IShimProcessRunner processes, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(processes);

        _processes = processes;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Runs every check in declaration order.</summary>
    /// <param name="checks">What the repository declared.</param>
    /// <param name="workspaceRoot">Where to run them.</param>
    /// <param name="onOutcome">Called with each outcome as it lands, so the transcript streams.</param>
    public async Task<IReadOnlyList<ShimCheckOutcome>> RunAsync(
        IReadOnlyList<ShimCheck> checks,
        string workspaceRoot,
        Func<ShimCheckOutcome, CancellationToken, ValueTask>? onOutcome = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        var outcomes = new List<ShimCheckOutcome>(checks.Count);

        foreach (var check in checks)
        {
            var outcome = await RunOneAsync(check, workspaceRoot, cancellationToken);
            outcomes.Add(outcome);

            if (onOutcome is not null)
            {
                await onOutcome(outcome, cancellationToken);
            }
        }

        return outcomes;
    }

    private async Task<ShimCheckOutcome> RunOneAsync(
        ShimCheck check,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        if (!check.Runnable)
        {
            return new ShimCheckOutcome(check, ShimCheckStatus.NotRun, -1, 0, check.Problem);
        }

        var started = _clock.GetTimestamp();
        var output = new List<string>();

        try
        {
            var result = await _processes.RunAsync(
                new ShimProcessRequest(check.Command, check.Arguments, workspaceRoot),
                (line, _) =>
                {
                    output.Add(line);
                    return ValueTask.CompletedTask;
                },
                cancellationToken);

            var elapsed = (long)_clock.GetElapsedTime(started).TotalMilliseconds;
            var detail = Tail(result.Error is { Length: > 0 } error ? error : string.Join('\n', output));

            return new ShimCheckOutcome(
                check,
                result.ExitCode == 0 ? ShimCheckStatus.Passed : ShimCheckStatus.Failed,
                result.ExitCode,
                elapsed,
                detail);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The executable is not there, or the host refused to start it. Not the same thing as a
            // check that ran and said no, and not a reason to lose the session's work either.
            return new ShimCheckOutcome(
                check,
                ShimCheckStatus.NotRun,
                -1,
                (long)_clock.GetElapsedTime(started).TotalMilliseconds,
                $"Charter could not start '{check.Command}'. {exception.Message}");
        }
    }

    /// <summary>The end of the output, which is where a build tool puts the reason.</summary>
    private static string? Tail(string text)
    {
        var trimmed = text.Trim();

        if (trimmed.Length == 0)
        {
            return null;
        }

        return trimmed.Length <= ShimChecks.MaxDetailLength
            ? trimmed
            : "…" + trimmed[^ShimChecks.MaxDetailLength..];
    }
}
