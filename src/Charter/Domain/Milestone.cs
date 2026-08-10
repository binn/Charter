namespace Charter.Domain;

/// <summary>
/// The four milestones promoted into pane 1 (section 11). A small, fixed, requester-facing
/// vocabulary is the point: the raw transcript stays in the engineer view.
/// </summary>
public enum MilestoneLabel
{
    /// <summary><em>Understanding the current setup</em>.</summary>
    UnderstandingSetup,

    /// <summary><em>Making changes</em>.</summary>
    MakingChanges,

    /// <summary><em>Checking it works</em>.</summary>
    CheckingItWorks,

    /// <summary><em>Putting it together</em>.</summary>
    PuttingItTogether,
}

/// <summary>
/// A promoted, requester-facing subset of <see cref="Event"/> (section 5), carrying the link that
/// makes the panes one app rather than three: clicking a milestone scrolls pane 2 to the event that
/// produced it (section 12).
/// </summary>
public sealed class Milestone
{
    private Milestone()
    {
    }

    private Milestone(Guid id, Guid sessionId, Guid eventId, MilestoneLabel label, DateTimeOffset createdAt)
    {
        Id = id;
        SessionId = sessionId;
        EventId = eventId;
        Label = label;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid SessionId { get; private set; }

    public Guid EventId { get; private set; }

    public MilestoneLabel Label { get; private set; }

    /// <summary>One sentence of teaching, generated lazily and billed separately (section 13).</summary>
    public string? AnnotationMd { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Milestone Promote(
        Guid sessionId,
        Guid eventId,
        MilestoneLabel label,
        DateTimeOffset? now = null,
        Guid? id = null)
        => new(id ?? Guid.CreateVersion7(), sessionId, eventId, label, DomainTime.Resolve(now));

    public void Annotate(string annotationMd)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(annotationMd);
        AnnotationMd = annotationMd;
    }
}
