using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Charter.Configuration.Preflight;

/// <summary>
/// Every blocking preflight failure, reported together, as one exception (sections 4.1, 30.1).
/// </summary>
/// <remarks>
/// Section 4.1's rule for configuration - print <em>all</em> problems at once and exit non-zero - is
/// the same rule here, and it is the reason this carries a rendered report rather than the first
/// failure. An operator who fixes one variable, redeploys, and is told about the next one has been
/// made to pay for a round trip Charter already knew about.
/// </remarks>
public sealed class PreflightException : Exception
{
    /// <summary>Creates an exception carrying <paramref name="message"/>.</summary>
    public PreflightException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception carrying <paramref name="message"/> and <paramref name="innerException"/>.</summary>
    public PreflightException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates an exception with no message. Present for the framework's benefit only.</summary>
    public PreflightException()
    {
    }

    /// <summary>Renders <paramref name="report"/> as the message an operator reads on a failed boot.</summary>
    public static PreflightException From(PreflightReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var lines = new List<string>
        {
            "Charter cannot start. Preflight found the following blocking problems (section 30.1):",
        };

        lines.AddRange(report.BlockingFailures.Select(failure => "  " + failure.Describe()));

        return new PreflightException(string.Join(Environment.NewLine, lines));
    }
}

/// <summary>
/// Runs the section 30.1 first-run checks at startup and displays the results.
/// </summary>
/// <remarks>
/// <para>
/// The checks, the runner and their tests all existed before this did, and nothing invoked them:
/// <c>Program.cs</c> goes parse, build, migrate, configure, run, and none of those steps asks. A
/// hosted service is the seam that fits, because it is the only startup hook the host owns that runs
/// <em>after</em> <c>MigrateAsync</c> - which matters, since "migrations applied" is otherwise a
/// check on work that has not happened yet.
/// </para>
/// <para>
/// It is registered at the front of the hosted-service list so it runs before Kestrel binds a
/// socket. Section 30.1 says never boot into a half-working state; an instance that serves requests
/// for the half second it takes to discover its database is gone has done exactly that.
/// </para>
/// <para>
/// A blocking failure throws, which fails <c>IHost.StartAsync</c>, which <c>Program.cs</c> already
/// catches and turns into a non-zero exit - the section 4.1 contract, reached without the entry point
/// needing to know preflight exists.
/// </para>
/// </remarks>
public sealed class PreflightHostedService(
    PreflightRunner runner,
    ILogger<PreflightHostedService> logger) : IHostedService
{
    /// <summary>
    /// How long the whole pass may take. Every check is an I/O call with its own timeout, but a DNS
    /// server that blackholes packets answers none of them, and a container that hangs at boot is
    /// harder to diagnose than one that fails.
    /// </summary>
    public static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(30);

    /// <summary>Runs the checks, logs every result, and throws if any blocking check failed.</summary>
    /// <exception cref="PreflightException">At least one blocking check failed.</exception>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(logger);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        var report = await runner.RunAsync(PreflightScope.All, timeout.Token).ConfigureAwait(false);

        Report(report, logger);

        if (report.ShouldHalt)
        {
            throw PreflightException.From(report);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Writes the whole report, one line per check, at the level each result deserves.
    /// </summary>
    /// <remarks>
    /// Every line is logged before anything throws. The point of preflight is the list, not the first
    /// item on it: an operator whose database is unreachable usually has a second thing wrong too,
    /// and finding out about it on the next redeploy is the failure mode this exists to prevent.
    /// </remarks>
    internal static void Report(PreflightReport report, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(logger);

        logger.LogInformation("Preflight checks (section 30.1):");

        foreach (var result in report.Results)
        {
            switch (result.Status)
            {
                case PreflightStatus.Failed when result.Severity == PreflightSeverity.Blocking:
                    logger.LogError("  {Check}", result.Describe());
                    break;
                case PreflightStatus.Failed:
                    logger.LogWarning("  {Check}", result.Describe());
                    break;
                default:
                    logger.LogInformation("  {Check}", result.Describe());
                    break;
            }
        }

        if (report.Advisories.Count > 0 && !report.ShouldHalt)
        {
            logger.LogWarning(
                "Preflight passed with {Count} warning(s). Charter is starting; the warnings above " +
                "describe things that will fail later if they are real.",
                report.Advisories.Count);
        }
    }
}
