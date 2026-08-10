using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;

namespace Charter.Tests;

/// <summary>
/// The queue's SQL, checked without a database.
/// </summary>
/// <remarks>
/// EF cannot express <c>SKIP LOCKED</c>, so these statements are hand-written and therefore the part
/// of the persistence layer most able to rot silently. These assertions pin the properties that make
/// them safe: the skip-locked claim itself, and the fact that no caller-supplied value is ever
/// concatenated into the text.
/// </remarks>
public class DataJobQueueSqlTests
{
    public static TheoryData<string> Statements =>
    [
        JobQueue.ClaimSql,
        JobQueue.RenewLeaseSql,
        JobQueue.CompleteSql,
        JobQueue.FailSql,
        JobQueue.ReleaseExpiredLeasesSql,
        JobQueue.ReleaseWorkerSql,
        JobQueue.TryAdvisoryLockSql,
        JobQueue.AdvisoryUnlockSql,
    ];

    [Fact]
    public void TheClaimLocksRowsAndSkipsOnesAnotherWorkerHolds()
    {
        // Section 2.3. Without SKIP LOCKED, a second dispatcher blocks on the first instead of
        // taking the next job, and the queue serialises.
        Assert.Contains("FOR UPDATE SKIP LOCKED", JobQueue.ClaimSql, StringComparison.Ordinal);

        // Bounded, ordered, and stamped with the lease in the same statement.
        Assert.Contains("LIMIT @batch_size", JobQueue.ClaimSql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY priority DESC, available_at, created_at", JobQueue.ClaimSql, StringComparison.Ordinal);
        Assert.Contains("lease_expires_at = @now + @lease", JobQueue.ClaimSql, StringComparison.Ordinal);
        Assert.Contains("attempts = j.attempts + 1", JobQueue.ClaimSql, StringComparison.Ordinal);
    }

    [Fact]
    public void CapabilityFilteringIsAnArrayContainmentTest()
    {
        // Section 27.3: an agent only ever sees jobs it can actually run, and a null parameter means
        // "no filter" for an in-process dispatcher.
        Assert.Contains(
            "(@capabilities IS NULL OR required_capabilities <@ @capabilities)",
            JobQueue.ClaimSql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EveryMutationAdvancesTheConcurrencyToken()
    {
        foreach (var sql in new[]
                 {
                     JobQueue.ClaimSql,
                     JobQueue.RenewLeaseSql,
                     JobQueue.CompleteSql,
                     JobQueue.FailSql,
                     JobQueue.ReleaseExpiredLeasesSql,
                     JobQueue.ReleaseWorkerSql,
                 })
        {
            Assert.Contains("version = ", sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CompletingAndFailingRequireTheClaimToStillBeHeld()
    {
        // A worker whose lease expired must not be able to finish a job somebody else now owns.
        Assert.Contains("AND claimed_by = @worker", JobQueue.CompleteSql, StringComparison.Ordinal);
        Assert.Contains("AND claimed_by = @worker", JobQueue.FailSql, StringComparison.Ordinal);
        Assert.Contains("AND claimed_by = @worker", JobQueue.RenewLeaseSql, StringComparison.Ordinal);
    }

    [Fact]
    public void LeaseExpiryReturnsWorkToTheQueueUntilAttemptsAreSpent()
    {
        Assert.Contains("lease_expires_at <= @now", JobQueue.ReleaseExpiredLeasesSql, StringComparison.Ordinal);
        Assert.Contains(
            "CASE WHEN attempts >= max_attempts THEN 'failed' ELSE 'pending' END",
            JobQueue.ReleaseExpiredLeasesSql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheDispatcherLockIsAdvisoryAndReleasable()
    {
        Assert.Contains("pg_try_advisory_lock(@key)", JobQueue.TryAdvisoryLockSql, StringComparison.Ordinal);
        Assert.Contains("pg_advisory_unlock(@key)", JobQueue.AdvisoryUnlockSql, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Statements))]
    public void NoStatementInterpolatesAValue(string sql)
    {
        // Values arrive as bound parameters. A literal quote around anything but a known enum
        // spelling would mean somebody built SQL from input.
        Assert.DoesNotContain("{", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("+ \"", sql, StringComparison.Ordinal);
        Assert.Contains("@", sql, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Statements))]
    public void EveryStatementTouchesOnlyTheQueueTable(string sql)
    {
        var referencesAnotherTable = sql.Contains(" sessions", StringComparison.Ordinal)
            || sql.Contains(" requests", StringComparison.Ordinal)
            || sql.Contains(" events", StringComparison.Ordinal);

        Assert.False(referencesAnotherTable, "The queue must be claimable without touching another table.");
    }
}

/// <summary>
/// The queue against a real Postgres.
/// </summary>
/// <remarks>
/// <c>SKIP LOCKED</c>, advisory locks and lease expiry have no in-memory equivalent, so these run
/// only when <c>CHARTER_TEST_DATABASE_URL</c> points at a throwaway database and skip otherwise —
/// a developer without Docker still gets a green build, and CI can set the variable against its
/// Postgres service. Each test namespaces its rows with its own worker identity, so they are safe to
/// run against a shared database.
/// </remarks>
public class DataJobQueueIntegrationTests
{
    private const string DatabaseUrlVariable = "CHARTER_TEST_DATABASE_URL";

    [Fact]
    public async Task AClaimedJobIsInvisibleToOtherWorkers()
    {
        await using var fixture = await QueueFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var worker = fixture.WorkerId;
        await fixture.Queue.EnqueueAsync(
            JobType.Build,
            """{"sessionId":"1"}""",
            requiredCapabilities: [fixture.Tag],
            cancellationToken: TestContext.Current.CancellationToken);

        var first = await fixture.Queue.ClaimAsync(
            worker,
            capabilities: [fixture.Tag],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(first);
        Assert.Equal(1, first[0].Attempts);

        var second = await fixture.Queue.ClaimAsync(
            $"{worker}-other",
            capabilities: [fixture.Tag],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(second);

        Assert.True(await fixture.Queue.CompleteAsync(
            first[0].Id,
            worker,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AJobIsOnlyOfferedToARunnerThatCanActuallyRunIt()
    {
        await using var fixture = await QueueFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        await fixture.Queue.EnqueueAsync(
            JobType.Build,
            "{}",
            requiredCapabilities: [fixture.Tag, "xcode:16"],
            cancellationToken: TestContext.Current.CancellationToken);

        var linuxOnly = await fixture.Queue.ClaimAsync(
            fixture.WorkerId,
            capabilities: [fixture.Tag],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(linuxOnly);

        var mac = await fixture.Queue.ClaimAsync(
            fixture.WorkerId,
            capabilities: [fixture.Tag, "xcode:16", "macos"],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(mac);
    }

    [Fact]
    public async Task ACrashedWorkersJobReturnsToTheQueue()
    {
        await using var fixture = await QueueFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        await fixture.Queue.EnqueueAsync(
            JobType.Recon,
            "{}",
            requiredCapabilities: [fixture.Tag],
            cancellationToken: TestContext.Current.CancellationToken);

        var claimed = await fixture.Queue.ClaimAsync(
            fixture.WorkerId,
            lease: TimeSpan.FromSeconds(30),
            capabilities: [fixture.Tag],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(claimed);

        // The worker never heartbeats again; the sweep runs a minute later.
        var sweptAt = DateTimeOffset.UtcNow.AddMinutes(1);
        var released = await fixture.Queue.ReleaseExpiredLeasesAsync(sweptAt, TestContext.Current.CancellationToken);

        Assert.True(released >= 1);

        var reclaimed = await fixture.Queue.ClaimAsync(
            $"{fixture.WorkerId}-successor",
            capabilities: [fixture.Tag],
            now: sweptAt,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(reclaimed);
        Assert.Equal(2, reclaimed[0].Attempts);

        // And the original worker can no longer finish the job it lost.
        Assert.False(await fixture.Queue.CompleteAsync(
            claimed[0].Id,
            fixture.WorkerId,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailureRetriesUntilAttemptsAreSpentAndThenStops()
    {
        await using var fixture = await QueueFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var job = await fixture.Queue.EnqueueAsync(
            JobType.Teach,
            "{}",
            maxAttempts: 2,
            requiredCapabilities: [fixture.Tag],
            cancellationToken: TestContext.Current.CancellationToken);

        var first = await fixture.Queue.ClaimAsync(
            fixture.WorkerId,
            capabilities: [fixture.Tag],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            JobStatus.Pending,
            await fixture.Queue.FailAsync(
                job.Id,
                fixture.WorkerId,
                "the model returned nothing",
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(first);

        var second = await fixture.Queue.ClaimAsync(
            fixture.WorkerId,
            capabilities: [fixture.Tag],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(second);
        Assert.Equal(2, second[0].Attempts);

        Assert.Equal(
            JobStatus.Failed,
            await fixture.Queue.FailAsync(
                job.Id,
                fixture.WorkerId,
                "the model returned nothing again",
                cancellationToken: TestContext.Current.CancellationToken));

        var third = await fixture.Queue.ClaimAsync(
            fixture.WorkerId,
            capabilities: [fixture.Tag],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(third);
    }

    [Fact]
    public async Task GracefulShutdownHandsBackWhatThisInstanceHolds()
    {
        await using var fixture = await QueueFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        await fixture.Queue.EnqueueAsync(
            JobType.Refine,
            "{}",
            requiredCapabilities: [fixture.Tag],
            cancellationToken: TestContext.Current.CancellationToken);

        await fixture.Queue.ClaimAsync(
            fixture.WorkerId,
            capabilities: [fixture.Tag],
            cancellationToken: TestContext.Current.CancellationToken);

        var released = await fixture.Queue.ReleaseWorkerClaimsAsync(
            fixture.WorkerId,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, released);

        var reclaimed = await fixture.Queue.ClaimAsync(
            $"{fixture.WorkerId}-successor",
            capabilities: [fixture.Tag],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(reclaimed);
    }

    [Fact]
    public async Task ADelayedJobIsNotClaimedEarly()
    {
        await using var fixture = await QueueFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        await fixture.Queue.EnqueueAsync(
            JobType.UpdateCheck,
            "{}",
            requiredCapabilities: [fixture.Tag],
            availableAt: DateTimeOffset.UtcNow.AddHours(6),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(await fixture.Queue.ClaimAsync(
            fixture.WorkerId,
            capabilities: [fixture.Tag],
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Single(await fixture.Queue.ClaimAsync(
            fixture.WorkerId,
            capabilities: [fixture.Tag],
            now: DateTimeOffset.UtcNow.AddHours(7),
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HigherPriorityIsClaimedFirst()
    {
        await using var fixture = await QueueFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        await fixture.Queue.EnqueueAsync(
            JobType.Prune,
            """{"which":"low"}""",
            priority: 0,
            requiredCapabilities: [fixture.Tag],
            cancellationToken: TestContext.Current.CancellationToken);

        await fixture.Queue.EnqueueAsync(
            JobType.Build,
            """{"which":"high"}""",
            priority: 10,
            requiredCapabilities: [fixture.Tag],
            cancellationToken: TestContext.Current.CancellationToken);

        var claimed = await fixture.Queue.ClaimAsync(
            fixture.WorkerId,
            capabilities: [fixture.Tag],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(claimed);
        Assert.Equal(JobType.Build, claimed[0].Type);
    }

    [Fact]
    public async Task OnlyOneDispatcherHoldsTheAdvisoryLock()
    {
        await using var first = await QueueFixture.CreateAsync();
        if (first is null)
        {
            return;
        }

        await using var second = await QueueFixture.CreateAsync();
        Assert.NotNull(second);

        var key = Random.Shared.NextInt64();

        Assert.True(await first.Queue.TryAcquireAdvisoryLockAsync(key, TestContext.Current.CancellationToken));
        Assert.False(await second.Queue.TryAcquireAdvisoryLockAsync(key, TestContext.Current.CancellationToken));
        Assert.True(await first.Queue.ReleaseAdvisoryLockAsync(key, TestContext.Current.CancellationToken));
        Assert.True(await second.Queue.TryAcquireAdvisoryLockAsync(key, TestContext.Current.CancellationToken));
        Assert.True(await second.Queue.ReleaseAdvisoryLockAsync(key, TestContext.Current.CancellationToken));
    }

    private sealed class QueueFixture : IAsyncDisposable
    {
        private QueueFixture(CharterDbContext db, string tag)
        {
            Db = db;
            Queue = new JobQueue(db);
            Tag = tag;
            WorkerId = $"worker-{tag}";
        }

        public CharterDbContext Db { get; }

        public JobQueue Queue { get; }

        /// <summary>Namespaces this test's jobs so a shared database stays usable.</summary>
        public string Tag { get; }

        public string WorkerId { get; }

        /// <summary>
        /// Returns null — and the caller returns green — when no test database is configured.
        /// </summary>
        public static async Task<QueueFixture?> CreateAsync()
        {
            var url = Environment.GetEnvironmentVariable(DatabaseUrlVariable);
            if (string.IsNullOrWhiteSpace(url))
            {
                Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the queue integration tests.");
                return null;
            }

            var options = new DbContextOptionsBuilder<CharterDbContext>();
            DataServiceCollectionExtensions.ConfigureNpgsql(options, DatabaseUrl.ToNpgsql(url));

            var db = new CharterDbContext(options.Options);
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

            return new QueueFixture(db, $"t{Guid.CreateVersion7():N}");
        }

        public async ValueTask DisposeAsync() => await Db.DisposeAsync();
    }
}
