using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Charter.Budgets;

/// <summary>
/// Releases budget reservations whose TTL has passed (section 34.4).
/// </summary>
/// <remarks>
/// <para>
/// <em>Reservations expire on a TTL so a crashed orchestrator doesn't strand budget.</em> A hold is
/// taken before a session runs and released when it settles; a control plane that dies in between
/// leaves one behind, and without this the team's cap shrinks by that amount until somebody goes
/// looking in the database.
/// </para>
/// <para>
/// The expiry is enforced twice over on purpose. <see cref="BudgetEvaluator"/> already excludes a
/// hold past its TTL when it measures headroom, so the arithmetic is correct whether or not this has
/// run; this tidies the rows so the ledger says what happened rather than leaving a reservation that
/// never resolved. Correctness does not depend on a background service being alive, which matters on
/// a PaaS that restarts containers whenever it likes (section 2.3).
/// </para>
/// </remarks>
public sealed class BudgetReservationSweeper : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BudgetReservationSweeper> _logger;

    /// <summary>How often expired holds are swept.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>Creates the sweeper.</summary>
    public BudgetReservationSweeper(
        IServiceScopeFactory scopeFactory,
        ILogger<BudgetReservationSweeper> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Runs one sweep. Exposed so a test does not have to wait for the timer.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        return await scope.ServiceProvider
            .GetRequiredService<IBudgetEvaluator>()
            .SweepExpiredAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A sweep that throws must not take the host down with it.
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "The budget reservation sweep failed. Headroom is still measured correctly — an "
                    + "expired hold stops counting whether or not it has been released — but the "
                    + "ledger keeps rows that never resolved.");
            }
#pragma warning restore CA1031

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
