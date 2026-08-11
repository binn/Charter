using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Charter.Orchestration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Charter.Updates;

/// <summary>
/// The daily release check, run as a job on the section 2.3 queue (section 28).
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="System.Threading.Timer"/> would have been shorter and wrong. The container restarts,
/// and scaling to two replicas is supported, so the schedule has to survive both: it lives as a
/// pending row claimed under a lease with <c>FOR UPDATE SKIP LOCKED</c>, which means exactly one
/// replica runs each check and a replica that dies mid-check hands it back.
/// </para>
/// <para>
/// The handler is also the scheduler. Completing a check enqueues the next one, dated a day out with
/// jitter, carrying the result as its payload — so the queue holds both "when to look again" and
/// "what we last saw" in one durable row, and a restart resumes from it without anything having been
/// remembered in process.
/// </para>
/// <para>
/// Nothing here fails. Offline, air-gapped and rate-limited instances complete the job, keep the
/// previous result, and say so at debug level; section 28 forbids the daily error that teaches an
/// operator to stop reading logs. A failure would also burn an attempt and, three days later, strand
/// the schedule in <see cref="JobStatus.Failed"/> with nothing to re-arm it.
/// </para>
/// </remarks>
public sealed class UpdateCheckJobHandler : IQueuedJobHandler
{
    private readonly IReleaseSource _source;
    private readonly UpdateCheckConfig _config;
    private readonly UpdateCheckOptions _options;
    private readonly JobQueue _queue;
    private readonly CharterDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<UpdateCheckJobHandler> _logger;

    public UpdateCheckJobHandler(
        IReleaseSource source,
        UpdateCheckConfig config,
        UpdateCheckOptions options,
        JobQueue queue,
        CharterDbContext db,
        TimeProvider clock,
        ILogger<UpdateCheckJobHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _source = source;
        _config = config;
        _options = options;
        _queue = queue;
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public JobType Type => JobType.UpdateCheck;

    /// <inheritdoc />
    public async Task<JobHandlingResult> HandleAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        var now = _clock.GetUtcNow();
        var previous = UpdateStatus.TryParse(job.Payload);

        var releases = await _source.ListAsync(cancellationToken);
        var status = Evaluate(previous, releases, now);

        Announce(previous, status);

        // Converge on one pending check. Two replicas seeding at the same instant, or a deferral that
        // crossed a completion, can leave two rows; whichever runs first cancels the others, so the
        // schedule settles back to a single row without anybody holding a lock to prevent it.
        await CancelOtherPendingChecksAsync(now, cancellationToken);

        await _queue.EnqueueAsync(
            JobType.UpdateCheck,
            status.ToJson(),
            // Carried rather than cleared, exactly as a deferral carries them: whatever constrained
            // this check constrains its successor. In production that is the empty set — the check
            // runs on the control plane and needs nothing from a runner.
            requiredCapabilities: job.RequiredCapabilities,
            availableAt: now + NextInterval(),
            now: now,
            cancellationToken: cancellationToken);

        return JobHandlingResult.Completed;
    }

    /// <summary>
    /// Decides what the instance now knows, given what it knew and what GitHub said.
    /// </summary>
    /// <remarks>
    /// Internal and pure so the whole matrix — offline, unparseable build version, prerelease channel,
    /// a security release, a release older than the running build — is a table test rather than six
    /// integration runs.
    /// </remarks>
    internal UpdateStatus Evaluate(
        UpdateStatus? previous,
        IReadOnlyList<Release>? releases,
        DateTimeOffset now)
    {
        var channel = _config.Channel;
        var current = _options.CurrentVersion;

        if (releases is null)
        {
            // Could not look. Keep what was known rather than reporting "up to date", which would be
            // a claim this check has not earned.
            return previous?.CarriedForward(channel, current) ?? UpdateStatus.Unknown(channel, current);
        }

        var running = ReleaseVersion.TryParse(current);

        if (running is null)
        {
            // A build with no usable version - a source checkout, or a fork that overrode the build
            // property with something that is not a version. Nothing can be compared against it, and
            // guessing would announce an "update" to every such instance forever.
            _logger.LogDebug(
                "The running build version {Version} is not a semantic version, so no comparison is possible",
                current);
            return previous?.CarriedForward(channel, current) ?? UpdateStatus.Unknown(channel, current);
        }

        Release? newest = null;

        foreach (var release in releases)
        {
            // Section 4.2: the stable channel is never offered a prerelease. The prerelease channel is
            // offered both, because a stable release supersedes the prerelease that led to it.
            if (release.IsPrerelease && channel != UpdateChannel.Prerelease)
            {
                continue;
            }

            if (newest is null || newest.Version.CompareTo(release.Version) < 0)
            {
                newest = release;
            }
        }

        return newest is not null && running.IsOlderThan(newest.Version)
            ? UpdateStatus.Available(channel, current, newest, now)
            : UpdateStatus.UpToDate(channel, current, now);
    }

    /// <summary>A day out, plus up to <see cref="UpdateCheckOptions.Jitter"/>.</summary>
    private TimeSpan NextInterval()
        => _options.Interval + (_options.Jitter * Random.Shared.NextDouble());

    /// <summary>
    /// Says it once per release rather than once per day.
    /// </summary>
    /// <remarks>
    /// Section 28 puts the notice in front of admins and engineers in the UI; this is the operator's
    /// copy of it, in the logs they already watch. Repeating it daily would make it furniture, so the
    /// level drops to debug once the same version has already been announced.
    /// </remarks>
    private void Announce(UpdateStatus? previous, UpdateStatus status)
    {
        if (!status.UpdateAvailable)
        {
            return;
        }

        var alreadySaid = string.Equals(previous?.LatestVersion, status.LatestVersion, StringComparison.Ordinal);
        var migrations = status.Migrations
            ? " This release includes schema migrations; take a backup before upgrading."
            : string.Empty;

        if (status.Security && !alreadySaid)
        {
            _logger.LogWarning(
                "Charter {Latest} is a security release and this instance runs {Current}. {Url}{Migrations}",
                status.LatestVersion,
                status.CurrentVersion,
                status.ReleaseUrl,
                migrations);
            return;
        }

        if (alreadySaid)
        {
            _logger.LogDebug(
                "Charter {Latest} is still the newest release; this instance runs {Current}",
                status.LatestVersion,
                status.CurrentVersion);
            return;
        }

        _logger.LogInformation(
            "Charter {Latest} is available and this instance runs {Current}. {Url}{Migrations}",
            status.LatestVersion,
            status.CurrentVersion,
            status.ReleaseUrl,
            migrations);
    }

    private async Task CancelOtherPendingChecksAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var duplicates = await _db.Jobs
            .Where(candidate => candidate.Type == JobType.UpdateCheck && candidate.Status == JobStatus.Pending)
            .ToListAsync(cancellationToken);

        if (duplicates.Count == 0)
        {
            return;
        }

        foreach (var duplicate in duplicates)
        {
            duplicate.Cancel(now);
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Somebody claimed one of them between the read and the write. It will run, find this
            // job's successor pending, and cancel that instead - the same convergence, one cycle later.
            _logger.LogDebug("A duplicate release check was claimed while it was being cancelled");
        }
    }
}

/// <summary>
/// What runs in place of the check when <c>CHARTER_UPDATE_CHECK=false</c> (sections 4.2, 28).
/// </summary>
/// <remarks>
/// A handler rather than no handler, and that is the whole reason it exists. The dispatcher
/// <em>defers</em> a job whose type nothing handles, re-enqueueing it every retry interval forever, so
/// an instance that turned the check off after having it on would spend the rest of its life churning
/// a row it will never run. This drains the schedule once and says so at debug level.
/// </remarks>
public sealed class DisabledUpdateCheckJobHandler : IQueuedJobHandler
{
    private readonly ILogger<DisabledUpdateCheckJobHandler> _logger;

    public DisabledUpdateCheckJobHandler(ILogger<DisabledUpdateCheckJobHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public JobType Type => JobType.UpdateCheck;

    /// <inheritdoc />
    public Task<JobHandlingResult> HandleAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        _logger.LogDebug(
            "Discarding queued release check {JobId}: the update check is off on this instance",
            job.Id);

        return Task.FromResult(JobHandlingResult.Completed);
    }
}
