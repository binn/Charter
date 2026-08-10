using Charter.Data;
using Charter.Domain;
using Microsoft.Extensions.Logging;

namespace Charter.Orchestration;

/// <summary>What a handler decided about a claimed job.</summary>
public enum JobHandling
{
    /// <summary>Done. The claim is completed and the job will not be offered again.</summary>
    Completed,

    /// <summary>Not done. The job returns to the queue until its attempts are spent (section 33.4).</summary>
    Failed,

    /// <summary>
    /// Not now. The job goes back to the queue after a delay without burning an attempt, which is how
    /// section 27.3's "queues rather than fails" is expressed on the queue.
    /// </summary>
    Deferred,
}

/// <summary>The outcome, with the delay a deferral waits and the reason a failure records.</summary>
public sealed record JobHandlingResult(JobHandling Handling, string? Reason = null, TimeSpan? Delay = null)
{
    public static JobHandlingResult Completed { get; } = new(JobHandling.Completed);

    public static JobHandlingResult Failed(string reason) => new(JobHandling.Failed, reason);

    public static JobHandlingResult Deferred(string reason, TimeSpan delay)
        => new(JobHandling.Deferred, reason, delay);
}

/// <summary>
/// A handler for one <see cref="JobType"/>, resolved from the container by the dispatcher.
/// </summary>
/// <remarks>
/// <para>
/// The extension point the rest of Charter plugs into. The dispatcher owns claiming, leases, the
/// advisory lock and graceful shutdown — the parts that are the same whatever the work is — and a
/// handler owns one kind of work. Repo onboarding registers handlers for <see cref="JobType.Recon"/>
/// and <see cref="JobType.SmokeTest"/> (section 9); the build handler below ships here.
/// </para>
/// <para>
/// A job whose type has no registered handler is <em>deferred</em>, never completed and never failed.
/// The alternative — one subsystem's dispatcher quietly consuming another's work because it claimed
/// it first — is a bug that looks exactly like a job queue that loses jobs.
/// </para>
/// </remarks>
public interface IQueuedJobHandler
{
    /// <summary>The job type this handles.</summary>
    JobType Type { get; }

    /// <summary>
    /// Does the work. Must be safe to run twice: a handler that completed the work and then lost its
    /// lease before completing the claim will be run again by whoever picks the job up.
    /// </summary>
    Task<JobHandlingResult> HandleAsync(ClaimedJob job, CancellationToken cancellationToken);
}

/// <summary>Runs a <see cref="JobType.Build"/> job by dispatching its session to a backend.</summary>
public sealed class BuildJobHandler : IQueuedJobHandler
{
    private readonly SessionCoordinator _coordinator;
    private readonly OrchestrationOptions _options;
    private readonly ILogger<BuildJobHandler> _logger;

    public BuildJobHandler(
        SessionCoordinator coordinator,
        OrchestrationOptions options,
        ILogger<BuildJobHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _coordinator = coordinator;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public JobType Type => JobType.Build;

    /// <inheritdoc />
    public async Task<JobHandlingResult> HandleAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        var payload = BuildJobPayload.TryParse(job.Payload);

        if (payload is null)
        {
            // The other shape: an approved specification with no session yet (section 7.5). Turning
            // it into one is the step between the spend gate and the execution plane, and it happens
            // here rather than at approval so that one durable row — this job — carries the whole of
            // it across a restart (section 2.3).
            if (SpecBuildPayload.TryParse(job.Payload) is not { } approved)
            {
                // Unparseable payloads never become parseable. Failing is right; retrying is not, so
                // the message says so and the attempts run out quickly.
                return JobHandlingResult.Failed(
                    "The build job's payload names neither a session nor a specification, so there is "
                    + "nothing to dispatch.");
            }

            var materialization = await _coordinator.EnsureSessionAsync(approved, cancellationToken);

            if (materialization.Session is null)
            {
                return materialization.WaitingForApproval
                    ? JobHandlingResult.Deferred(
                        materialization.Explanation ?? "This is waiting on somebody to approve it.",
                        _options.LockRetryInterval)
                    : JobHandlingResult.Failed(
                        materialization.Explanation ?? "The specification could not become a session.");
            }

            _logger.LogInformation(
                "Build job {JobId} for specification {SpecId} resolved to session {SessionId} ({Origin})",
                job.Id,
                approved.SpecId,
                materialization.Session.Id,
                materialization.Created ? "created" : "already existed");

            payload = new BuildJobPayload { SessionId = materialization.Session.Id };
        }

        var outcome = await _coordinator.DispatchAsync(payload, cancellationToken);

        _logger.LogInformation(
            "Build job {JobId} for session {SessionId}: {Decision}",
            job.Id,
            payload.SessionId,
            outcome.Decision);

        return outcome.Decision switch
        {
            DispatchDecision.Dispatched => JobHandlingResult.Completed,
            DispatchDecision.AlreadyDispatched => JobHandlingResult.Completed,
            DispatchDecision.Skipped => JobHandlingResult.Completed,
            DispatchDecision.Queued => JobHandlingResult.Deferred(
                outcome.Explanation ?? "No runner is available yet.",
                _options.LockRetryInterval),
            _ => JobHandlingResult.Failed(outcome.Explanation ?? "The runner refused the dispatch."),
        };
    }
}
