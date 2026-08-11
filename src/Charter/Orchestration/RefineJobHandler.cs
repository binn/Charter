using System.Text.Json;
using Charter.Data;
using Charter.Domain;
using Charter.Models;
using Charter.Refinement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Charter.Orchestration;

/// <summary>
/// The payload of a <see cref="JobType.Refine"/> job.
/// </summary>
/// <remarks>
/// Three call sites write one of these: intake, which carries only the request; a reply, which
/// carries what the requester typed; and an approver sending a specification back, which carries what
/// they want changed. All three are the same turn from the refiner's side — untrusted text against a
/// conversation — so they are one payload and one handler rather than three.
/// </remarks>
public sealed record RefineJobPayload
{
    /// <summary>The request whose conversation this turn belongs to.</summary>
    public required Guid RequestId { get; init; }

    /// <summary>What the requester typed, when this turn came from a reply.</summary>
    public string? Body { get; init; }

    /// <summary>Which suggested answer they picked, when they picked one rather than typing.</summary>
    public string? ChoiceId { get; init; }

    /// <summary>What an approver wants changed, when a specification was sent back (section 7.5).</summary>
    public string? ChangesRequested { get; init; }

    /// <summary>The requester's words for this turn, or null when this is the opening turn.</summary>
    public string? Message
    {
        get
        {
            foreach (var candidate in new[] { Body, ChangesRequested, ChoiceId })
            {
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    return candidate.Trim();
                }
            }

            return null;
        }
    }

    /// <summary>Parses a payload in either the API's camelCase or the orchestrator's snake_case.</summary>
    public static RefineJobPayload? TryParse(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        try
        {
            using var document = JsonDocument.Parse(payload);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var root = document.RootElement;

            if (Read(root, "requestId", "request_id") is not { Length: > 0 } text
                || !Guid.TryParse(text, out var requestId))
            {
                return null;
            }

            return new RefineJobPayload
            {
                RequestId = requestId,
                Body = Read(root, "body", "body"),
                ChoiceId = Read(root, "choiceId", "choice_id"),
                ChangesRequested = Read(root, "changesRequested", "changes_requested"),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Read(JsonElement root, string camel, string snake)
    {
        foreach (var name in new[] { camel, snake })
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }
}

/// <summary>
/// Runs one refinement turn against an untrusted request (sections 10, 16).
/// </summary>
/// <remarks>
/// <para>
/// Intake queues one of these and, until now, nothing consumed it — so a filed request sat in
/// <c>Refining</c> forever while the dispatcher politely deferred the row. This is the consumer. It
/// owns the whole of one turn: the conversation it belongs to, the credential it spends, the refiner
/// call, and what the outcome does to the request's state.
/// </para>
/// <para>
/// <strong>Nothing it needs is held in memory.</strong> The conversation is rehydrated from
/// <see cref="ConversationRecord"/> on every turn and written back on every turn, because section 2.3
/// says a refinement conversation held in a field on a singleton is exactly the state a PaaS restart
/// destroys.
/// </para>
/// <para>
/// The turn is at-least-once, and the guard against replaying it is the transcript itself: a job
/// whose message is already the newest requester turn, with the refiner's answer after it, has
/// already run. That costs one read and saves an accidental second model call every time a lease
/// lapses.
/// </para>
/// </remarks>
public sealed class RefineJobHandler : IQueuedJobHandler
{
    private readonly CharterDbContext _db;
    private readonly ISpecRefiner _refiner;
    private readonly ICredentialResolver _credentials;
    private readonly RefinementOptions _refinement;
    private readonly SessionCoordinator _coordinator;
    private readonly IAutoDispatchGate _autoDispatch;
    private readonly OrchestrationOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<RefineJobHandler> _logger;
    private readonly IRefinementStream? _stream;

    public RefineJobHandler(
        CharterDbContext db,
        ISpecRefiner refiner,
        ICredentialResolver credentials,
        RefinementOptions refinement,
        SessionCoordinator coordinator,
        IAutoDispatchGate autoDispatch,
        OrchestrationOptions options,
        TimeProvider clock,
        ILogger<RefineJobHandler> logger,
        IRefinementStream? stream = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(refiner);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(refinement);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(autoDispatch);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _db = db;
        _refiner = refiner;
        _credentials = credentials;
        _refinement = refinement;
        _coordinator = coordinator;
        _autoDispatch = autoDispatch;
        _options = options;
        _clock = clock;
        _logger = logger;

        // Optional: a host that wired the execution plane without the API — a worker replica, a test —
        // still refines correctly and simply streams nothing. The rows are the record (section 2.3).
        _stream = stream;
    }

    /// <inheritdoc />
    public JobType Type => JobType.Refine;

    /// <inheritdoc />
    public async Task<JobHandlingResult> HandleAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (RefineJobPayload.TryParse(job.Payload) is not { } payload)
        {
            return JobHandlingResult.Failed(
                "The refine job's payload does not name a request, so there is nothing to refine.");
        }

        var request = await _db.Requests.FirstOrDefaultAsync(
            row => row.Id == payload.RequestId,
            cancellationToken);

        if (request is null)
        {
            return JobHandlingResult.Completed;
        }

        // Section 6: refinement runs before the spend gate and never after it. A request that has
        // moved on has already had this turn, or has had it overtaken.
        if (request.Status is not (RequestStatus.Draft or RequestStatus.Refining or RequestStatus.Rejected))
        {
            _logger.LogDebug(
                "Refine job {JobId} skipped: request {RequestId} is {Status}",
                job.Id,
                request.Id,
                request.Status);

            return JobHandlingResult.Completed;
        }

        var repo = await _db.Repos.AsNoTracking().FirstOrDefaultAsync(
            row => row.Id == request.RepoId,
            cancellationToken);

        if (repo is null)
        {
            return JobHandlingResult.Failed("The repository this request was filed against no longer exists.");
        }

        var record = await LoadConversationAsync(request, cancellationToken);
        var message = payload.Message ?? (record.Turns.Count == 0 ? request.RawText : null);

        if (string.IsNullOrWhiteSpace(message))
        {
            // An opening turn on a conversation that has already opened. Nothing was said.
            return JobHandlingResult.Completed;
        }

        if (AlreadyAnswered(record, message))
        {
            _logger.LogInformation(
                "Refine job {JobId} had already been applied to request {RequestId}; not spending a second turn",
                job.Id,
                request.Id);

            return JobHandlingResult.Completed;
        }

        var credential = await _credentials.ResolveAsync(
            new ModelCredentialQuery(
                _refinement.Model,
                request.RequesterId.ToString(),
                request.OrgId.ToString()),
            cancellationToken);

        if (credential.Credential is not { } resolved)
        {
            // Section 20b.3: exhausted means wait, not fail — but only where waiting produces a
            // credential. A provider that named a reset instant will come back on its own.
            if (credential.RecoversOnItsOwn)
            {
                return JobHandlingResult.Deferred(
                    "Every model credential is currently exhausted, so this refinement is waiting for capacity.",
                    Delay(credential.WaitingForCapacityUntil));
            }

            // Nothing configured, or nothing usable. Deferring here is what left a filed request in
            // Refining forever with nothing anywhere saying why — the worst outcome available, because
            // it tells the requester nothing and the operator nothing. Section 4.1's fail loudly, one
            // step later than startup.
            return await FailForCredentialAsync(request, record, credential, cancellationToken);
        }

        var conversation = ConversationRehydration.ToConversation(record);
        var context = RepoRefinementContext.For(repo);
        var now = _clock.GetUtcNow();

        RefinementResult result;

        try
        {
            result = await _refiner.AdvanceAsync(
                conversation,
                RequesterText.From(message),
                context,
                resolved.Credential,
                cancellationToken);
        }
        catch (ModelClientException exception)
        {
            await _credentials.ReportFailureAsync(resolved, exception, cancellationToken);

            // A provider that is down is not a request that is wrong. Retrying is the right answer,
            // and the queue's attempt budget is what stops it retrying forever.
            return JobHandlingResult.Failed($"The refinement model could not be reached: {exception.Message}");
        }

        var asked = record.AppendRequesterMessage(message, now);
        var answered = record.AppendCharterTurn(KindOf(result.Outcome), result.Outcome.RequesterMessage, now);

        var handled = await ApplyAsync(request, record, result, now, cancellationToken);

        // Section 11: "do stream something — a 5–20 minute silent gap reads as broken." The rows above
        // are the record and this is a courtesy on top of them (section 2.3), which is why it happens
        // after the commit and why nothing it does can fail the turn.
        if (_stream is not null)
        {
            await _stream.PublishAsync(
                new RefinementTurn
                {
                    Request = request,
                    Repo = repo,
                    Conversation = record,
                    Spec = await CurrentSpecAsync(request.Id, cancellationToken),
                    AppendedTurnIds = [asked.Id, answered.Id],
                },
                cancellationToken);
        }

        return handled;
    }

    /// <summary>What a requester reads when this instance cannot reach a model at all.</summary>
    /// <remarks>
    /// Section 11: one sentence, no stack trace, and nothing that reads as their mistake — a missing
    /// environment variable is the operator's problem and naming it here would put configuration in
    /// front of somebody who has no way to act on it (section 7.4). The actionable half goes to the
    /// log and to the job's recorded error, where the person who can fix it is looking.
    /// </remarks>
    internal const string CredentialFailureMessage =
        "Charter could not reach a model to work on this, so it has stopped rather than leaving you "
        + "waiting. Nothing you did caused it — this instance needs a model credential set up, and an "
        + "engineer has been told.";

    /// <summary>
    /// Ends the request loudly when no credential can be resolved and waiting will not produce one.
    /// </summary>
    /// <remarks>
    /// Section 6 puts this in <see cref="RequestStatus.Failed"/>: it is terminal, it is not the
    /// requester's fault, and its label — <em>this turned out to be bigger than expected, an engineer
    /// has been told</em> — is the accurate one. <see cref="RequestStatus.Rejected"/> would be wrong,
    /// because refinement never judged the request at all.
    /// </remarks>
    private async Task<JobHandlingResult> FailForCredentialAsync(
        Request request,
        ConversationRecord record,
        ModelCredentialResolution resolution,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();

        _logger.LogError(
            "Request {RequestId} cannot be refined: {Explanation}",
            request.Id,
            resolution.Explanation);

        record.AppendCharterTurn(ConversationTurnKind.Refusal, CredentialFailureMessage, now);
        request.TransitionTo(RequestStatus.Failed, now);

        await _db.SaveChangesAsync(cancellationToken);

        // Failed rather than Completed so the operator-facing sentence lands on the job row as its
        // recorded error. The retry it earns is harmless: the request is terminal now, so the next
        // attempt returns at the status guard above without spending anything.
        return JobHandlingResult.Failed(resolution.Explanation);
    }

    /// <summary>Writes what the turn produced, and moves the request to where section 6 puts it.</summary>
    private async Task<JobHandlingResult> ApplyAsync(
        Request request,
        ConversationRecord record,
        RefinementResult result,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        switch (result.Outcome)
        {
            case RefinementOutcome.SpecProposed proposed:
                {
                    record.RecordSpec(ConversationRehydration.WriteSpec(proposed.Spec), now);

                    if (proposed.RequiresEngineerReview)
                    {
                        // Section 16: instruction-shaped language in the submitted text. An engineer must
                        // read the flags before anything is dispatched, so this never auto-dispatches.
                        record.RecordFlags(
                            ConversationRehydration.WriteFlags(proposed.Flags),
                            proposed.Flags.Count,
                            now);
                    }

                    var version = await NextSpecVersionAsync(request.Id, cancellationToken);
                    var spec = SpecDocumentMapper.ToDraft(proposed.Spec, request.Id, version, now);

                    _db.Specs.Add(spec);
                    request.TransitionTo(RequestStatus.SpecReady, now);

                    await _db.SaveChangesAsync(cancellationToken);

                    // Section 7.5: SpecReady → Queued can be automatic, and where it is, the spend gate
                    // is skipped entirely rather than shown and auto-pressed.
                    await MaybeAutoDispatchAsync(request, spec, proposed, now, cancellationToken);

                    return JobHandlingResult.Completed;
                }

            case RefinementOutcome.Refused refused:
                {
                    _logger.LogInformation(
                        "Refinement refused request {RequestId}: {Detail}",
                        request.Id,
                        refused.EngineerDetail);

                    request.TransitionTo(RequestStatus.Rejected, now);
                    await _db.SaveChangesAsync(cancellationToken);

                    return JobHandlingResult.Completed;
                }

            default:
                {
                    // A question, or an answer in chat. Either way the thread stays open and the next
                    // thing that happens is the requester saying something.
                    request.TransitionTo(RequestStatus.Refining, now);
                    await _db.SaveChangesAsync(cancellationToken);

                    return JobHandlingResult.Completed;
                }
        }
    }

    /// <summary>Section 7.5's auto-dispatch, applied the moment a specification exists.</summary>
    private async Task MaybeAutoDispatchAsync(
        Request request,
        Spec spec,
        RefinementOutcome.SpecProposed proposed,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (proposed.RequiresEngineerReview)
        {
            return;
        }

        var permission = await _autoDispatch.PermitsAsync(request, cancellationToken);

        if (!permission.Enabled)
        {
            return;
        }

        request.TransitionTo(RequestStatus.Queued, now);
        await _db.SaveChangesAsync(cancellationToken);

        // The job is written after the status, deliberately: a restart in the gap leaves a queued
        // request the orchestrator's own sweep re-queues, whereas the other order would dispatch
        // work the thread does not yet claim to be doing.
        await _coordinator.EnqueueSpecAsync(
            new SpecBuildPayload { RequestId = request.Id, SpecId = spec.Id },
            cancellationToken);

        _logger.LogInformation(
            "Request {RequestId} auto-dispatched without an approver: {Reason}",
            request.Id,
            permission.Reason);
    }

    private async Task<ConversationRecord> LoadConversationAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        var record = await _db.Conversations
            .Include(row => row.Turns)
            .Where(row => row.RequestId == request.Id)
            .OrderBy(row => row.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (record is not null)
        {
            return record;
        }

        // Plan, not Chat: a request has been filed, so somebody has already decided they want a
        // change rather than an answer (section 10b).
        var started = ConversationRecord.Start(
            request.OrgId,
            InteractionMode.Plan,
            request.Id,
            _clock.GetUtcNow());

        _db.Conversations.Add(started);

        return started;
    }

    /// <summary>
    /// The newest specification on this request, or null while refinement has not produced one.
    /// </summary>
    /// <remarks>
    /// Re-read rather than carried out of <see cref="ApplyAsync"/>, because the version the thread has
    /// to show is the version the row says — including on the turns that produced no new one, where
    /// a previously proposed spec is still what the requester is looking at.
    /// </remarks>
    private Task<Spec?> CurrentSpecAsync(Guid requestId, CancellationToken cancellationToken)
        => _db.Specs
            .AsNoTracking()
            .Where(spec => spec.RequestId == requestId)
            .OrderByDescending(spec => spec.Version)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<int> NextSpecVersionAsync(Guid requestId, CancellationToken cancellationToken)
        => await _db.Specs
            .AsNoTracking()
            .Where(spec => spec.RequestId == requestId)
            .MaxAsync(spec => (int?)spec.Version, cancellationToken) is { } highest
            ? highest + 1
            : 1;

    /// <summary>
    /// True when this exact turn is already in the transcript with an answer after it.
    /// </summary>
    /// <remarks>
    /// The queue is at-least-once and a model call is the most expensive thing in the loop, so this
    /// is worth one read. It deliberately requires the <em>answer</em> to be present too: a job that
    /// recorded the requester's words and then died still has a turn owing.
    /// </remarks>
    private static bool AlreadyAnswered(ConversationRecord record, string message)
    {
        var turns = record.Turns;

        for (var i = turns.Count - 1; i >= 0; i--)
        {
            if (turns[i].Kind != ConversationTurnKind.RequesterMessage)
            {
                continue;
            }

            return string.Equals(
                       turns[i].RequesterText.RevealForPersistence(),
                       message,
                       StringComparison.Ordinal)
                   && i < turns.Count - 1;
        }

        return false;
    }

    private static ConversationTurnKind KindOf(RefinementOutcome outcome) => outcome switch
    {
        RefinementOutcome.SpecProposed => ConversationTurnKind.SpecProposed,
        RefinementOutcome.Refused => ConversationTurnKind.Refusal,
        RefinementOutcome.Answered => ConversationTurnKind.Answer,
        _ => ConversationTurnKind.ClarifyingQuestion,
    };

    private TimeSpan Delay(DateTimeOffset? until)
    {
        if (until is not { } reset)
        {
            return _options.LockRetryInterval;
        }

        var wait = reset - _clock.GetUtcNow();

        return wait <= TimeSpan.Zero ? _options.LockRetryInterval : wait;
    }
}
