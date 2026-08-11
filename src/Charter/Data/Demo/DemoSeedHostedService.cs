using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Charter.Data.Demo;

/// <summary>
/// Seeds the section 30.6 demonstration data on boot, when <c>CHARTER_DEMO=true</c>.
/// </summary>
/// <remarks>
/// <para>
/// Registered only by <c>AddCharterDemoMode</c>, so on an ordinary instance this type is not in the
/// graph at all and cannot write anything by accident.
/// </para>
/// <para>
/// It runs after migrations, because <c>Program.cs</c> migrates before it starts the host, and after
/// preflight, which is registered ahead of it — so by the time it inserts a row the schema exists and
/// the database has already been confirmed reachable with a message an operator can read.
/// </para>
/// <para>
/// Seeding creates users, which ends setup mode (section 30.1). That is the intent: a demonstration
/// instance that greeted an evaluator with a one-time setup token and an empty database would be
/// demonstrating the empty states.
/// </para>
/// </remarks>
public sealed class DemoSeedHostedService(
    IServiceProvider services,
    TimeProvider time,
    ILogger<DemoSeedHostedService> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        await using var scope = services.CreateAsyncScope();

        var database = scope.ServiceProvider.GetRequiredService<CharterDbContext>();
        var seeded = await new DemoSeeder(database, time).SeedAsync(cancellationToken).ConfigureAwait(false);

        if (seeded)
        {
            logger.LogWarning(
                "CHARTER_DEMO is on. This instance has been seeded with the demonstration "
                + "organisation '{Organization}' and repository '{Repository}', and will make no "
                + "outbound calls: no model provider, no code host, no mail server (section 30.6). "
                + "Do not use this instance for real work.",
                DemoSeeder.OrganizationName,
                DemoSeeder.RepositoryFullName);
        }
        else
        {
            logger.LogWarning(
                "CHARTER_DEMO is on and outbound calls are blocked, but this database already has "
                + "an organisation, so no demonstration data was written (section 30.6).");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
