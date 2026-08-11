using System.Globalization;
using System.Text;
using Charter.Auth.Providers;
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
/// demonstrating the empty states. It also means the seeded accounts are the <em>only</em> way in, so
/// this service prints them where section 30.1 already trained the operator to look - stdout, next to
/// the preflight results, in the place the setup token would otherwise be.
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
        var hasher = scope.ServiceProvider.GetRequiredService<ICharterPasswordHasher>();

        var outcome = await new DemoSeeder(database, time, hasher)
            .SeedAsync(cancellationToken)
            .ConfigureAwait(false);

        if (outcome == DemoSeedOutcome.Occupied)
        {
            // No credentials printed. This database belongs to someone else, the seeded accounts do
            // not exist in it, and naming a password that nearly worked is the worst of both.
            logger.LogWarning(
                "CHARTER_DEMO is on and outbound calls are blocked, but this database already holds "
                + "an organisation or a user that is not the demonstration data, so nothing was "
                + "written and no demonstration account exists here (section 30.6). Sign in with the "
                + "accounts this instance already has, or point DATABASE_URL at an empty database.");

            return;
        }

        logger.LogWarning(
            "{Banner}",
            Banner(outcome, DateTimeOffset.UtcNow));
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// The block an operator reads to get into the instance, and the warning that comes with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately loud and multi-line, for the same reason <c>SetupHostedService</c>'s token block
    /// is: a credential on one line inside structured JSON is a credential nobody sees. This replaces
    /// that block on a demo instance, because seeding ends setup mode and the token is never issued.
    /// </para>
    /// <para>
    /// The warning is not decoration. A published password plus a reachable URL is a real exposure,
    /// and the moment to say so is the moment the instance starts answering on a port.
    /// </para>
    /// </remarks>
    internal static string Banner(DemoSeedOutcome outcome, DateTimeOffset now)
    {
        var banner = new StringBuilder();

        banner.Append(CultureInfo.InvariantCulture, $"""

              ┌───────────────────────────────────────────────────────────────────────┐
              │  CHARTER_DEMO=true — this is a demonstration instance.                │
              │  Every request, session, transcript and preview in it is invented.    │
              │  It makes no outbound calls: no model provider, no code host, no      │
              │  mail server. Nothing here can be used for real work.                 │
              └───────────────────────────────────────────────────────────────────────┘

              Organisation: {DemoSeeder.OrganizationName}
              Repository:   {DemoSeeder.RepositoryFullName}
              {(outcome == DemoSeedOutcome.AlreadySeeded
                  ? "Seeded on an earlier boot; nothing was rewritten."
                  : "Seeded just now, into an empty database.")}

              Sign in at /sign-in. Both accounts use the password:

                  {DemoSeeder.Password}


            """);

        foreach (var account in DemoSeeder.Accounts)
        {
            banner.Append(CultureInfo.InvariantCulture, $"""
                  {account.Email}
                      {account.DisplayName} — {account.Roles}
                      Sees {account.Sees}.


                """);
        }

        banner.Append(CultureInfo.InvariantCulture, $"""
              Sign in as each in turn: the difference between the two views is the
              product, and it is enforced by the API, not by the page.

              This password is published in Charter's documentation, so treat this
              instance as public. Do not expose it on an address you would not hand
              out, and do not keep this database once you start real work — claim a
              fresh one instead, so no account with a known password survives into it.

              (section 30.6, printed at {now.ToString("u", CultureInfo.InvariantCulture)})

            """);

        return banner.ToString();
    }
}
