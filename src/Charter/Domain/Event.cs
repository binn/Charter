namespace Charter.Domain;

/// <summary>
/// The event types Charter itself understands. Adapters are data, not code (section 12b), so
/// <see cref="Event.Type"/> stays an open string: an adapter emitting a type this build has never
/// heard of must be stored, not rejected.
/// </summary>
public static class EventTypes
{
    public const string Message = "message";
    public const string ToolUse = "tool_use";
    public const string FileWrite = "file_write";
    public const string Command = "command";
    public const string CheckResult = "check_result";
    public const string NetworkCall = "network_call";
    public const string Error = "error";
    public const string Cost = "cost";
    public const string SessionStarted = "session_started";
    public const string SessionEnded = "session_ended";
}

/// <summary>
/// The streamed transcript: append-only, with a per-session monotonic <see cref="Seq"/> (section 5).
/// </summary>
/// <remarks>
/// This is the largest table by orders of magnitude, and it has exactly one hot access pattern —
/// fetch a session's events in <see cref="Seq"/> order, cursor-paginated for the virtualized
/// pane 2 (section 12). The unique index on <c>(session_id, seq)</c> serves both the ordering and
/// the monotonicity guarantee, and the cursor is the last <see cref="Seq"/> seen rather than an
/// offset, so pagination cost does not grow with the transcript.
/// <para>
/// Nothing here is ever updated. Section 20 prunes by retention policy instead, and section 25
/// leaves monthly partitioning open as a scale decision.
/// </para>
/// </remarks>
public sealed class Event
{
    private Event()
    {
    }

    private Event(Guid id, Guid sessionId, long seq, string type, string payload, DateTimeOffset createdAt)
    {
        Id = id;
        SessionId = sessionId;
        Seq = seq;
        Type = type;
        Payload = payload;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid SessionId { get; private set; }

    /// <summary>Monotonic within a session, starting at 1. The pagination cursor.</summary>
    public long Seq { get; private set; }

    public string Type { get; private set; } = string.Empty;

    /// <summary>
    /// jsonb. Section 19 warns that this carries repo content and business context: log metadata,
    /// never the body, unless <c>CHARTER_LOG_INCLUDE_TRANSCRIPTS</c> is deliberately set.
    /// </summary>
    public string Payload { get; private set; } = "{}";

    public DateTimeOffset CreatedAt { get; private set; }

    public static Event Append(
        Guid sessionId,
        long seq,
        string type,
        string payload,
        DateTimeOffset? now = null,
        Guid? id = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(seq, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        return new Event(
            id ?? Guid.CreateVersion7(),
            sessionId,
            seq,
            type.Trim(),
            payload,
            DomainTime.Resolve(now));
    }
}
