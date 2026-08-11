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
/// What a failing check costs: a refusal to boot, or a warning an operator should read.
/// </summary>
/// <remarks>
/// <para>
/// Section 30.1 says never boot into a half-working state, and section 4.1 says fail fast and loud.
/// Neither means every observation is fatal. The distinction drawn here is <em>whether the failure
/// is proof of a broken instance</em>:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="Blocking"/> — an unreachable database, an unmigrated schema, a weak or duplicated key,
/// or no model credential anywhere. Every one of these is observed from inside the container against
/// the resource itself, so a failure is conclusive, and nothing Charter does works without it.
/// </description></item>
/// <item><description>
/// <see cref="Advisory"/> — an observation made from the wrong vantage point to be conclusive. The
/// public base URL is resolved by GitHub and by browsers, not by this container: split-horizon DNS,
/// a private PaaS network, or a DNS record that lands minutes after the first deploy all make the
/// in-container lookup fail on an instance that is completely healthy. Refusing to boot on that
/// evidence breaks working deployments, so it is logged loudly and the boot continues.
/// </description></item>
/// </list>
/// </remarks>
public enum PreflightSeverity
{
    /// <summary>A failure here stops the boot with a non-zero exit.</summary>
    Blocking,

    /// <summary>A failure here is logged as a warning and the boot continues.</summary>
    Advisory,
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
    /// <summary>
    /// What this result costs the boot. Stamped by <see cref="PreflightRunner"/> from the check that
    /// produced it, so a check never has to remember to carry its own severity into every result.
    /// </summary>
    public PreflightSeverity Severity { get; init; } = PreflightSeverity.Blocking;

    /// <summary>True when this result must stop the process from serving traffic.</summary>
    public bool IsBlockingFailure
        => Status == PreflightStatus.Failed && Severity == PreflightSeverity.Blocking;

    /// <summary>True when this result is a failure the operator should read but may boot through.</summary>
    public bool IsAdvisoryFailure
        => Status == PreflightStatus.Failed && Severity == PreflightSeverity.Advisory;

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
            PreflightStatus.Failed when Severity == PreflightSeverity.Advisory => "WARN",
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

    /// <summary>
    /// What a failure of this check costs the boot. Blocking unless a check says otherwise, because
    /// the safe default for a first-run check is to refuse rather than to serve a broken instance.
    /// </summary>
    PreflightSeverity Severity => PreflightSeverity.Blocking;

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

    /// <inheritdoc />
    public virtual PreflightSeverity Severity => PreflightSeverity.Blocking;

    /// <summary>Runs the check synchronously.</summary>
    public abstract PreflightResult Run();

    /// <inheritdoc />
    public ValueTask<PreflightResult> RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Run());
    }
}
