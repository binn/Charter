using System.Text.Json;
using System.Text.Json.Nodes;
using Charter.Data;
using Charter.Domain;
using Charter.Notifications;
using Charter.Orchestration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Charter.Runners;

/// <summary>
/// The event types a sandbox posts that Charter acts on rather than merely records.
/// </summary>
/// <remarks>
/// <see cref="EventTypes"/> is the agent-produced vocabulary and <see cref="OrchestrationEventTypes"/>
/// is the control plane's own. These two are neither: they are the two halves of one conversation
/// between an agent that stopped to ask and the person who answered, and the transcript is the only
/// place either of them can live, because section 2.3 leaves the control plane no memory to hold them
/// in.
/// </remarks>
public static class RunnerEventTypes
{
    /// <summary>
    /// The agent stopped and asked the requester something. Section 6's first notifying state.
    /// </summary>
    public const string Question = "question";

    /// <summary>The requester answered. The session resumes from here.</summary>
    public const string QuestionAnswered = "question_answered";

    /// <summary>
    /// The question one <see cref="Question"/> payload carries, or null when it carries none.
    /// </summary>
    /// <remarks>
    /// Three spellings are read because the payload is written by a shim, a workflow's <c>curl</c>, or
    /// an adapter's own mapping, and none of them shares a schema with the others. A payload with no
    /// readable question is not a question: it would put the request in a state that notifies somebody
    /// and then show them nothing to answer.
    /// </remarks>
    public static string? ReadQuestion(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        JsonNode? node;

        try
        {
            node = JsonNode.Parse(payload);
        }
        catch (JsonException)
        {
            return null;
        }

        if (node is not JsonObject root)
        {
            return null;
        }

        foreach (var name in new[] { "question", "message", "text" })
        {
            if (root[name] is JsonValue value
                && value.TryGetValue<string>(out var text)
                && !string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }

        return null;
    }
}

/// <summary>What asking or answering did.</summary>
/// <param name="Moved">True when the request and session were moved here.</param>
/// <param name="Notification">What the notification service decided, or null when nothing was sent.</param>
public sealed record NeedsInputAnnouncement(bool Moved, NotificationOutcome? Notification = null);

/// <summary>
/// Section 6's first notifying state, and the only place it is entered and left.
/// </summary>
/// <remarks>
/// <para>
/// <c>NeedsInput</c> existed in the enum, the label table and the projection, and nothing ever set it
/// — so an agent that stopped to ask a question reached nobody, and the session simply sat there
/// until somebody happened to open the app. It belongs here because the state is a fact about a
/// sandbox that has stopped, and the runner callback is the only thing that learns it.
/// </para>
/// <para>
/// The section 6 gate is <em>not</em> re-implemented here. <see cref="INotificationService"/> checks
/// it once, above the channels; a second copy of the rule in a call site is how the closed set of two
/// quietly becomes three. What <em>is</em> enforced here is that the same question does not notify
/// twice: the callback appends under an idempotency key first and only announces a genuinely new
/// event, and a request already sitting in <c>NeedsInput</c> is not announced again either.
/// </para>
/// <para>
/// Nothing the requester is sent carries a repository, a branch, a commit or a session id — that is a
/// property of <see cref="RequestNotification"/>, which has nowhere to put them, rather than of care
/// taken here. The agent's own words go through <see cref="RequesterSafeText"/> on the way into the
/// template, which is where free text from a model is cleaned for everything else too.
/// </para>
/// </remarks>
public sealed class NeedsInputAnnouncer
{
    private readonly CharterDbContext _db;
    private readonly SessionJournal _journal;
    private readonly JobQueue _queue;
    private readonly OrchestrationOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<NeedsInputAnnouncer> _logger;
    private readonly INotificationService? _notifications;

    public NeedsInputAnnouncer(
        CharterDbContext db,
        SessionJournal journal,
        JobQueue queue,
        OrchestrationOptions options,
        TimeProvider clock,
        ILogger<NeedsInputAnnouncer> logger,
        INotificationService? notifications = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _db = db;
        _journal = journal;
        _queue = queue;
        _options = options;
        _clock = clock;
        _logger = logger;
        _notifications = notifications;
    }

    /// <summary>
    /// Moves the session and its request to <c>NeedsInput</c> and tells the requester.
    /// </summary>
    /// <remarks>
    /// Idempotent, because a runner that loses its connection retries and a control plane that
    /// restarted cannot know whether it saw the delivery before. A request already in
    /// <c>NeedsInput</c> is not moved again and nobody is told twice.
    /// </remarks>
    public async Task<NeedsInputAnnouncement> AskAsync(
        Guid sessionId,
        string question,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var session = await _db.Sessions.FirstOrDefaultAsync(
            row => row.Id == sessionId,
            cancellationToken);

        if (session is null || session.IsTerminal)
        {
            return new NeedsInputAnnouncement(false);
        }

        var context = await LoadAsync(session.SpecId, cancellationToken);
        if (context is null)
        {
            return new NeedsInputAnnouncement(false);
        }

        var request = await _db.Requests.FirstOrDefaultAsync(
            row => row.Id == context.RequestId,
            cancellationToken);

        if (request is null)
        {
            return new NeedsInputAnnouncement(false);
        }

        var alreadyThere = request.Status == RequestStatus.NeedsInput;

        // Section 6 puts NeedsInput opposite Running. A request that has moved past the build — a
        // preview is up, an engineer is reviewing, it is merged — is not dragged backwards because a
        // late event arrived, and it is not notified about either: there is nobody left waiting on
        // the answer, and section 6's whole argument for two notifying states is that Charter gets
        // muted the moment it is noisy.
        var waiting = request.Status is RequestStatus.Queued
            or RequestStatus.Running
            or RequestStatus.PrOpen;

        if (!waiting && !alreadyThere)
        {
            return new NeedsInputAnnouncement(false);
        }

        var now = _clock.GetUtcNow();
        var moved = false;

        if (session.Status != SessionStatus.NeedsInput)
        {
            session.TransitionTo(SessionStatus.NeedsInput, now);
            moved = true;
        }

        if (waiting)
        {
            request.TransitionTo(RequestStatus.NeedsInput, now);
            moved = true;
        }

        if (moved)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        if (alreadyThere || _notifications is null)
        {
            return new NeedsInputAnnouncement(moved);
        }

        return new NeedsInputAnnouncement(
            moved,
            await NotifyAsync(request, context.User, question, cancellationToken));
    }

    /// <summary>
    /// Records the answer, puts the session back to <c>Running</c>, and queues its dispatch again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All three, in that order, and all three durable. The answer is an event because section 2.3
    /// gives it nowhere else to live: the control plane may be a different container by the time a
    /// runner asks what it was, and a queue row is claimed and eventually completed away. The build
    /// job is what actually resumes the work, and it names the same session rather than a new one —
    /// section 11's one thread, and the same branch.
    /// </para>
    /// <para>
    /// Returns <c>false</c> for a session that was not waiting on anybody, which is what makes a
    /// double-submitted answer a no-op rather than a second dispatch.
    /// </para>
    /// </remarks>
    public async Task<bool> AnswerAsync(
        Guid sessionId,
        string answer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(answer);

        var session = await _db.Sessions.FirstOrDefaultAsync(
            row => row.Id == sessionId,
            cancellationToken);

        if (session is null || session.Status != SessionStatus.NeedsInput)
        {
            return false;
        }

        var context = await LoadAsync(session.SpecId, cancellationToken);
        var now = _clock.GetUtcNow();

        session.TransitionTo(SessionStatus.Running, now);

        if (context is not null)
        {
            var request = await _db.Requests.FirstOrDefaultAsync(
                row => row.Id == context.RequestId,
                cancellationToken);

            if (request is { Status: RequestStatus.NeedsInput })
            {
                request.TransitionTo(RequestStatus.Running, now);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);

        await _journal.AppendAsync(
            sessionId,
            RunnerEventTypes.QuestionAnswered,
            new JsonObject { ["answer"] = answer }.ToJsonString(),
            $"answer:{RunnerCallbackEndpoints.ContentKey(RunnerEventTypes.QuestionAnswered, answer)}",
            now,
            cancellationToken);

        // Section 2.3: the queue row is the record. Same session id, so whatever picks it up continues
        // the run rather than starting a second one on a second branch.
        await _queue.EnqueueAsync(
            JobType.Build,
            new BuildJobPayload { SessionId = sessionId }.ToJson(),
            now: now,
            cancellationToken: cancellationToken);

        _logger.LogInformation("Session {SessionId} was answered and is running again", sessionId);

        return true;
    }

    private async Task<NotificationOutcome?> NotifyAsync(
        Request request,
        User requester,
        string question,
        CancellationToken cancellationToken)
    {
        var notification = new RequestNotification
        {
            RequestId = request.Id,
            Status = RequestStatus.NeedsInput,
            Recipient = new NotificationRecipient
            {
                UserId = requester.Id,
                Email = requester.Email,
                DisplayName = requester.DisplayName,
            },
            RequestSummary = request.RawText,
            ThreadUrl = _options.ThreadUrlFor(request.Id),
            Question = question,
            NotificationSettingsUrl = _options.NotificationSettingsUrl,
        };

        try
        {
            var outcome = await _notifications!.NotifyAsync(notification, cancellationToken);

            _logger.LogInformation(
                "Request {RequestId} reached NeedsInput; notification {Outcome}",
                request.Id,
                outcome.Kind);

            return outcome;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A state transition must not roll back because a mail server was down. The row already
            // says NeedsInput and the thread already shows the question.
            _logger.LogError(
                exception,
                "Could not tell the requester that request {RequestId} has a question waiting",
                request.Id);

            return null;
        }
    }

    /// <summary>The request and the person who filed it, behind one specification.</summary>
    private Task<RequesterContext?> LoadAsync(Guid specId, CancellationToken cancellationToken)
        => (from spec in _db.Specs.AsNoTracking()
            where spec.Id == specId
            join filed in _db.Requests.AsNoTracking() on spec.RequestId equals filed.Id
            join user in _db.Users.AsNoTracking() on filed.RequesterId equals user.Id
            select new RequesterContext(filed.Id, user))
            .FirstOrDefaultAsync(cancellationToken);

    private sealed record RequesterContext(Guid RequestId, User User);
}
