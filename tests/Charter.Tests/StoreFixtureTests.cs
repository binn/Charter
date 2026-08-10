using Charter.Configuration;
using Charter.Data;
using Charter.Data.Notifications;
using Charter.Data.Teaching;
using Charter.Domain;
using Charter.Notifications;
using Charter.Teaching;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Charter.Tests;

/// <summary>
/// A throwaway database, a service provider over it, and enough seeded rows to hang a foreign key on.
/// </summary>
/// <remarks>
/// Shared by every <c>Store*Tests</c> class. Like the job queue's fixture, it returns null and the
/// caller returns green when <c>CHARTER_TEST_DATABASE_URL</c> is not set — a developer without Docker
/// still gets a green build, and CI sets the variable. Every fixture seeds its own organisation and
/// its own user, so the tests are safe to run against a shared database and in parallel.
/// </remarks>
internal sealed class StoreFixture : IAsyncDisposable
{
    public const string DatabaseUrlVariable = "CHARTER_TEST_DATABASE_URL";

    private readonly ServiceProvider _provider;

    private StoreFixture(ServiceProvider provider, ModelFakeTimeProvider clock, Guid orgId, Guid userId)
    {
        _provider = provider;
        Clock = clock;
        OrgId = orgId;
        UserId = userId;
    }

    /// <summary>The instant every store in this fixture reads. Move it to cross a day boundary.</summary>
    public ModelFakeTimeProvider Clock { get; }

    public Guid OrgId { get; }

    public Guid UserId { get; }

    public IServiceProvider Services => _provider;

    public IServiceScopeFactory Scopes => _provider.GetRequiredService<IServiceScopeFactory>();

    /// <summary>Returns null — and the caller returns green — when no test database is configured.</summary>
    public static async Task<StoreFixture?> CreateAsync(DateTimeOffset? now = null)
    {
        var url = Environment.GetEnvironmentVariable(DatabaseUrlVariable);
        if (string.IsNullOrWhiteSpace(url))
        {
            Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the store tests.");
            return null;
        }

        var clock = new ModelFakeTimeProvider(now ?? new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero));

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<TimeProvider>(clock);
        services.AddCharterData(DatabaseUrl.ToNpgsql(url));

        var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CharterDbContext>();
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

            var tag = Guid.CreateVersion7().ToString("N");
            var organization = Organization.Create($"store-tests-{tag}");
            var user = User.Create($"reader-{tag}@charter.invalid", "Reader");

            db.Organizations.Add(organization);
            db.Users.Add(user);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            return new StoreFixture(provider, clock, organization.Id, user.Id);
        }
    }

    /// <summary>A second person, for the tests that need one ledger not to be the other's.</summary>
    public async Task<Guid> AddUserAsync(string label)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharterDbContext>();

        var user = User.Create($"{label}-{Guid.CreateVersion7():N}@charter.invalid", label);
        db.Users.Add(user);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return user.Id;
    }

    /// <summary>The repo → request → spec → session chain a walkthrough hangs off.</summary>
    public async Task<Guid> AddSessionAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharterDbContext>();

        var tag = Guid.CreateVersion7().ToString("N");
        var repo = Repo.Connect(OrgId, 4242, $"charter/store-{tag}");
        var request = Request.File(OrgId, repo.Id, UserId, "The totals are wrong past ten lines.");
        var spec = Spec.Draft(request.Id, 1, "Fix the totals", "Totals add up", "body", "[]");
        var session = Session.Queue(spec.Id, RunnerKind.Docker, "anthropic/claude-opus-5");

        db.Repos.Add(repo);
        db.Requests.Add(request);
        db.Specs.Add(spec);
        db.Sessions.Add(session);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return session.Id;
    }

    /// <summary>
    /// Empties the delivery log.
    /// </summary>
    /// <remarks>
    /// The delivery log is the one table here with no user or organisation on it — mail goes to
    /// people who have no account yet, which is the whole point of an invitation — so it cannot be
    /// namespaced per fixture the way every other table in these tests is. It is emptied instead.
    /// Only <c>StoreNotificationTests</c> writes to it, and its tests run in sequence.
    /// </remarks>
    public async Task ClearDeliveriesAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharterDbContext>();

        _ = await db.EmailDeliveries.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Runs <paramref name="work"/> against a fresh context, as a request would.</summary>
    public async Task<T> WithContextAsync<T>(Func<CharterDbContext, Task<T>> work)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<CharterDbContext>());
    }

    public async ValueTask DisposeAsync() => await _provider.DisposeAsync();
}

/// <summary>
/// Which implementation an instance actually gets.
/// </summary>
/// <remarks>
/// Every one of these stores has a working in-process default registered with <c>TryAdd</c> by the
/// subsystem that declared the seam. Those defaults lose everything the container does, so the
/// question this class answers is not "does a Postgres implementation exist" but "does it win".
/// </remarks>
public class StoreWiringTests
{
    private const string UnusedConnectionString =
        "Host=localhost;Port=5432;Database=charter;Username=charter;Password=unused";

    [Fact]
    public void ThePersistentTeachingStoresBeatTheInMemoryDefaults()
    {
        // AddCharterTeaching registers InMemoryConceptLedgerStore, InMemoryWalkthroughStore and
        // InMemoryExplainThisQuota with TryAdd. Data registers outright, and Program.cs calls it
        // first - but the assertion is deliberately made with teaching wired *after* the data layer
        // and, below, before it, because which store an instance gets must not depend on the order.
        using var provider = Build(services =>
        {
            services.AddCharterData(UnusedConnectionString);
            services.AddCharterTeaching();
        });

        using var scope = provider.CreateScope();

        Assert.IsType<EfConceptLedgerStore>(scope.ServiceProvider.GetRequiredService<IConceptLedgerStore>());
        Assert.IsType<EfWalkthroughStore>(scope.ServiceProvider.GetRequiredService<IWalkthroughStore>());
        Assert.IsType<EfExplainThisQuota>(scope.ServiceProvider.GetRequiredService<IExplainThisQuota>());
    }

    [Fact]
    public void TheOrderTheSubsystemsAreWiredInDoesNotDecideWhereTeachingStateLives()
    {
        using var provider = Build(services =>
        {
            services.AddCharterTeaching();
            services.AddCharterData(UnusedConnectionString);
        });

        using var scope = provider.CreateScope();

        Assert.IsType<EfConceptLedgerStore>(scope.ServiceProvider.GetRequiredService<IConceptLedgerStore>());
        Assert.IsType<EfWalkthroughStore>(scope.ServiceProvider.GetRequiredService<IWalkthroughStore>());
        Assert.IsType<EfExplainThisQuota>(scope.ServiceProvider.GetRequiredService<IExplainThisQuota>());
    }

    [Fact]
    public void ThePersistentNotificationStoresBeatTheInMemoryDefaults()
    {
        // DefaultNotificationPreferenceStore answers "email, for everyone" and RecentEmailDeliveryLog
        // is a ring buffer that empties on restart. Both are TryAdd, both lose to these.
        using var provider = Build(
            services => services.AddCharterData(UnusedConnectionString),
            validateOnBuild: true);

        using var scope = provider.CreateScope();

        Assert.IsType<EfNotificationPreferenceStore>(
            scope.ServiceProvider.GetRequiredService<INotificationPreferenceStore>());
        Assert.IsType<EfEmailDeliveryLog>(scope.ServiceProvider.GetRequiredService<IEmailDeliveryLog>());
    }

    [Fact]
    public void TheStoresAreSingletonsBecauseTheirConsumersAre()
    {
        // IEmailSender and the notification dispatcher are singletons and hold these. A scoped
        // registration would be a captive dependency the container refuses at startup - which is why
        // each store opens its own scope per operation rather than holding a DbContext.
        using var provider = Build(
            services => services.AddCharterData(UnusedConnectionString),
            validateOnBuild: true);

        var first = provider.GetRequiredService<IEmailDeliveryLog>();
        using var scope = provider.CreateScope();

        Assert.Same(first, scope.ServiceProvider.GetRequiredService<IEmailDeliveryLog>());
        Assert.Same(
            provider.GetRequiredService<IConceptLedgerStore>(),
            scope.ServiceProvider.GetRequiredService<IConceptLedgerStore>());
    }

    private static ServiceProvider Build(Action<ServiceCollection> configure, bool validateOnBuild = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        configure(services);

        // ValidateScopes always: a captive dependency has to be a failure here rather than a surprise
        // in production, because avoiding one is exactly what these lifetimes are arranged around.
        // ValidateOnBuild only where the collection is complete - AddCharterTeaching's generator
        // needs the model layer, which these tests deliberately do not wire up.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = validateOnBuild,
        });
    }
}
