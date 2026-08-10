namespace Charter.Domain;

/// <summary>The work the queue carries. Section 2.3: Postgres is the queue, and there is no Redis.</summary>
public enum JobType
{
    /// <summary>Read-only agent run over a repository during onboarding (section 9).</summary>
    Recon,

    /// <summary>The canned trivial request that validates all six integration points (section 9).</summary>
    SmokeTest,

    /// <summary>A refinement turn against an untrusted request (section 10).</summary>
    Refine,

    /// <summary>The agent run itself.</summary>
    Build,

    /// <summary>Teaching generation, billed to its own budget line (section 13).</summary>
    Teach,

    /// <summary>The engineer recap, posted as a pull request comment (section 14).</summary>
    Recap,

    /// <summary>New project scaffolding from the org's standards (section 26.4).</summary>
    Scaffold,

    /// <summary>Retention pruning of the event table and expired artifacts (sections 20, 27.5).</summary>
    Prune,

    /// <summary>The daily release check (section 28).</summary>
    UpdateCheck,
}

/// <summary>Queue states. A job is claimable only while <see cref="JobStatus.Pending"/>.</summary>
public enum JobStatus
{
    Pending,

    /// <summary>Held under a lease. A crashed worker's claim expires and the job returns to pending.</summary>
    Claimed,

    Completed,

    /// <summary>Attempts exhausted. Terminal; the dispatcher will not pick it up again.</summary>
    Failed,

    Cancelled,
}

/// <summary>
/// A unit of queued work (section 5) — the <c>FOR UPDATE SKIP LOCKED</c> queue of section 2.3.
/// </summary>
/// <remarks>
/// A job carries no foreign keys on purpose. The queue must stay claimable, releasable and prunable
/// without reference to anything else in the schema, and the payload carries whichever identifiers
/// the handler needs.
/// <para>
/// Claims carry a lease with a TTL, renewed by heartbeat, so a crashed agent's jobs return to the
/// queue automatically (section 33.4). <see cref="Charter.Data.JobQueue"/> owns every state
/// transition after enqueue, in raw SQL, because EF cannot express <c>SKIP LOCKED</c>.
/// </para>
/// </remarks>
public sealed class Job : IVersionedEntity
{
    /// <summary>Conservative by default; a worker renews the lease by heartbeat (section 33.4).</summary>
    public static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(5);

    public const int DefaultMaxAttempts = 3;

    private Job()
    {
    }

    private Job(
        Guid id,
        JobType type,
        string payload,
        int maxAttempts,
        int priority,
        IReadOnlyList<string> requiredCapabilities,
        DateTimeOffset availableAt,
        DateTimeOffset createdAt)
    {
        Id = id;
        Type = type;
        Payload = payload;
        Status = JobStatus.Pending;
        MaxAttempts = maxAttempts;
        Priority = priority;
        RequiredCapabilities = requiredCapabilities;
        AvailableAt = availableAt;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public JobType Type { get; private set; }

    /// <summary>jsonb.</summary>
    public string Payload { get; private set; } = "{}";

    public JobStatus Status { get; private set; }

    /// <summary>Not claimable before this instant. Retry backoff moves it forward.</summary>
    public DateTimeOffset AvailableAt { get; private set; }

    public DateTimeOffset? ClaimedAt { get; private set; }

    /// <summary>The worker's own identifier — an agent id, or a control-plane instance id.</summary>
    public string? ClaimedBy { get; private set; }

    /// <summary>When the lease lapses. Past this, the job is returned to the queue.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; private set; }

    public int Attempts { get; private set; }

    public int MaxAttempts { get; private set; }

    /// <summary>Higher is claimed first.</summary>
    public int Priority { get; private set; }

    /// <summary>
    /// Section 27.3: runners advertise capabilities and the dispatcher matches. Claims are filtered
    /// by this, so an agent only ever sees jobs it can actually run.
    /// </summary>
    public IReadOnlyList<string> RequiredCapabilities { get; private set; } = [];

    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public int Version { get; private set; }

    public bool IsLeaseExpiredAt(DateTimeOffset now)
        => Status == JobStatus.Claimed && LeaseExpiresAt is not null && LeaseExpiresAt <= now;

    public static Job Enqueue(
        JobType type,
        string payload,
        int priority = 0,
        int maxAttempts = DefaultMaxAttempts,
        IEnumerable<string>? requiredCapabilities = null,
        DateTimeOffset? availableAt = null,
        DateTimeOffset? now = null,
        Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        var createdAt = DomainTime.Resolve(now);

        return new Job(
            id ?? Guid.CreateVersion7(),
            type,
            payload,
            maxAttempts,
            priority,
            requiredCapabilities is null ? [] : [.. requiredCapabilities.Select(c => c.Trim()).Distinct().Order()],
            DomainTime.ResolveOptional(availableAt) ?? createdAt,
            createdAt);
    }

    /// <summary>
    /// Cancels a job that has not been claimed. In-flight work is stopped through the session, not
    /// here — the queue does not reach into a running runner.
    /// </summary>
    public void Cancel(DateTimeOffset? now = null)
    {
        if (Status != JobStatus.Pending)
        {
            throw new InvalidOperationException($"A {Status} job cannot be cancelled through the queue.");
        }

        Status = JobStatus.Cancelled;
        CompletedAt = DomainTime.Resolve(now);
        UpdatedAt = CompletedAt.Value;
    }

    void IVersionedEntity.NextVersion() => Version++;
}
