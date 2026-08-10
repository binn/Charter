namespace Charter.Configuration.Preflight;

/// <summary>Outcome of a single preflight check (section 30.1).</summary>
public enum PreflightStatus
{
    /// <summary>The check ran and the instance is fine on this point.</summary>
    Passed,

    /// <summary>The check ran and found a problem. Never boot into a half-working state.</summary>
    Failed,

    /// <summary>The check does not apply to this configuration, and says why.</summary>
    Skipped,
}

/// <summary>
/// A named pass/fail result with remediation an operator can act on.
/// </summary>
/// <param name="Name">Short check name, shown in the first-run results list.</param>
/// <param name="Status">Whether the check passed.</param>
/// <param name="Detail">What was observed, in one sentence.</param>
/// <param name="Remediation">
/// What to change to make a failing check pass. Required on failure - section 30.1: say which check
/// failed and what to change.
/// </param>
public sealed record PreflightResult(
    string Name,
    PreflightStatus Status,
    string Detail,
    string? Remediation = null)
{
    /// <summary>A passing result.</summary>
    public static PreflightResult Pass(string name, string detail)
        => new(name, PreflightStatus.Passed, detail);

    /// <summary>A failing result, with the change that fixes it.</summary>
    public static PreflightResult Fail(string name, string detail, string remediation)
        => new(name, PreflightStatus.Failed, detail, remediation);

    /// <summary>A check that does not apply, and why.</summary>
    public static PreflightResult Skip(string name, string detail)
        => new(name, PreflightStatus.Skipped, detail);

    /// <summary>One line suitable for stdout on first run.</summary>
    public string Describe()
    {
        var marker = Status switch
        {
            PreflightStatus.Passed => "PASS",
            PreflightStatus.Failed => "FAIL",
            _ => "SKIP",
        };

        return Remediation is null
            ? $"[{marker}] {Name}: {Detail}"
            : $"[{marker}] {Name}: {Detail} -> {Remediation}";
    }
}

/// <summary>
/// One first-run check (section 30.1): database reachable, migrations applied, base URL resolves,
/// a model credential is valid, keys are long enough.
/// </summary>
/// <remarks>
/// <see cref="RequiresIo"/> separates checks that talk to the network or the database from those
/// that only read the parsed config. The config parser itself never makes a network call - it cannot,
/// or a DNS timeout would become a startup hang - so everything that needs I/O lives behind this
/// interface and can be run, deferred or skipped independently.
/// </remarks>
public interface IPreflightCheck
{
    /// <summary>Short name shown to the operator.</summary>
    string Name { get; }

    /// <summary>True when running this check touches the network, the database, or the disk.</summary>
    bool RequiresIo { get; }

    /// <summary>Runs the check. Implementations report failure rather than throwing.</summary>
    ValueTask<PreflightResult> RunAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A check over the parsed configuration alone: no network, no database, no clock skew.
/// </summary>
public abstract class PurePreflightCheck : IPreflightCheck
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public bool RequiresIo => false;

    /// <summary>Runs the check synchronously.</summary>
    public abstract PreflightResult Run();

    /// <inheritdoc />
    public ValueTask<PreflightResult> RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Run());
    }
}
