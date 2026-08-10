using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Charter.Data;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Charter.Orchestration;

/// <summary>
/// The orchestration event types, alongside the agent-produced ones in <see cref="EventTypes"/>.
/// </summary>
/// <remarks>
/// These exist because section 2.3 leaves nowhere else for them to live. The container can restart
/// mid-session, so the orchestrator has no memory: what it knows about a session it must be able to
/// read back out of Postgres. The event stream is already append-only, already per-session, already
/// ordered by <see cref="Event.Seq"/>, and already the thing pane 2 renders — so it is also the
/// orchestrator's own record of what it has done.
/// </remarks>
public static class OrchestrationEventTypes
{
    /// <summary>
    /// Written the instant a backend accepts a session. Its presence is what makes double-dispatch
    /// impossible after a restart: the orchestrator asks the journal, not itself.
    /// </summary>
    public const string SessionDispatched = "session_dispatched";

    /// <summary>
    /// The compensating event for a dispatch that was claimed but never accepted.
    /// </summary>
    /// <remarks>
    /// The claim is written <em>before</em> the backend is called, because the failure Charter cannot
    /// tolerate is dispatching twice, not failing to dispatch once. That leaves one case to undo: the
    /// backend refused, or threw, after the claim was recorded. Events are append-only and the summary
    /// is a fold over them, so the undo is another event rather than a mutation — and the count of
    /// these is the dispatch generation, which is what lets a genuine retry through while a restart
    /// still cannot double-dispatch.
    /// </remarks>
    public const string SessionDispatchFailed = "session_dispatch_failed";

    /// <summary>Section 27.3: no eligible runner, so the session waits with an explanation.</summary>
    public const string SessionQueued = "session_queued";

    /// <summary>A new control-plane instance picked this session up. Records the resume cursor.</summary>
    public const string SessionResumed = "session_resumed";

    /// <summary>Section 11: the cancel button was pressed.</summary>
    public const string SessionCancelRequested = "session_cancel_requested";
}

/// <summary>One row of a session's transcript.</summary>
public sealed record SessionJournalEntry(long Seq, string Type, string Payload, DateTimeOffset CreatedAt);

/// <summary>The result of appending. <paramref name="Appended"/> is false when the event already existed.</summary>
public sealed record JournalAppend(bool Appended, long Seq, Guid EventId);

/// <summary>
/// Everything the orchestrator needs to know about a session, read from its events alone.
/// </summary>
/// <param name="LastSeq">The resume cursor. 0 when the session has no events yet.</param>
/// <param name="DispatchAttempts">How many times a dispatch has been claimed.</param>
/// <param name="DispatchFailures">How many of those a backend refused. Also the dispatch generation.</param>
/// <param name="Runner">Which backend, from the dispatch event.</param>
/// <param name="ExternalReference">
/// The backend's handle — the workflow run URL, most often learned from the runner's own
/// <c>session_started</c> callback rather than from the dispatch, since
/// <c>repository_dispatch</c> returns no run id.
/// </param>
/// <param name="TerminalState">The state the runner last reported, or null while it is still running.</param>
/// <param name="ReportedCostUsd">Summed from <c>cost</c> events, for settlement (sections 11, 34.4).</param>
/// <param name="QueuedExplanation">The last no-eligible-runner explanation shown (section 27.3).</param>
/// <param name="LastResumeSeq">
/// Where the newest resume marker sits. When it is the newest event outright, nothing has happened
/// since the last restart, and a crash-looping container must not fill the transcript saying so.
/// </param>
public sealed record SessionJournalSummary(
    Guid SessionId,
    long LastSeq,
    int DispatchAttempts,
    int DispatchFailures,
    RunnerKind? Runner,
    string? ExternalReference,
    string? TerminalState,
    decimal ReportedCostUsd,
    string? QueuedExplanation,
    long LastResumeSeq = 0)
{
    /// <summary>
    /// True when a backend currently holds this session. The single fact that makes double-dispatch
    /// impossible: a restarted control plane asks the journal, not its own memory, which it has none of.
    /// </summary>
    public bool Dispatched => DispatchAttempts > DispatchFailures;

    /// <summary>
    /// Which dispatch attempt the next one would be. Part of the dispatch idempotency key, so a
    /// restart reuses the key (and is refused) while a recorded failure produces a fresh one.
    /// </summary>
    public int DispatchGeneration => DispatchFailures;

    /// <summary>True when the runner reported a terminal result.</summary>
    public bool TerminalReported => TerminalState is not null;

    /// <summary>
    /// True when the newest thing in the transcript is the last restart's own resume marker.
    /// </summary>
    /// <remarks>
    /// A container that crash-loops recovers repeatedly, and each recovery would otherwise append a
    /// marker saying it had recovered — turning a restart loop into a transcript nobody can read.
    /// One marker per restart is only worth writing if something happened in between.
    /// </remarks>
    public bool ResumedWithNoProgressSince => LastResumeSeq > 0 && LastResumeSeq == LastSeq;

    public static SessionJournalSummary Empty(Guid sessionId)
        => new(sessionId, 0, 0, 0, null, null, null, 0m, null, 0);
}

/// <summary>
/// Append-only access to a session's event stream, with the two properties orchestration depends on:
/// a monotonic sequence, and idempotent appends.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Sequence.</strong> <c>ux_events_session_id_seq</c> makes <see cref="Event.Seq"/> unique per
/// session, so two writers computing <c>max(seq) + 1</c> concurrently cannot both win. The loser gets
/// a unique violation and retries with a fresh maximum. That is deliberately not a lock: the common
/// case is one writer, and paying for a lock on every event of the largest table in the schema
/// (section 5) to serialise a case that resolves itself in one retry is the wrong trade.
/// </para>
/// <para>
/// <strong>Idempotence.</strong> An append may carry a key, from which the event's primary key is
/// derived deterministically. Appending the same key twice is a no-op rather than a duplicate row.
/// This is what makes a runner replaying its stream after a reconnect — or a restarted control plane
/// re-reading a webhook delivery — safe, and it is the property the resumability test asserts.
/// </para>
/// </remarks>
public sealed class SessionJournal
{
    /// <summary>How many times a sequence collision is retried before giving up.</summary>
    public const int MaxSequenceAttempts = 8;

    private const string PrimaryKeyConstraint = "pk_events";
    private const string SequenceConstraint = "ux_events_session_id_seq";

    private static readonly string[] SummaryTypes =
    [
        OrchestrationEventTypes.SessionDispatched,
        OrchestrationEventTypes.SessionDispatchFailed,
        OrchestrationEventTypes.SessionQueued,
        OrchestrationEventTypes.SessionResumed,
        EventTypes.SessionStarted,
        EventTypes.SessionEnded,
        EventTypes.Cost,
    ];

    private readonly CharterDbContext _db;

    public SessionJournal(CharterDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <summary>
    /// Appends one event. Returns the existing row's position, without writing, when
    /// <paramref name="idempotencyKey"/> has already been used for this session.
    /// </summary>
    public async Task<JournalAppend> AppendAsync(
        Guid sessionId,
        string type,
        string payloadJson,
        string? idempotencyKey = null,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);

        var id = idempotencyKey is null
            ? Guid.CreateVersion7()
            : DeterministicEventId(sessionId, idempotencyKey);

        if (idempotencyKey is not null)
        {
            var existing = await _db.Events
                .AsNoTracking()
                .Where(@event => @event.Id == id)
                .Select(@event => (long?)@event.Seq)
                .FirstOrDefaultAsync(cancellationToken);

            if (existing is not null)
            {
                return new JournalAppend(false, existing.Value, id);
            }
        }

        for (var attempt = 1; attempt <= MaxSequenceAttempts; attempt++)
        {
            var next = await LastSeqAsync(sessionId, cancellationToken) + 1;
            var entity = Event.Append(sessionId, next, type, payloadJson, now, id);

            _db.Events.Add(entity);

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                return new JournalAppend(true, next, id);
            }
            catch (DbUpdateException exception) when (Violates(exception, PrimaryKeyConstraint))
            {
                // Somebody else appended this exact event first. That is the idempotency guarantee
                // doing its job, not a failure.
                _db.Entry(entity).State = EntityState.Detached;

                var seq = await _db.Events
                    .AsNoTracking()
                    .Where(@event => @event.Id == id)
                    .Select(@event => (long?)@event.Seq)
                    .FirstOrDefaultAsync(cancellationToken);

                return new JournalAppend(false, seq ?? next, id);
            }
            catch (DbUpdateException exception) when (Violates(exception, SequenceConstraint))
            {
                // Another writer took this sequence number. Re-read the maximum and try again.
                _db.Entry(entity).State = EntityState.Detached;
            }
        }

        throw new InvalidOperationException(
            $"Could not append a '{type}' event to session {sessionId} after {MaxSequenceAttempts} attempts: "
            + "another writer keeps taking the next sequence number.");
    }

    /// <summary>The highest sequence recorded for a session, or 0 when it has no events.</summary>
    public async Task<long> LastSeqAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => await _db.Events
            .AsNoTracking()
            .Where(@event => @event.SessionId == sessionId)
            .MaxAsync(@event => (long?)@event.Seq, cancellationToken) ?? 0L;

    /// <summary>Cursor-paginated read, the way pane 2 does it (section 12).</summary>
    public async Task<IReadOnlyList<SessionJournalEntry>> ReadAsync(
        Guid sessionId,
        long afterSeq = 0,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return await _db.Events
            .AsNoTracking()
            .Where(@event => @event.SessionId == sessionId && @event.Seq > afterSeq)
            .OrderBy(@event => @event.Seq)
            .Take(limit)
            .Select(@event => new SessionJournalEntry(@event.Seq, @event.Type, @event.Payload, @event.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Reconstructs the orchestrator's view of a session from its events.
    /// </summary>
    /// <remarks>
    /// This is the whole of section 2.3's "fully resumable from Postgres alone" in one method. A brand
    /// new process calls it and knows, without having been running when any of it happened, whether
    /// the session was dispatched, to what, how far the transcript got, and how it ended.
    /// </remarks>
    public async Task<SessionJournalSummary> SummarizeAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var rows = await _db.Events
            .AsNoTracking()
            .Where(@event => @event.SessionId == sessionId && SummaryTypes.Contains(@event.Type))
            .OrderBy(@event => @event.Seq)
            .Select(@event => new SessionJournalEntry(@event.Seq, @event.Type, @event.Payload, @event.CreatedAt))
            .ToListAsync(cancellationToken);

        var lastSeq = await LastSeqAsync(sessionId, cancellationToken);
        var summary = SessionJournalSummary.Empty(sessionId) with { LastSeq = lastSeq };

        foreach (var row in rows)
        {
            var payload = TryParse(row.Payload);

            switch (row.Type)
            {
                case OrchestrationEventTypes.SessionDispatched:
                    summary = summary with
                    {
                        DispatchAttempts = summary.DispatchAttempts + 1,
                        Runner = ParseRunner(Text(payload, "runner")) ?? summary.Runner,
                        ExternalReference = Text(payload, "external_ref") ?? summary.ExternalReference,
                    };
                    break;

                case OrchestrationEventTypes.SessionDispatchFailed:
                    summary = summary with { DispatchFailures = summary.DispatchFailures + 1 };
                    break;

                case EventTypes.SessionStarted:
                    summary = summary with
                    {
                        ExternalReference = Text(payload, "run_url") ?? summary.ExternalReference,
                    };
                    break;

                case EventTypes.SessionEnded:
                    summary = summary with { TerminalState = Text(payload, "state") ?? "completed" };
                    break;

                case OrchestrationEventTypes.SessionQueued:
                    summary = summary with { QueuedExplanation = Text(payload, "explanation") };
                    break;

                case OrchestrationEventTypes.SessionResumed:
                    summary = summary with { LastResumeSeq = row.Seq };
                    break;

                case EventTypes.Cost:
                    summary = summary with { ReportedCostUsd = summary.ReportedCostUsd + Money(payload, "usd") };
                    break;
            }
        }

        return summary;
    }

    /// <summary>
    /// A stable event id for <paramref name="key"/> within <paramref name="sessionId"/>.
    /// </summary>
    /// <remarks>
    /// A name-based UUID over the session and the key, in the shape RFC 4122 version 5 describes. The
    /// point is only that it is a pure function of its inputs: two control-plane instances, or one
    /// instance either side of a restart, derive the same id for the same event and the primary key
    /// refuses the second copy.
    /// </remarks>
    public static Guid DeterministicEventId(Guid sessionId, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"charter.event:{sessionId:D}:{key}"));
        var id = new byte[16];
        Array.Copy(bytes, id, 16);

        id[6] = (byte)((id[6] & 0x0F) | 0x50);
        id[8] = (byte)((id[8] & 0x3F) | 0x80);

        return new Guid(id);
    }

    private static bool Violates(DbUpdateException exception, string constraint)
        => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
           && string.Equals(postgres.ConstraintName, constraint, StringComparison.Ordinal);

    private static JsonObject? TryParse(string payload)
    {
        try
        {
            return JsonNode.Parse(payload) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonObject? payload, string property)
        => payload?[property] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0
            ? text
            : null;

    private static decimal Money(JsonObject? payload, string property)
    {
        if (payload?[property] is not JsonValue value)
        {
            return 0m;
        }

        if (value.TryGetValue<decimal>(out var amount))
        {
            return amount;
        }

        return value.TryGetValue<string>(out var text)
               && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;
    }

    private static RunnerKind? ParseRunner(string? value) => value switch
    {
        "agent" => RunnerKind.Agent,
        "github-actions" => RunnerKind.GitHubActions,
        "docker" => RunnerKind.Docker,
        _ => null,
    };

    /// <summary>The wire spelling of a backend, matching <c>CHARTER_RUNNER</c> and the database.</summary>
    public static string WireName(RunnerKind kind) => kind switch
    {
        RunnerKind.Agent => "agent",
        RunnerKind.GitHubActions => "github-actions",
        _ => "docker",
    };
}
