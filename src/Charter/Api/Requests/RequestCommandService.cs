using System.Text.Json;
using Charter.Api.Contracts;
using Charter.Auth.Authorization;
using Charter.Data;
using Charter.Domain;
using Charter.Hubs;
using Microsoft.EntityFrameworkCore;

namespace Charter.Api.Requests;

/// <summary>The outcome of a command, in the two shapes an endpoint needs to answer with.</summary>
/// <param name="Succeeded">Whether the command did what was asked.</param>
/// <param name="Reason">Plain language, safe to show a person (section 11). Never a stack trace.</param>
/// <param name="Status">The HTTP status the endpoint should return.</param>
public readonly record struct CommandOutcome(bool Succeeded, string Reason, int Status)
{
    /// <summary>It worked.</summary>
    public static CommandOutcome Ok() => new(true, string.Empty, StatusCodes.Status204NoContent);

    /// <summary>The caller may not do this, and may know why.</summary>
    public static CommandOutcome Forbidden(string reason)
        => new(false, reason, StatusCodes.Status403Forbidden);

    /// <summary>Gone, or never theirs — the same answer either way (section 7.3).</summary>
    public static CommandOutcome NotFound() => new(false, string.Empty, StatusCodes.Status404NotFound);

    /// <summary>The state machine of section 6 does not allow this from here.</summary>
    public static CommandOutcome Conflict(string reason)
        => new(false, reason, StatusCodes.Status409Conflict);

    /// <summary>The body did not make sense.</summary>
    public static CommandOutcome Invalid(string reason)
        => new(false, reason, StatusCodes.Status400BadRequest);
}

/// <summary>
/// Everything that changes a request: intake, the refinement turn, the spend gate, feedback, cancel
/// and rebuild.
/// </summary>
/// <remarks>
/// <para>
/// Every write goes through the domain aggregates rather than setting columns, so the invariants
/// those types own — a spec approves once, a cancellation latches, cost only accumulates — hold here
/// too.
/// </para>
/// <para>
/// Everything that a runner has to act on is written to the Postgres job queue in the same call,
/// because section 2.3 forbids orchestration state anywhere else. The SignalR broadcast is a
/// courtesy on top of that, never the record.
/// </para>
/// </remarks>
public sealed class RequestCommandService
{
    private readonly CharterDbContext database;
    private readonly ICharterAuthorizationService authorization;
    private readonly RequestQueryService queries;
    private readonly IRequestStreamPublisher stream;
    private readonly JobQueue jobs;
    private readonly TimeProvider clock;

    public RequestCommandService(
        CharterDbContext database,
        ICharterAuthorizationService authorization,
        RequestQueryService queries,
        IRequestStreamPublisher stream,
        JobQueue jobs,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(clock);

        this.database = database;
        this.authorization = authorization;
        this.queries = queries;
        this.stream = stream;
        this.jobs = jobs;
        this.clock = clock;
    }

    /// <summary>The longest raw request intake accepts. Long enough for a paragraph, not a novel.</summary>
    public const int MaxRawTextLength = 8_000;

    /// <summary>
    /// Files a request (section 5).
    /// </summary>
    /// <remarks>
    /// Section 7.3 decides whether this repository is open to this member, and it is deny-by-default:
    /// a repository with no scope row is requestable by nobody, and one that has not passed its smoke
    /// test (section 9) is requestable by nobody either. Both refusals read the same from outside.
    /// </remarks>
    public async Task<(CommandOutcome Outcome, Guid RequestId)> CreateAsync(
        MemberSnapshot member,
        CreateRequestBody body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(body);

        if (!Guid.TryParse(body.ProjectId, out var repoId))
        {
            return (CommandOutcome.Invalid("Pick a project to file this against."), Guid.Empty);
        }

        var rawText = body.RawText?.Trim();
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return (CommandOutcome.Invalid("Tell us what you need in your own words."), Guid.Empty);
        }

        if (rawText.Length > MaxRawTextLength)
        {
            return (
                CommandOutcome.Invalid("That is longer than we can take in one go. Trim it down and send the rest as a follow-up."),
                Guid.Empty);
        }

        var decision = await authorization.CanFileRequestAsync(member, repoId, cancellationToken);
        if (decision.IsDenied)
        {
            return (CommandOutcome.Forbidden(decision.Reason), Guid.Empty);
        }

        var now = clock.GetUtcNow();
        var request = Request.File(member.OrgId, repoId, member.UserId, rawText, body.TemplateId, now);

        // Section 10: nothing reaches an agent without passing through refinement, so intake lands in
        // Refining and queues a refine job rather than anything that could write to a repository.
        request.TransitionTo(RequestStatus.Refining, now);

        database.Requests.Add(request);
        await database.SaveChangesAsync(cancellationToken);

        await jobs.EnqueueAsync(
            JobType.Refine,
            JsonSerializer.Serialize(new { requestId = request.Id }, CharterApiJson.Options),
            now: now,
            cancellationToken: cancellationToken);

        return (CommandOutcome.Ok(), request.Id);
    }

    /// <summary>Adds a turn to the refinement conversation and asks the refiner for the next one.</summary>
    public async Task<CommandOutcome> SendRefinementMessageAsync(
        MemberSnapshot member,
        Guid requestId,
        SendRefinementMessageBody body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(body);

        var text = body.Body?.Trim();
        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(body.ChoiceId))
        {
            return CommandOutcome.Invalid("Type something, or pick one of the suggested answers.");
        }

        if (text is { Length: > MaxRawTextLength })
        {
            return CommandOutcome.Invalid("That is longer than we can take in one go.");
        }

        var view = await queries.LoadAsync(member, requestId, cancellationToken);
        if (view is null)
        {
            return CommandOutcome.NotFound();
        }

        var request = view.Aggregate.Request;

        // Only the person the conversation belongs to may add to it; an engineer reading along is
        // reading, not talking (section 10).
        if (request.RequesterId != member.UserId)
        {
            return CommandOutcome.Forbidden("only the person who filed this request can reply to it");
        }

        if (request.Status is not (RequestStatus.Draft or RequestStatus.Refining or RequestStatus.Rejected))
        {
            return CommandOutcome.Conflict("this request has moved past the point where it can be changed by replying");
        }

        var now = clock.GetUtcNow();

        // The durable record of the turn: section 2.3 means the queue row, not a hub frame, is what
        // survives a restart.
        await jobs.EnqueueAsync(
            JobType.Refine,
            JsonSerializer.Serialize(
                new { requestId = request.Id, body = text ?? string.Empty, choiceId = body.ChoiceId },
                CharterApiJson.Options),
            now: now,
            cancellationToken: cancellationToken);

        var message = RefinementThread.RequesterTurn(request.Id, text ?? string.Empty, now);

        await stream.PublishAsync(request.Id, RequestStreamEvents.RefinementMessage(message), cancellationToken);
        await stream.PublishAsync(request.Id, RequestStreamEvents.CharterThinking(thinking: true), cancellationToken);

        return CommandOutcome.Ok();
    }

    /// <summary>
    /// The spend gate (section 7.5), and nothing else.
    /// </summary>
    /// <remarks>
    /// Approving says the work is worth burning tokens on. It says nothing about whether the
    /// resulting code may ship — the merge gate is branch protection and CODEOWNERS, outside
    /// Charter's trust boundary entirely (section 7.4).
    /// </remarks>
    public async Task<CommandOutcome> ApproveSpecAsync(
        MemberSnapshot member,
        Guid requestId,
        int version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        var spec = await LoadSpecAsync(requestId, version, cancellationToken);
        if (spec is null)
        {
            return CommandOutcome.NotFound();
        }

        var decision = await authorization.CanApproveSpecAsync(member, spec.Id, cancellationToken);
        if (decision.IsDenied)
        {
            return CommandOutcome.Forbidden(decision.Reason);
        }

        if (spec.IsApproved)
        {
            return CommandOutcome.Conflict("this plan has already been approved");
        }

        var request = await database.Requests.SingleOrDefaultAsync(row => row.Id == requestId, cancellationToken);
        if (request is null)
        {
            return CommandOutcome.NotFound();
        }

        var now = clock.GetUtcNow();
        spec.Approve(member.UserId, now);
        request.TransitionTo(RequestStatus.Queued, now);
        await database.SaveChangesAsync(cancellationToken);

        await jobs.EnqueueAsync(
            JobType.Build,
            JsonSerializer.Serialize(new { requestId = request.Id, specId = spec.Id }, CharterApiJson.Options),
            now: now,
            cancellationToken: cancellationToken);

        await stream.PublishAsync(
            request.Id,
            RequestStreamEvents.Status(ApiRequestStatus.Queued),
            cancellationToken);

        return CommandOutcome.Ok();
    }

    /// <summary>Sends a spec back for another round of refinement.</summary>
    public async Task<CommandOutcome> RequestSpecChangesAsync(
        MemberSnapshot member,
        Guid requestId,
        int version,
        RequestSpecChangesBody body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(body);

        var note = body.Note?.Trim();
        if (string.IsNullOrWhiteSpace(note))
        {
            return CommandOutcome.Invalid("Say what needs changing, so the next version can fix it.");
        }

        var spec = await LoadSpecAsync(requestId, version, cancellationToken);
        if (spec is null)
        {
            return CommandOutcome.NotFound();
        }

        var view = await queries.LoadAsync(member, requestId, cancellationToken);
        if (view is null)
        {
            return CommandOutcome.NotFound();
        }

        var request = view.Aggregate.Request;

        // The requester owns the acceptance criteria (section 10b), and an approver holding the spend
        // gate may also send it back rather than approve it (section 7.5).
        var mayAskForChanges = request.RequesterId == member.UserId
                               || (await authorization.CanApproveSpecAsync(member, spec.Id, cancellationToken))
                                   .IsAllowed;

        if (!mayAskForChanges)
        {
            return CommandOutcome.Forbidden("only the person who filed this request, or an approver, can send it back");
        }

        if (spec.IsApproved)
        {
            return CommandOutcome.Conflict("this plan has already been approved, so it cannot be sent back");
        }

        var tracked = await database.Requests.SingleOrDefaultAsync(row => row.Id == requestId, cancellationToken);
        if (tracked is null)
        {
            return CommandOutcome.NotFound();
        }

        var now = clock.GetUtcNow();
        tracked.TransitionTo(RequestStatus.Refining, now);
        await database.SaveChangesAsync(cancellationToken);

        await jobs.EnqueueAsync(
            JobType.Refine,
            JsonSerializer.Serialize(
                new { requestId, specId = spec.Id, changesRequested = note },
                CharterApiJson.Options),
            now: now,
            cancellationToken: cancellationToken);

        await stream.PublishAsync(
            requestId,
            RequestStreamEvents.Status(ApiRequestStatus.Refining),
            cancellationToken);

        return CommandOutcome.Ok();
    }

    /// <summary>
    /// Section 11: two buttons. "Not quite" becomes a new session on the same spec, same thread.
    /// </summary>
    public async Task<CommandOutcome> SubmitFeedbackAsync(
        MemberSnapshot member,
        Guid requestId,
        SubmitFeedbackBody body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(body);

        if (body.Verdict is not { } verdict)
        {
            return CommandOutcome.Invalid("Tell us whether it works or not.");
        }

        var view = await queries.LoadAsync(member, requestId, cancellationToken);
        if (view is null)
        {
            return CommandOutcome.NotFound();
        }

        var request = view.Aggregate.Request;
        if (request.RequesterId != member.UserId)
        {
            return CommandOutcome.Forbidden("only the person who filed this request can say whether it worked");
        }

        var note = body.Note?.Trim();
        if (note is { Length: > RequestFeedback.MaxNoteLength })
        {
            return CommandOutcome.Invalid("That is longer than we can take in one go.");
        }

        var now = clock.GetUtcNow();
        var specId = view.Aggregate.Spec?.Id;

        // The verdict is a row before it is a job. A queue payload is a work item that gets claimed,
        // retried and eventually completed away; "did this work" is a fact about the thread, and
        // section 11 renders it back on every load.
        database.RequestFeedback.Add(RequestFeedback.Record(
            requestId,
            member.UserId,
            verdict.ToDomain(),
            view.Aggregate.Session?.Id,
            note,
            now));

        await database.SaveChangesAsync(cancellationToken);

        await jobs.EnqueueAsync(
            verdict == ApiFeedbackVerdict.NotQuite ? JobType.Build : JobType.Recap,
            JsonSerializer.Serialize(
                new
                {
                    requestId,
                    specId,
                    verdict = verdict == ApiFeedbackVerdict.Works ? "works" : "not_quite",
                    note,
                },
                CharterApiJson.Options),
            now: now,
            cancellationToken: cancellationToken);

        if (verdict == ApiFeedbackVerdict.NotQuite)
        {
            var tracked = await database.Requests.SingleOrDefaultAsync(row => row.Id == requestId, cancellationToken);
            if (tracked is not null)
            {
                tracked.TransitionTo(RequestStatus.Queued, now);
                await database.SaveChangesAsync(cancellationToken);
            }

            await stream.PublishAsync(
                requestId,
                RequestStreamEvents.Status(ApiRequestStatus.Queued),
                cancellationToken);
        }

        return CommandOutcome.Ok();
    }

    /// <summary>
    /// Section 11: the cancel button must actually kill the runner and settle token cost.
    /// </summary>
    /// <remarks>
    /// The latch is a column, not a signal, precisely because the container can restart before any
    /// runner notices (section 2.3). Whoever picks the session up next reads
    /// <see cref="Session.CancelRequestedAt"/> and stops.
    /// </remarks>
    public async Task<CommandOutcome> CancelAsync(
        MemberSnapshot member,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        var view = await queries.LoadAsync(member, requestId, cancellationToken);
        if (view is null)
        {
            return CommandOutcome.NotFound();
        }

        var request = view.Aggregate.Request;

        var mayCancel = request.RequesterId == member.UserId || view.Visibility.Transcript;
        if (!mayCancel)
        {
            return CommandOutcome.Forbidden("only the person who filed this request, or an engineer, can stop it");
        }

        if (view.Aggregate.Session is not { } snapshot)
        {
            return CommandOutcome.Conflict("there is nothing running to stop yet");
        }

        var session = await database.Sessions.SingleOrDefaultAsync(row => row.Id == snapshot.Id, cancellationToken);
        if (session is null)
        {
            return CommandOutcome.NotFound();
        }

        // The same rule the projection uses to decide whether to offer the button at all.
        if (!RequestProjection.IsRunning(session))
        {
            return CommandOutcome.Conflict("this has already finished");
        }

        var now = clock.GetUtcNow();
        session.RequestCancellation(now);
        session.TransitionTo(SessionStatus.Cancelled, now);

        var tracked = await database.Requests.SingleOrDefaultAsync(row => row.Id == requestId, cancellationToken);
        tracked?.TransitionTo(RequestStatus.Cancelled, now);

        await database.SaveChangesAsync(cancellationToken);

        await stream.PublishAsync(
            requestId,
            RequestStreamEvents.Status(ApiRequestStatus.Cancelled),
            cancellationToken);
        await stream.PublishAsync(requestId, RequestStreamEvents.Ended(now), cancellationToken);

        return CommandOutcome.Ok();
    }

    /// <summary>
    /// Section 27.7: <c>Rebuild</c> is the primary action on an expired artifact.
    /// </summary>
    public async Task<CommandOutcome> RebuildArtifactAsync(
        MemberSnapshot member,
        Guid requestId,
        Guid artifactId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        var view = await queries.LoadAsync(member, requestId, cancellationToken);
        if (view is null)
        {
            return CommandOutcome.NotFound();
        }

        var artifact = view.Aggregate.Artifacts.FirstOrDefault(row => row.Id == artifactId);
        if (artifact is null)
        {
            return CommandOutcome.NotFound();
        }

        var request = view.Aggregate.Request;
        if (request.RequesterId != member.UserId && !view.Visibility.Transcript)
        {
            return CommandOutcome.Forbidden("only the person who filed this request, or an engineer, can rebuild it");
        }

        var now = clock.GetUtcNow();

        // Section 27.7: the row moves before the job is queued and before anything is broadcast. The
        // aggregate above is untracked, so the artifact is re-read here to be written. Leaving it
        // `expired` would contradict both the stream frame published below and the card the requester
        // is looking at — and a reload would put the dead host straight back under a Rebuild button
        // they have already pressed. `MarkPending` clears the URL and the expiry with it, which is
        // the point: a skeleton above a link that does not work is worse than either alone.
        var tracked = await database.VerificationArtifacts
            .SingleOrDefaultAsync(row => row.Id == artifactId, cancellationToken);

        if (tracked is not null)
        {
            tracked.MarkPending();
            await database.SaveChangesAsync(cancellationToken);
        }

        await jobs.EnqueueAsync(
            JobType.Build,
            JsonSerializer.Serialize(
                new { requestId, artifactId, rebuild = true },
                CharterApiJson.Options),
            now: now,
            cancellationToken: cancellationToken);

        await stream.PublishAsync(
            requestId,
            RequestStreamEvents.ArtifactState(artifactId.ToString(), ApiArtifactState.Pending),
            cancellationToken);

        return CommandOutcome.Ok();
    }

    /// <summary>
    /// Section 7.5's first post-hoc action: the normal review path, merge as usual.
    /// </summary>
    /// <remarks>
    /// Charter has no merge button (section 7.4) and this is not one. It records that an engineer has
    /// read the work and moves the request to <c>in_review</c> so the requester's thread stops saying
    /// the agent is still going; the merge gate remains branch protection and CODEOWNERS, outside
    /// Charter entirely.
    /// </remarks>
    public async Task<CommandOutcome> ApproveSessionAsync(
        MemberSnapshot member,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        var (outcome, view, session) = await LoadForSessionActionAsync(member, requestId, cancellationToken);
        if (!outcome.Succeeded)
        {
            return outcome;
        }

        var now = clock.GetUtcNow();

        session!.TransitionTo(SessionStatus.InReview, now);

        var tracked = await database.Requests.SingleOrDefaultAsync(row => row.Id == requestId, cancellationToken);
        tracked?.TransitionTo(RequestStatus.InReview, now);

        Audit(member, ApiAuditActions.SessionApproved, session.Id, now);
        await database.SaveChangesAsync(cancellationToken);

        await stream.PublishAsync(
            requestId,
            RequestStreamEvents.Status(ApiRequestStatus.InReview),
            cancellationToken);

        return CommandOutcome.Ok();
    }

    /// <summary>
    /// Section 7.5 "Steer": continue the existing session with a new instruction; same branch, same
    /// thread.
    /// </summary>
    public async Task<CommandOutcome> SteerSessionAsync(
        MemberSnapshot member,
        Guid requestId,
        SteerSessionBody body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        var instruction = body.Instruction?.Trim();
        if (string.IsNullOrWhiteSpace(instruction))
        {
            return CommandOutcome.Invalid("Say what it should do next.");
        }

        if (instruction.Length > MaxRawTextLength)
        {
            return CommandOutcome.Invalid("That is longer than we can take in one go.");
        }

        var (outcome, view, session) = await LoadForSessionActionAsync(member, requestId, cancellationToken);
        if (!outcome.Succeeded)
        {
            return outcome;
        }

        var now = clock.GetUtcNow();

        session!.TransitionTo(SessionStatus.Running, now);

        var tracked = await database.Requests.SingleOrDefaultAsync(row => row.Id == requestId, cancellationToken);
        tracked?.TransitionTo(RequestStatus.Running, now);

        Audit(member, ApiAuditActions.SessionSteered, session.Id, now);
        await database.SaveChangesAsync(cancellationToken);

        // Section 2.3: the queue row is the record. Same session id, so the runner continues rather
        // than starting again — that is what "same branch, same thread" means in the payload.
        await jobs.EnqueueAsync(
            JobType.Build,
            JsonSerializer.Serialize(
                new { requestId, session_id = session.Id, steer = instruction },
                CharterApiJson.Options),
            now: now,
            cancellationToken: cancellationToken);

        await stream.PublishAsync(
            requestId,
            RequestStreamEvents.Status(ApiRequestStatus.Running),
            cancellationToken);

        return CommandOutcome.Ok();
    }

    /// <summary>
    /// Section 7.5 "Revise and rebuild": fork the spec, edit it, dispatch a fresh session onto the
    /// same branch.
    /// </summary>
    /// <remarks>
    /// A fork is a new <see cref="Spec"/> version rather than an edit of the old one, because the
    /// version somebody approved has to stay readable next to the version that was built. The
    /// structured fields come across from the parent: an engineer editing the markdown is editing the
    /// instruction, not re-authoring the acceptance criteria the requester agreed to (section 10b),
    /// and silently replacing those with whatever a parser made of the prose would break the one
    /// thing section 10b says must never drift.
    /// </remarks>
    public async Task<CommandOutcome> ReviseSessionAsync(
        MemberSnapshot member,
        Guid requestId,
        ReviseSessionBody body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        var revised = body.RevisedSpecMd?.Trim();
        if (string.IsNullOrWhiteSpace(revised))
        {
            return CommandOutcome.Invalid("Send the revised plan, not an empty one.");
        }

        if (revised.Length > MaxSpecLength)
        {
            return CommandOutcome.Invalid("That plan is longer than we can take in one go.");
        }

        var (outcome, view, session) = await LoadForSessionActionAsync(member, requestId, cancellationToken);
        if (!outcome.Succeeded)
        {
            return outcome;
        }

        if (view!.Aggregate.Spec is not { } parent)
        {
            return CommandOutcome.Conflict("there is no plan to revise yet");
        }

        var now = clock.GetUtcNow();

        var fork = Spec.Draft(
            requestId,
            parent.Version + 1,
            parent.Title,
            parent.Outcome,
            revised,
            parent.AcceptanceCriteria,
            parent.TechnicalApproach,
            parent.Scope,
            parent.Risks,
            openQuestions: null,
            now);

        // The engineer revising it is the person vetting it, so the spend gate is already answered
        // (section 7.5). Recording the approval is what keeps "who authorised this session" true.
        fork.Approve(member.UserId, now);
        database.Specs.Add(fork);

        var tracked = await database.Requests.SingleOrDefaultAsync(row => row.Id == requestId, cancellationToken);
        tracked?.TransitionTo(RequestStatus.Queued, now);

        Audit(member, ApiAuditActions.SessionRevised, session!.Id, now);
        await database.SaveChangesAsync(cancellationToken);

        await jobs.EnqueueAsync(
            JobType.Build,
            JsonSerializer.Serialize(
                new { requestId, specId = fork.Id, revisedFrom = session.Id },
                CharterApiJson.Options),
            now: now,
            cancellationToken: cancellationToken);

        await stream.PublishAsync(
            requestId,
            RequestStreamEvents.Status(ApiRequestStatus.Queued),
            cancellationToken);

        return CommandOutcome.Ok();
    }

    /// <summary>
    /// Section 7.5 "Take over": Charter marks the session <c>handed_off</c> and stops touching it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is irreversible, and it has to be.</strong> Section 7.5 calls an agent and a human
    /// editing the same branch concurrently <em>"the one genuinely destructive failure mode in this
    /// design"</em>, so this is not a flag a later dispatch could decline to read. Three things make
    /// it stick, and each one is sufficient on its own:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <see cref="Session.HandOff"/> moves the session to a status <see cref="Session.IsTerminal"/>
    /// includes, and the dispatch planner returns no dispatch for a terminal session — so nothing can
    /// be sent to a runner for it, by any path, ever again.
    /// </description></item>
    /// <item><description>
    /// Every queued build job for this session is cancelled here, so work already in the queue when
    /// the button was pressed does not dispatch a second later.
    /// </description></item>
    /// <item><description>
    /// Steer and revise refuse a handed-off session below, and the projection stops offering them —
    /// the two halves of the same rule, which is why they read the session status rather than each
    /// other.
    /// </description></item>
    /// </list>
    /// <para>
    /// There is deliberately no un-take-over endpoint. Recovering is starting a new request, which
    /// makes a new session on a new branch, and that is the only shape in which "hand it back" is
    /// safe.
    /// </para>
    /// </remarks>
    public async Task<CommandOutcome> TakeOverSessionAsync(
        MemberSnapshot member,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        var (outcome, view, session) = await LoadForSessionActionAsync(member, requestId, cancellationToken);
        if (!outcome.Succeeded)
        {
            return outcome;
        }

        var now = clock.GetUtcNow();

        // The latch is a status the whole system already refuses to dispatch, not a new flag.
        session!.HandOff(now);

        // And the cancel latch too, so a runner already holding this session stops rather than
        // finishing its current write. Section 11's cancel path reads exactly this column.
        session.RequestCancellation(now);

        var tracked = await database.Requests.SingleOrDefaultAsync(row => row.Id == requestId, cancellationToken);
        tracked?.TransitionTo(RequestStatus.InReview, now);

        await CancelQueuedBuildJobsAsync(requestId, session.Id, view!.Aggregate.Spec?.Id, cancellationToken);

        Audit(member, ApiAuditActions.SessionHandedOff, session.Id, now);
        await database.SaveChangesAsync(cancellationToken);

        await stream.PublishAsync(
            requestId,
            RequestStreamEvents.Status(ApiRequestStatus.InReview),
            cancellationToken);
        await stream.PublishAsync(requestId, RequestStreamEvents.Ended(now), cancellationToken);

        return CommandOutcome.Ok();
    }

    /// <summary>The longest revised plan the fork endpoint accepts.</summary>
    public const int MaxSpecLength = 32_000;

    /// <summary>
    /// The three checks every section 7.5 action shares.
    /// </summary>
    /// <remarks>
    /// Repository read is the gate, because section 7.4 makes it the one that names an engineer, and
    /// it is the same flag <see cref="SessionActionsProjection"/> reads to decide whether to offer the
    /// controls at all. A handed-off session refuses everything, which is the irreversibility of
    /// take-over expressed once rather than in each of the three commands that must respect it.
    /// </remarks>
    private async Task<(CommandOutcome Outcome, RequestView? View, Session? Session)> LoadForSessionActionAsync(
        MemberSnapshot member,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);

        var view = await queries.LoadAsync(member, requestId, cancellationToken);
        if (view is null)
        {
            return (CommandOutcome.NotFound(), null, null);
        }

        if (!view.Visibility.Transcript)
        {
            return (
                CommandOutcome.Forbidden("these actions need read access to the repository"),
                null,
                null);
        }

        if (view.Aggregate.Session is not { } snapshot)
        {
            return (CommandOutcome.Conflict("nothing has been built for this request yet"), null, null);
        }

        var session = await database.Sessions.SingleOrDefaultAsync(
            row => row.Id == snapshot.Id,
            cancellationToken);

        if (session is null)
        {
            return (CommandOutcome.NotFound(), null, null);
        }

        if (session.Status == SessionStatus.HandedOff)
        {
            return (
                CommandOutcome.Conflict(
                    "an engineer has taken this branch over, so Charter no longer writes to it"),
                null,
                null);
        }

        return (CommandOutcome.Ok(), view, session);
    }

    /// <summary>
    /// Cancels build work already in the queue for this session.
    /// </summary>
    /// <remarks>
    /// The payload shapes differ by who wrote them — intake writes <c>specId</c>, the orchestrator
    /// writes <c>session_id</c> — so this matches on any identifier that names this request's work
    /// rather than on one spelling. Being generous is the safe direction: the cost of cancelling one
    /// job too many is a build that has to be asked for again, and the cost of missing one is the
    /// concurrent write section 7.5 calls destructive.
    /// </remarks>
    private async Task CancelQueuedBuildJobsAsync(
        Guid requestId,
        Guid sessionId,
        Guid? specId,
        CancellationToken cancellationToken)
    {
        var pending = await database.Jobs
            .Where(job => job.Type == JobType.Build && job.Status == JobStatus.Pending)
            .ToListAsync(cancellationToken);

        foreach (var job in pending)
        {
            if (Mentions(job.Payload, requestId) || Mentions(job.Payload, sessionId)
                || (specId is { } spec && Mentions(job.Payload, spec)))
            {
                job.Cancel();
            }
        }
    }

    private static bool Mentions(string payload, Guid id)
        => payload.Contains(id.ToString("D"), StringComparison.OrdinalIgnoreCase);

    private void Audit(MemberSnapshot member, string action, Guid sessionId, DateTimeOffset now)
        => database.AuditLogs.Add(AuditLog.Record(
            member.OrgId,
            action,
            targetType: "session",
            actorUserId: member.UserId,
            targetId: sessionId.ToString(),
            now: now));

    private Task<Spec?> LoadSpecAsync(Guid requestId, int version, CancellationToken cancellationToken)
        => database.Specs.SingleOrDefaultAsync(
            row => row.RequestId == requestId && row.Version == version,
            cancellationToken);
}
