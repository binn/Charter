using System.Data;
using System.Data.Common;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Charter.Data;

/// <summary>A job handed to a worker under a lease.</summary>
/// <param name="Id">The job's identifier.</param>
/// <param name="Type">What kind of work this is.</param>
/// <param name="Payload">The job's jsonb payload, as JSON text.</param>
/// <param name="Attempts">Attempts so far, including this one.</param>
/// <param name="MaxAttempts">After this many attempts a failure is terminal.</param>
/// <param name="ClaimedBy">The worker that holds the claim.</param>
/// <param name="LeaseExpiresAt">When the claim lapses unless renewed by heartbeat.</param>
/// <param name="RequiredCapabilities">What the job needed from a runner (section 27.3).</param>
public sealed record ClaimedJob(
    Guid Id,
    JobType Type,
    string Payload,
    int Attempts,
    int MaxAttempts,
    string ClaimedBy,
    DateTimeOffset LeaseExpiresAt,
    IReadOnlyList<string> RequiredCapabilities);

/// <summary>
/// The Postgres job queue of section 2.3: claimed with <c>SELECT ... FOR UPDATE SKIP LOCKED</c>, no
/// Redis, and safe to run from several replicas at once.
/// </summary>
/// <remarks>
/// <para>
/// EF cannot express <c>SKIP LOCKED</c>, so claiming, completing, failing and releasing are raw SQL.
/// Every statement is a constant with bound parameters — no identifier or value is ever interpolated
/// into the text — and every one of them is a single statement, so a claim is atomic without an
/// explicit transaction.
/// </para>
/// <para>
/// Claims carry a lease with a TTL (section 33.4). A worker renews it by heartbeat; a worker that
/// dies stops renewing, and <see cref="ReleaseExpiredLeasesAsync"/> returns its jobs to the queue.
/// That is what makes the outbound-only Charter Agent safe: the control plane never needs to reach
/// the worker to find out that it is gone.
/// </para>
/// </remarks>
public sealed class JobQueue
{
    /// <summary>
    /// Claim: lock a bounded set of pending rows, skipping any another worker already holds, and
    /// stamp the lease in the same statement. <c>FOR UPDATE SKIP LOCKED</c> inside the CTE is what
    /// lets N workers poll the same queue without contending, and <c>RETURNING</c> means a claim is
    /// one round trip rather than a select followed by an update.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There are two capability tests here and they run in opposite directions, because they answer
    /// two different questions.
    /// </para>
    /// <para>
    /// <c>required_capabilities &lt;@ @capabilities</c> asks <em>can this worker run the job</em>
    /// (section 27.3). Containment is the right shape for that, and it is why a job requiring macOS
    /// is never handed to a Linux host. But the empty set is contained in everything, so this test
    /// alone says yes to every worker for every job that requires nothing — which is every control
    /// plane job Charter enqueues.
    /// </para>
    /// <para>
    /// <c>@requires = ANY(required_capabilities)</c> asks <em>was this job meant for this worker</em>
    /// (section 2.2). It is a positive requirement rather than a subset relation, so it cannot be
    /// satisfied by silence: a row that does not name the marker is invisible to a claimant that
    /// demands it, however capable that claimant is. The execution plane claims with the marker and
    /// therefore sees only rows written for it; the control plane claims without one and its own
    /// filter is unchanged.
    /// </para>
    /// </remarks>
    internal const string ClaimSql = """
        WITH claimable AS (
            SELECT id
            FROM jobs
            WHERE status = 'pending'
              AND available_at <= @now
              AND (@capabilities IS NULL OR required_capabilities <@ @capabilities)
              AND (@requires IS NULL OR @requires = ANY(required_capabilities))
            ORDER BY priority DESC, available_at, created_at
            FOR UPDATE SKIP LOCKED
            LIMIT @batch_size
        )
        UPDATE jobs AS j
        SET status = 'claimed',
            claimed_at = @now,
            claimed_by = @worker,
            lease_expires_at = @now + @lease,
            attempts = j.attempts + 1,
            updated_at = @now,
            version = j.version + 1
        FROM claimable AS c
        WHERE j.id = c.id
        RETURNING j.id, j.type, j.payload, j.attempts, j.max_attempts, j.claimed_by,
                  j.lease_expires_at, j.required_capabilities;
        """;

    /// <summary>Heartbeat. Extends the lease only while this worker still holds the claim.</summary>
    internal const string RenewLeaseSql = """
        UPDATE jobs
        SET lease_expires_at = @now + @lease,
            updated_at = @now,
            version = version + 1
        WHERE id = @id
          AND status = 'claimed'
          AND claimed_by = @worker;
        """;

    internal const string CompleteSql = """
        UPDATE jobs
        SET status = 'completed',
            completed_at = @now,
            updated_at = @now,
            lease_expires_at = NULL,
            last_error = NULL,
            version = version + 1
        WHERE id = @id
          AND status = 'claimed'
          AND claimed_by = @worker;
        """;

    /// <summary>
    /// Failure is a retry until the attempts are spent, and terminal after that. The decision is
    /// made in the statement rather than in the worker, so two workers cannot disagree about it.
    /// </summary>
    internal const string FailSql = """
        UPDATE jobs
        SET status = CASE WHEN attempts >= max_attempts THEN 'failed' ELSE 'pending' END,
            available_at = CASE WHEN attempts >= max_attempts THEN available_at ELSE @retry_at END,
            completed_at = CASE WHEN attempts >= max_attempts THEN @now ELSE NULL END,
            claimed_at = NULL,
            claimed_by = NULL,
            lease_expires_at = NULL,
            last_error = @error,
            updated_at = @now,
            version = version + 1
        WHERE id = @id
          AND status = 'claimed'
          AND claimed_by = @worker
        RETURNING status;
        """;

    /// <summary>
    /// Lease expiry (section 33.4). A crashed worker's jobs go back to pending; jobs that have
    /// already burned their attempts go to failed rather than looping forever.
    /// </summary>
    internal const string ReleaseExpiredLeasesSql = """
        UPDATE jobs
        SET status = CASE WHEN attempts >= max_attempts THEN 'failed' ELSE 'pending' END,
            completed_at = CASE WHEN attempts >= max_attempts THEN @now ELSE NULL END,
            available_at = CASE WHEN attempts >= max_attempts THEN available_at ELSE @now END,
            claimed_at = NULL,
            claimed_by = NULL,
            lease_expires_at = NULL,
            last_error = 'The worker holding this job stopped renewing its lease.',
            updated_at = @now,
            version = version + 1
        WHERE status = 'claimed'
          AND lease_expires_at IS NOT NULL
          AND lease_expires_at <= @now;
        """;

    /// <summary>
    /// Graceful shutdown (section 31): hand back what this instance still holds instead of making
    /// the queue wait out the lease.
    /// </summary>
    internal const string ReleaseWorkerSql = """
        UPDATE jobs
        SET status = 'pending',
            available_at = @now,
            claimed_at = NULL,
            claimed_by = NULL,
            lease_expires_at = NULL,
            updated_at = @now,
            version = version + 1
        WHERE status = 'claimed'
          AND claimed_by = @worker;
        """;

    /// <summary>
    /// Section 2.3: one dispatcher at a time, so scaling to two replicas does not double-dispatch.
    /// The lock is held for the life of the session and released by disconnecting, which is exactly
    /// the behaviour wanted when a container is killed mid-dispatch.
    /// </summary>
    internal const string TryAdvisoryLockSql = "SELECT pg_try_advisory_lock(@key);";

    internal const string AdvisoryUnlockSql = "SELECT pg_advisory_unlock(@key);";

    private readonly CharterDbContext _db;

    public JobQueue(CharterDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <summary>Adds a job. Enqueue goes through EF: it is an ordinary insert with no locking.</summary>
    public async Task<Job> EnqueueAsync(
        JobType type,
        string payload,
        int priority = 0,
        int maxAttempts = Job.DefaultMaxAttempts,
        IEnumerable<string>? requiredCapabilities = null,
        DateTimeOffset? availableAt = null,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        var job = Job.Enqueue(type, payload, priority, maxAttempts, requiredCapabilities, availableAt, now);

        _db.Jobs.Add(job);
        await _db.SaveChangesAsync(cancellationToken);

        return job;
    }

    /// <summary>
    /// Claims up to <paramref name="batchSize"/> jobs for <paramref name="workerId"/>.
    /// </summary>
    /// <param name="workerId">The claiming worker's identity — an agent id or a replica id.</param>
    /// <param name="lease">
    /// How long the claim survives without a heartbeat. Defaults to <see cref="Job.DefaultLease"/>.
    /// </param>
    /// <param name="batchSize">The most jobs to take in one round trip.</param>
    /// <param name="capabilities">
    /// What this worker can do (section 27.3). <see langword="null"/> disables capability filtering
    /// for an in-process dispatcher; an empty set claims only jobs that require nothing.
    /// </param>
    /// <param name="requires">
    /// A capability a row must <em>name</em> before this worker will take it (section 2.2). Unlike
    /// <paramref name="capabilities"/>, which is a containment test a job requiring nothing always
    /// passes, this is a positive requirement: pass <c>charter_agent</c> and only rows enqueued for
    /// the execution plane are claimed. <see langword="null"/> leaves the claim unrestricted, which
    /// is what the control plane's own dispatcher wants.
    /// </param>
    /// <param name="now">The instant to evaluate against. Defaults to now, in UTC.</param>
    /// <param name="cancellationToken">Cancels the claim.</param>
    public async Task<IReadOnlyList<ClaimedJob>> ClaimAsync(
        string workerId,
        TimeSpan? lease = null,
        int batchSize = 1,
        IEnumerable<string>? capabilities = null,
        string? requires = null,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        var instant = Normalize(now);
        var ttl = lease ?? Job.DefaultLease;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ttl.Ticks);

        var advertised = capabilities?.Select(capability => capability.Trim()).Distinct().Order().ToArray();

        await using var command = await CreateCommandAsync(ClaimSql, cancellationToken);
        AddParameter(command, "now", NpgsqlDbType.TimestampTz, instant);
        AddParameter(command, "worker", NpgsqlDbType.Varchar, workerId);
        AddParameter(command, "lease", NpgsqlDbType.Interval, ttl);
        AddParameter(command, "batch_size", NpgsqlDbType.Integer, batchSize);
        AddParameter(command, "capabilities", NpgsqlDbType.Array | NpgsqlDbType.Text, advertised);
        AddParameter(
            command,
            "requires",
            NpgsqlDbType.Text,
            string.IsNullOrWhiteSpace(requires) ? null : requires.Trim());

        var claimed = new List<ClaimedJob>(batchSize);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            claimed.Add(new ClaimedJob(
                reader.GetGuid(0),
                EnumDbNames<JobType>.FromDb(reader.GetString(1)),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetFieldValue<string[]>(7)));
        }

        return claimed;
    }

    /// <summary>Extends the lease. Returns false when the claim has already been lost.</summary>
    public async Task<bool> RenewLeaseAsync(
        Guid jobId,
        string workerId,
        TimeSpan? lease = null,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        await using var command = await CreateCommandAsync(RenewLeaseSql, cancellationToken);
        AddParameter(command, "id", NpgsqlDbType.Uuid, jobId);
        AddParameter(command, "worker", NpgsqlDbType.Varchar, workerId);
        AddParameter(command, "now", NpgsqlDbType.TimestampTz, Normalize(now));
        AddParameter(command, "lease", NpgsqlDbType.Interval, lease ?? Job.DefaultLease);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>
    /// Marks a job done. Returns false if this worker no longer holds the claim, which is the
    /// signal that its lease expired and somebody else has the job.
    /// </summary>
    public async Task<bool> CompleteAsync(
        Guid jobId,
        string workerId,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        await using var command = await CreateCommandAsync(CompleteSql, cancellationToken);
        AddParameter(command, "id", NpgsqlDbType.Uuid, jobId);
        AddParameter(command, "worker", NpgsqlDbType.Varchar, workerId);
        AddParameter(command, "now", NpgsqlDbType.TimestampTz, Normalize(now));

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>
    /// Records a failure. The job returns to the queue after <paramref name="retryDelay"/> until its
    /// attempts are spent, then becomes terminal.
    /// </summary>
    /// <returns>
    /// The resulting status, or <see langword="null"/> when this worker no longer held the claim.
    /// </returns>
    public async Task<JobStatus?> FailAsync(
        Guid jobId,
        string workerId,
        string error,
        TimeSpan? retryDelay = null,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        var instant = Normalize(now);

        await using var command = await CreateCommandAsync(FailSql, cancellationToken);
        AddParameter(command, "id", NpgsqlDbType.Uuid, jobId);
        AddParameter(command, "worker", NpgsqlDbType.Varchar, workerId);
        AddParameter(command, "error", NpgsqlDbType.Text, Truncate(error, 4000));
        AddParameter(command, "now", NpgsqlDbType.TimestampTz, instant);
        AddParameter(command, "retry_at", NpgsqlDbType.TimestampTz, instant + (retryDelay ?? TimeSpan.Zero));

        var status = await command.ExecuteScalarAsync(cancellationToken);

        return status is string name ? EnumDbNames<JobStatus>.FromDb(name) : null;
    }

    /// <summary>
    /// Returns jobs whose lease has lapsed to the queue (section 33.4). Run on an interval and at
    /// startup; it is the only thing that recovers work from a worker that never came back.
    /// </summary>
    /// <returns>How many jobs were released or failed.</returns>
    public async Task<int> ReleaseExpiredLeasesAsync(
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        await using var command = await CreateCommandAsync(ReleaseExpiredLeasesSql, cancellationToken);
        AddParameter(command, "now", NpgsqlDbType.TimestampTz, Normalize(now));

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Releases everything a named worker holds, for graceful shutdown (section 31).</summary>
    public async Task<int> ReleaseWorkerClaimsAsync(
        string workerId,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        await using var command = await CreateCommandAsync(ReleaseWorkerSql, cancellationToken);
        AddParameter(command, "worker", NpgsqlDbType.Varchar, workerId);
        AddParameter(command, "now", NpgsqlDbType.TimestampTz, Normalize(now));

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Takes the session-scoped advisory lock that keeps one dispatcher authoritative (section 2.3).
    /// </summary>
    public async Task<bool> TryAcquireAdvisoryLockAsync(long key, CancellationToken cancellationToken = default)
    {
        await using var command = await CreateCommandAsync(TryAdvisoryLockSql, cancellationToken);
        AddParameter(command, "key", NpgsqlDbType.Bigint, key);

        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    /// <summary>Releases an advisory lock taken by <see cref="TryAcquireAdvisoryLockAsync"/>.</summary>
    public async Task<bool> ReleaseAdvisoryLockAsync(long key, CancellationToken cancellationToken = default)
    {
        await using var command = await CreateCommandAsync(AdvisoryUnlockSql, cancellationToken);
        AddParameter(command, "key", NpgsqlDbType.Bigint, key);

        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static DateTimeOffset Normalize(DateTimeOffset? value) => (value ?? DateTimeOffset.UtcNow).ToUniversalTime();

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static void AddParameter(DbCommand command, string name, NpgsqlDbType type, object? value)
    {
        var parameter = new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value };
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// Builds a command on the context's own connection, enlisting in an ambient EF transaction when
    /// one is open so a caller can wrap a claim and its side effects together.
    /// </summary>
    private async Task<DbCommand> CreateCommandAsync(string sql, CancellationToken cancellationToken)
    {
        var connection = _db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await _db.Database.OpenConnectionAsync(cancellationToken);
        }

        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();

        return command;
    }
}
