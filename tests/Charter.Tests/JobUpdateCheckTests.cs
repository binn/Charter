using System.Data.Common;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Charter.Orchestration;
using Charter.Updates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Charter.Tests;

/// <summary>
/// The release check on the real queue (sections 2.3, 28).
/// </summary>
/// <remarks>
/// <para>
/// Section 28 says the result is cached in Postgres and section 2.3 says nothing about a session may
/// live in process, so the schedule and the cached result are both rows: a pending
/// <see cref="JobType.UpdateCheck"/> job whose payload is what the last check found. Everything here
/// is about that arrangement surviving contact with a database — claiming, completing, re-arming, and
/// converging when two of them exist.
/// </para>
/// <para>
/// Runs only when <c>CHARTER_TEST_DATABASE_URL</c> points at a throwaway Postgres, like every other
/// database-backed suite here. No test in this file reaches the network: the release source is canned.
/// </para>
/// </remarks>
public class JobUpdateCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AFinishedCheckArmsTheNextOneADayOutCarryingWhatItFound()
    {
        await using var fixture = await UpdateQueueFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        await fixture.EnqueueCheckAsync(UpdateStatus.Unknown(UpdateChannel.Stable, "0.5.0").ToJson());
        var claimed = await fixture.ClaimAsync();

        var result = await fixture.Handler().HandleAsync(claimed, TestContext.Current.CancellationToken);

        // Completed, not failed: a failure burns an attempt, and three of them would strand the
        // schedule in a terminal state with nothing left to re-arm it.
        Assert.Equal(JobHandling.Completed, result.Handling);

        var next = await fixture.PendingCheckAsync();

        Assert.NotNull(next);
        Assert.InRange(
            next.AvailableAt,
            Now + TimeSpan.FromHours(24),
            Now + TimeSpan.FromHours(27));

        // The tag this test namespaces its rows with rides along, which is the same mechanism that
        // keeps the production check on whatever ran it.
        Assert.Contains(fixture.Tag, next.RequiredCapabilities);

        var status = UpdateStatus.TryParse(next.Payload);

        Assert.NotNull(status);
        Assert.True(status.UpdateAvailable);
        Assert.Equal("v0.6.0", status.LatestTag);
        Assert.Equal(Now, status.CheckedAt);
    }

    [Fact]
    public async Task TheResultIsReadableAfterTheProcessThatFoundItIsGone()
    {
        await using var fixture = await UpdateQueueFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        await fixture.EnqueueCheckAsync(UpdateStatus.Unknown(UpdateChannel.Stable, "0.5.0").ToJson());
        var claimed = await fixture.ClaimAsync();
        await fixture.Handler().HandleAsync(claimed, TestContext.Current.CancellationToken);

        // A different context, which is as close as a test gets to "the container restarted": nothing
        // was remembered, and the answer comes back out of Postgres.
        await using var reader = fixture.Fresh();
        var status = await new UpdateStatusReader(reader).ReadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(status);
        Assert.Equal("v0.6.0", status.LatestTag);
        Assert.Equal("stable", status.Channel);
    }

    [Fact]
    public async Task AnOfflineCheckStillReArmsAndKeepsThePreviousAnswer()
    {
        await using var fixture = await UpdateQueueFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var known = UpdateStatus.Available(
            UpdateChannel.Stable,
            "0.5.0",
            new Release(
                "v0.6.0",
                ReleaseVersion.TryParse("v0.6.0")!,
                "v0.6.0",
                "https://github.com/binn/Charter/releases/tag/v0.6.0",
                "Notes.",
                IsPrerelease: false,
                PublishedAt: null),
            Now - TimeSpan.FromDays(3));

        await fixture.EnqueueCheckAsync(known.ToJson());
        var claimed = await fixture.ClaimAsync();

        var result = await fixture.Handler(offline: true)
            .HandleAsync(claimed, TestContext.Current.CancellationToken);

        Assert.Equal(JobHandling.Completed, result.Handling);

        var next = await fixture.PendingCheckAsync();
        var status = UpdateStatus.TryParse(next!.Payload);

        // An air-gapped instance keeps what it knew and keeps checking, without an error a day.
        Assert.NotNull(status);
        Assert.True(status.UpdateAvailable);
        Assert.Equal(Now - TimeSpan.FromDays(3), status.CheckedAt);
    }

    [Fact]
    public async Task TwoScheduledChecksConvergeOnOne()
    {
        await using var fixture = await UpdateQueueFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        // Two replicas seeding in the same instant is the case this defends against.
        await fixture.EnqueueCheckAsync(UpdateStatus.Unknown(UpdateChannel.Stable, "0.5.0").ToJson());
        await fixture.EnqueueCheckAsync(UpdateStatus.Unknown(UpdateChannel.Stable, "0.5.0").ToJson());

        var claimed = await fixture.ClaimAsync();
        await fixture.Handler().HandleAsync(claimed, TestContext.Current.CancellationToken);

        var pending = await fixture.PendingChecksAsync();

        Assert.Single(pending);
        Assert.Equal(1, await fixture.CountAsync(JobStatus.Cancelled));
    }

    [Fact]
    public async Task TheSchedulerSeedsTheFirstCheckAndThenLeavesTheQueueAlone()
    {
        await using var fixture = await UpdateQueueFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        await fixture.ClearChecksAsync();

        var scheduler = fixture.Scheduler();

        Assert.True(await scheduler.EnsureScheduledAsync(TestContext.Current.CancellationToken));

        // Every check after the first is armed by the check before it, so a restart must not add one.
        Assert.False(await scheduler.EnsureScheduledAsync(TestContext.Current.CancellationToken));

        await fixture.ClearChecksAsync();
    }

    [Fact]
    public async Task TurningTheCheckOffDrainsAScheduledOneRatherThanChurningIt()
    {
        await using var fixture = await UpdateQueueFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        await fixture.EnqueueCheckAsync(UpdateStatus.Unknown(UpdateChannel.Stable, "0.5.0").ToJson());
        var claimed = await fixture.ClaimAsync();

        var handler = new DisabledUpdateCheckJobHandler(new RecordingLogger<DisabledUpdateCheckJobHandler>());
        var result = await handler.HandleAsync(claimed, TestContext.Current.CancellationToken);

        Assert.Equal(JobHandling.Completed, result.Handling);

        await fixture.Queue.CompleteAsync(
            claimed.Id,
            fixture.WorkerId,
            cancellationToken: TestContext.Current.CancellationToken);

        // Nothing re-armed: the schedule stops rather than being deferred round the queue forever.
        Assert.Empty(await fixture.PendingChecksAsync());
    }

    /// <summary>A release source that never opens a socket.</summary>
    private sealed class CannedReleaseSource : IReleaseSource
    {
        private readonly IReadOnlyList<Release>? _releases;

        public CannedReleaseSource(IReadOnlyList<Release>? releases) => _releases = releases;

        public Task<IReadOnlyList<Release>?> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_releases);
    }

    /// <summary>
    /// A queue against a throwaway Postgres, with every row this test writes tagged.
    /// </summary>
    /// <remarks>
    /// The tag is a required capability nothing else advertises, so these rows are invisible to any
    /// other suite claiming from the same database — including one claiming with no capability filter
    /// at all, which is what the control plane's own dispatcher does.
    /// </remarks>
    private sealed class UpdateQueueFixture : IAsyncDisposable
    {
        private const string DatabaseUrlVariable = "CHARTER_TEST_DATABASE_URL";

        private readonly string _connectionString;
        private readonly List<CharterDbContext> _contexts = [];
        private readonly List<ServiceProvider> _providers = [];

        private UpdateQueueFixture(string connectionString, CharterDbContext db, string tag)
        {
            _connectionString = connectionString;
            Db = db;
            Tag = tag;
            WorkerId = $"worker-{tag}";
            Queue = new JobQueue(db);
        }

        public CharterDbContext Db { get; }

        public JobQueue Queue { get; }

        public string Tag { get; }

        public string WorkerId { get; }

        public static async Task<UpdateQueueFixture?> CreateAsync()
        {
            var url = Environment.GetEnvironmentVariable(DatabaseUrlVariable);
            if (string.IsNullOrWhiteSpace(url))
            {
                Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the update check queue tests.");
                return null;
            }

            var connectionString = DatabaseUrl.ToNpgsql(url);
            var db = Create(connectionString);

            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

            return new UpdateQueueFixture(connectionString, db, $"t{Guid.CreateVersion7():N}");
        }

        /// <summary>A second context on the same database, standing in for a restarted process.</summary>
        public CharterDbContext Fresh()
        {
            var db = Create(_connectionString);
            _contexts.Add(db);
            return db;
        }

        public async Task EnqueueCheckAsync(string payload)
            => await Queue.EnqueueAsync(
                JobType.UpdateCheck,
                payload,
                requiredCapabilities: [Tag],
                now: Now,
                cancellationToken: TestContext.Current.CancellationToken);

        public async Task<ClaimedJob> ClaimAsync()
        {
            var claimed = await Queue.ClaimAsync(
                WorkerId,
                capabilities: [Tag],
                now: Now,
                cancellationToken: TestContext.Current.CancellationToken);

            return Assert.Single(claimed);
        }

        public UpdateCheckJobHandler Handler(bool offline = false)
        {
            var releases = offline
                ? null
                : new List<Release>
                {
                    new(
                        "v0.6.0",
                        ReleaseVersion.TryParse("v0.6.0")!,
                        "v0.6.0",
                        "https://github.com/binn/Charter/releases/tag/v0.6.0",
                        "Notes.",
                        IsPrerelease: false,
                        PublishedAt: Now - TimeSpan.FromDays(8)),
                };

            return new UpdateCheckJobHandler(
                new CannedReleaseSource(releases),
                new UpdateCheckConfig { Enabled = true, Channel = UpdateChannel.Stable },
                new UpdateCheckOptions { CurrentVersion = "0.5.0", Repository = "binn/Charter" },
                Queue,
                Db,
                new ModelFakeTimeProvider(Now),
                new RecordingLogger<UpdateCheckJobHandler>());
        }

        public UpdateCheckScheduler Scheduler()
        {
            var services = new ServiceCollection();
            services.AddCharterData(_connectionString);

            var provider = services.BuildServiceProvider();
            _providers.Add(provider);

            return new UpdateCheckScheduler(
                provider.GetRequiredService<IServiceScopeFactory>(),
                new UpdateCheckConfig { Enabled = true, Channel = UpdateChannel.Stable },
                new UpdateCheckOptions { CurrentVersion = "0.5.0" },
                new ModelFakeTimeProvider(Now),
                new RecordingLogger<UpdateCheckScheduler>());
        }

        public async Task<Job?> PendingCheckAsync() => (await PendingChecksAsync()).SingleOrDefault();

        public async Task<IReadOnlyList<Job>> PendingChecksAsync()
            => [.. (await TaggedAsync()).Where(job => job.Status == JobStatus.Pending)];

        public async Task<int> CountAsync(JobStatus status)
            => (await TaggedAsync()).Count(job => job.Status == status);

        /// <summary>Removes untagged checks so a seeding assertion is about this test's own row.</summary>
        public async Task ClearChecksAsync()
            => await Db.Jobs
                .Where(job => job.Type == JobType.UpdateCheck)
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Db.Jobs
                    .Where(job => job.Type == JobType.UpdateCheck)
                    .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            }
            catch (Exception exception) when (exception is DbException or OperationCanceledException)
            {
                // Best effort. A leftover row is a row the next run deletes.
            }

            foreach (var context in _contexts)
            {
                await context.DisposeAsync();
            }

            foreach (var provider in _providers)
            {
                await provider.DisposeAsync();
            }

            await Db.DisposeAsync();
        }

        /// <summary>
        /// This test's own checks. The capability filter is applied in memory rather than in SQL:
        /// the set is tiny, and translating a containment test over a text[] is not what is under
        /// test here.
        /// </summary>
        private async Task<IReadOnlyList<Job>> TaggedAsync()
        {
            var jobs = await Db.Jobs
                .AsNoTracking()
                .Where(job => job.Type == JobType.UpdateCheck)
                .ToListAsync(TestContext.Current.CancellationToken);

            return [.. jobs.Where(job => job.RequiredCapabilities.Contains(Tag))];
        }

        private static CharterDbContext Create(string connectionString)
        {
            var options = new DbContextOptionsBuilder<CharterDbContext>();
            DataServiceCollectionExtensions.ConfigureNpgsql(options, connectionString);

            return new CharterDbContext(options.Options);
        }
    }
}
