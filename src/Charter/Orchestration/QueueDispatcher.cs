using Charter.Data;
using Charter.Domain;
using Charter.Runners;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Charter.Orchestration;

/// <summary>
/// The queue dispatcher of section 2.1: claims work from Postgres and hands it to a handler.
/// </summary>
/// <remarks>
/// <para>
/// Three of section 2.3's constraints meet here. Work is claimed with
/// <c>SELECT ... FOR UPDATE SKIP LOCKED</c> under a lease, so a replica that dies returns its work
/// automatically. One replica at a time holds <c>pg_try_advisory_lock</c>, so scaling out does not
/// double-dispatch. And nothing about a claim is remembered in this process: the job row is the
/// state, and a restart re-reads it.
/// </para>
/// <para>
/// Claims are filtered by the capabilities the registered runners actually advertise (section 27.3).
/// That is what makes "a session with no eligible runner queues with a clear explanation rather than
/// failing" the queue's own behaviour: a job requiring macOS is never claimed by a control plane with
/// only a Linux backend, so it simply waits, while <see cref="SessionOrchestrator"/> writes the
/// explanation onto the session so the requester and the engineer can both see why.
/// </para>
/// </remarks>
public sealed class QueueDispatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRunnerRegistry _registry;
    private readonly OrchestrationOptions _options;
    private readonly ILogger<QueueDispatcher> _logger;

    private DispatcherLease? _lease;

    public QueueDispatcher(
        IServiceScopeFactory scopeFactory,
        IRunnerRegistry registry,
        OrchestrationOptions options,
        ILogger<QueueDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _registry = registry;
        _options = options;
        _logger = logger;
    }

    /// <summary>True while this replica is the one dispatching.</summary>
    public bool IsLeader => _lease?.IsHeld == true;

    /// <summary>Takes the advisory lock if it is free. Returns whether this replica now leads.</summary>
    public async Task<bool> TryBecomeLeaderAsync(CancellationToken cancellationToken = default)
    {
        if (IsLeader)
        {
            return true;
        }

        _lease = await DispatcherLease.TryAcquireAsync(_scopeFactory, _options.DispatcherLockKey, cancellationToken);

        if (_lease is not null)
        {
            _logger.LogInformation(
                "Dispatcher {WorkerId} holds the advisory lock and is dispatching",
                _options.WorkerId);
        }

        return IsLeader;
    }

    /// <summary>
    /// Claims one batch and runs it. Public so a test can drive a full cycle deterministically rather
    /// than waiting on a timer.
    /// </summary>
    /// <returns>How many jobs were claimed.</returns>
    public async Task<int> DispatchOnceAsync(CancellationToken cancellationToken = default)
    {
        var advertised = await _registry.AdvertisedCapabilitiesAsync(cancellationToken);

        // The other half of the section 2.2 boundary. A row carrying the execution plane's routing
        // marker belongs to a Charter Agent, and dispatching it here would run the session a second
        // time. AgentRunner.DescribeAsync already keeps the marker out of what it advertises, but the
        // union is taken across every enabled backend and a runner's capabilities can come from
        // operator configuration — so the claimant drops it rather than trusting every backend to.
        var capabilities = advertised
            .Where(capability => !string.Equals(
                capability,
                Runners.Agent.AgentRunner.ClaimCapability,
                StringComparison.Ordinal))
            .ToArray();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();

        var claimed = await queue.ClaimAsync(
            _options.WorkerId,
            _options.Lease,
            _options.BatchSize,
            capabilities,
            cancellationToken: cancellationToken);

        foreach (var job in claimed)
        {
            await RunAsync(job, queue, cancellationToken);
        }

        return claimed.Count;
    }

    /// <summary>Returns work whose lease has lapsed, at startup and on every cycle (section 33.4).</summary>
    public async Task<int> ReclaimExpiredLeasesAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();

        var released = await queue.ReleaseExpiredLeasesAsync(cancellationToken: cancellationToken);

        if (released > 0)
        {
            _logger.LogInformation("Returned {Count} job(s) whose lease had expired to the queue", released);
        }

        return released;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await TryBecomeLeaderAsync(stoppingToken))
                {
                    await Task.Delay(_options.LockRetryInterval, stoppingToken);
                    continue;
                }

                await ReclaimExpiredLeasesAsync(stoppingToken);
                var claimed = await DispatchOnceAsync(stoppingToken);

                // A full batch probably means there is more waiting; an empty one means there is not.
                if (claimed < _options.BatchSize)
                {
                    await Task.Delay(_options.PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "The queue dispatcher cycle failed; retrying");
                await Delay(_options.LockRetryInterval, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Section 31: drain in-flight work, release advisory locks, mark claimed jobs for retry.
    /// </summary>
    /// <remarks>
    /// The base implementation signals the stopping token and waits for
    /// <see cref="ExecuteAsync"/> to unwind, which is the "drain" half. What is left is to hand back
    /// what this worker still holds rather than making the queue wait out a five-minute lease, and to
    /// let go of the advisory lock so a surviving replica leads immediately instead of on its next
    /// retry.
    /// </remarks>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();

            var released = await queue.ReleaseWorkerClaimsAsync(
                _options.WorkerId,
                cancellationToken: cancellationToken);

            if (released > 0)
            {
                _logger.LogInformation(
                    "Returned {Count} claimed job(s) to the queue on shutdown",
                    released);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not hand back claimed jobs on shutdown");
        }

        if (_lease is not null)
        {
            await _lease.DisposeAsync();
            _lease = null;
        }
    }

    private async Task RunAsync(ClaimedJob job, JobQueue queue, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        var handler = scope.ServiceProvider
            .GetServices<IQueuedJobHandler>()
            .FirstOrDefault(candidate => candidate.Type == job.Type);

        if (handler is null)
        {
            await DeferAsync(
                job,
                queue,
                $"No handler is registered for {job.Type} jobs on this instance.",
                _options.LockRetryInterval,
                cancellationToken);
            return;
        }

        JobHandlingResult result;

        try
        {
            result = await handler.HandleAsync(job, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Handling {JobType} job {JobId} threw", job.Type, job.Id);
            result = JobHandlingResult.Failed(exception.Message);
        }

        switch (result.Handling)
        {
            case JobHandling.Completed:
                await queue.CompleteAsync(job.Id, _options.WorkerId, cancellationToken: cancellationToken);
                break;

            case JobHandling.Deferred:
                await DeferAsync(
                    job,
                    queue,
                    result.Reason ?? "Not ready yet.",
                    result.Delay ?? _options.LockRetryInterval,
                    cancellationToken);
                break;

            default:
                await queue.FailAsync(
                    job.Id,
                    _options.WorkerId,
                    result.Reason ?? "The job failed.",
                    _options.RetryDelay,
                    cancellationToken: cancellationToken);
                break;
        }
    }

    /// <summary>
    /// Puts a job back without burning an attempt.
    /// </summary>
    /// <remarks>
    /// The queue's own <c>FailAsync</c> counts against <c>max_attempts</c>, which is right for work
    /// that went wrong and wrong for work that is merely waiting — a session with no eligible runner
    /// must queue, not fail after three polls (section 27.3). Re-enqueueing an identical job and
    /// completing the claim expresses "later" on a queue that has no other way to say it. The new job
    /// is enqueued first: a crash between the two leaves a duplicate, which the dispatch claim in
    /// <see cref="SessionCoordinator"/> makes harmless, whereas the other order would lose the work.
    /// </remarks>
    private async Task DeferAsync(
        ClaimedJob job,
        JobQueue queue,
        string reason,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Deferring {JobType} job {JobId} for {Delay}: {Reason}", job.Type, job.Id, delay, reason);

        await queue.EnqueueAsync(
            job.Type,
            job.Payload,
            maxAttempts: job.MaxAttempts,
            requiredCapabilities: job.RequiredCapabilities,
            availableAt: DateTimeOffset.UtcNow + delay,
            cancellationToken: cancellationToken);

        await queue.CompleteAsync(job.Id, _options.WorkerId, cancellationToken: cancellationToken);
    }

    private static async Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
