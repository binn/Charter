namespace Charter.Configuration.Preflight;

/// <summary>Which checks to run.</summary>
public enum PreflightScope
{
    /// <summary>Only checks over the parsed configuration. Never touches the network.</summary>
    PureOnly,

    /// <summary>Every check, including those that open a connection or resolve a name.</summary>
    All,
}

/// <summary>The results of a preflight pass (section 30.1).</summary>
/// <param name="Results">One result per check, in registration order.</param>
public sealed record PreflightReport(IReadOnlyList<PreflightResult> Results)
{
    /// <summary>True when nothing failed.</summary>
    public bool Passed => Results.All(result => result.Status != PreflightStatus.Failed);

    /// <summary>The failures, which are what an operator needs to read.</summary>
    public IReadOnlyList<PreflightResult> Failures
        => [.. Results.Where(result => result.Status == PreflightStatus.Failed)];

    /// <summary>
    /// The failures that must stop the boot (section 30.1: never boot into a half-working state).
    /// </summary>
    public IReadOnlyList<PreflightResult> BlockingFailures
        => [.. Results.Where(result => result.IsBlockingFailure)];

    /// <summary>
    /// The failures an operator should read but can boot through - see <see cref="PreflightSeverity"/>
    /// for why the base URL is one of these and the database is not.
    /// </summary>
    public IReadOnlyList<PreflightResult> Advisories
        => [.. Results.Where(result => result.IsAdvisoryFailure)];

    /// <summary>True when at least one blocking check failed, so the process must not serve.</summary>
    public bool ShouldHalt => BlockingFailures.Count > 0;

    /// <summary>The whole report, one line per check, for stdout on first run.</summary>
    public string Describe()
        => string.Join(Environment.NewLine, Results.Select(result => result.Describe()));
}

/// <summary>
/// Runs the first-run checks of section 30.1 and collects their results.
/// </summary>
/// <remarks>
/// A check that throws becomes a failed result rather than an unhandled exception: preflight exists
/// to explain what is wrong, so it must survive a check that is itself broken, and it must run every
/// check before reporting - the same "all problems at once" rule as configuration validation.
/// </remarks>
public sealed class PreflightRunner
{
    private readonly IReadOnlyList<IPreflightCheck> checks;

    /// <summary>Creates a runner over <paramref name="checks"/>.</summary>
    public PreflightRunner(IEnumerable<IPreflightCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        this.checks = [.. checks];
    }

    /// <summary>Runs the checks in <paramref name="scope"/>.</summary>
    public async Task<PreflightReport> RunAsync(
        PreflightScope scope = PreflightScope.All,
        CancellationToken cancellationToken = default)
    {
        var results = new List<PreflightResult>(checks.Count);

        foreach (var check in checks)
        {
            if (scope == PreflightScope.PureOnly && check.RequiresIo)
            {
                results.Add(PreflightResult.Skip(check.Name, "not run: this check needs I/O") with
                {
                    Severity = check.Severity,
                });
                continue;
            }

            try
            {
                var result = await check.RunAsync(cancellationToken).ConfigureAwait(false);

                // The check reports what it observed; the runner decides what that costs, so a check
                // cannot forget to stamp its own severity onto one of its several return paths.
                results.Add(result with { Severity = check.Severity });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // A broken check must not take the process down with it.
            catch (Exception ex)
            {
                results.Add(PreflightResult.Fail(
                    check.Name,
                    $"the check itself failed: {ex.Message}",
                    "this is a Charter bug or an unreachable dependency; the message above is the raw error") with
                {
                    Severity = check.Severity,
                });
            }
#pragma warning restore CA1031
        }

        return new PreflightReport(results);
    }
}
