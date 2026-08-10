namespace Charter.Domain;

/// <summary>
/// Section 11: <em>"Feedback is two buttons — Works / Not quite."</em>
/// </summary>
/// <remarks>
/// Two values, and there will not be a third. A five-point scale, a severity field or a free-text
/// requirement all push the requester towards writing a bug report, which section 11 is explicit
/// about not making them do.
/// </remarks>
public enum FeedbackVerdict
{
    /// <summary>It does what was agreed.</summary>
    Works,

    /// <summary>It does not. Opens a box and becomes a new session on the same spec.</summary>
    NotQuite,
}

/// <summary>
/// What the requester said when they tried it (section 11).
/// </summary>
/// <remarks>
/// <para>
/// A row per verdict rather than a column on the request, because one thread carries several
/// sessions and a request that was <em>not quite</em> right twice before working is a history worth
/// keeping. The status thread renders the latest; the rest is the record of how many rounds it took.
/// </para>
/// <para>
/// <see cref="Note"/> is untrusted text, in the same sense as <c>Request.RawText</c>: a person typed
/// it. Nothing here hands it to an agent — the "not quite" job carries it as queue payload, and
/// whatever picks that up runs it through refinement like any other requester input (section 16).
/// </para>
/// </remarks>
public sealed class RequestFeedback
{
    /// <summary>As long as a request's raw text. Long enough for a paragraph, not a novel.</summary>
    public const int MaxNoteLength = 8_000;

    private RequestFeedback()
    {
    }

    private RequestFeedback(
        Guid id,
        Guid requestId,
        Guid? sessionId,
        Guid submittedBy,
        FeedbackVerdict verdict,
        string? note,
        DateTimeOffset createdAt)
    {
        Id = id;
        RequestId = requestId;
        SessionId = sessionId;
        SubmittedBy = submittedBy;
        Verdict = verdict;
        Note = note;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid RequestId { get; private set; }

    /// <summary>The session that produced the thing being judged, when there was one.</summary>
    public Guid? SessionId { get; private set; }

    /// <summary>The person who pressed the button. Only the requester may (section 11).</summary>
    public Guid SubmittedBy { get; private set; }

    public FeedbackVerdict Verdict { get; private set; }

    /// <summary>Optional free text captured after "Not quite". Never demanded.</summary>
    public string? Note { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Records a verdict.</summary>
    /// <exception cref="ArgumentException">The note is longer than intake accepts.</exception>
    public static RequestFeedback Record(
        Guid requestId,
        Guid submittedBy,
        FeedbackVerdict verdict,
        Guid? sessionId = null,
        string? note = null,
        DateTimeOffset? now = null,
        Guid? id = null)
    {
        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        if (trimmed is { Length: > MaxNoteLength })
        {
            throw new ArgumentException(
                "That is longer than we can take in one go.",
                nameof(note));
        }

        return new RequestFeedback(
            id ?? Guid.CreateVersion7(),
            requestId,
            sessionId,
            submittedBy,
            verdict,
            trimmed,
            DomainTime.Resolve(now));
    }
}
