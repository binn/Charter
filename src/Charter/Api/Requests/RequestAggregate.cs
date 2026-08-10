using Charter.Api.Contracts;
using Charter.Api.Projects;
using Charter.Domain;
using Charter.VersionControl;

namespace Charter.Api.Requests;

/// <summary>
/// Every row one request's detail is projected from, loaded once.
/// </summary>
/// <remarks>
/// The projection is a pure function of this record plus a
/// <see cref="Charter.Auth.Authorization.SessionVisibility"/>, which is what makes the section 7.4
/// omission rule testable without a database: a test can hand it a requester's visibility and read
/// the bytes that would go on the wire.
/// </remarks>
public sealed record RequestAggregate
{
    public required Request Request { get; init; }

    public required Repo Repo { get; init; }

    public required RepoProjectProfile Profile { get; init; }

    /// <summary>The current specification, if refinement has produced one.</summary>
    public Spec? Spec { get; init; }

    /// <summary>The most recent session. Multiple sessions collapse into one thread (section 11).</summary>
    public Session? Session { get; init; }

    public IReadOnlyList<Milestone> Milestones { get; init; } = [];

    /// <summary>Ordered by <see cref="Event.Seq"/>. Only loaded when the viewer may see pane 2.</summary>
    public IReadOnlyList<Event> Events { get; init; } = [];

    public IReadOnlyList<VerificationArtifact> Artifacts { get; init; } = [];

    public ChangeRequest? ChangeRequest { get; init; }

    /// <summary>
    /// What the repository's provider calls a change request (change spec 001 part A.2).
    /// </summary>
    /// <remarks>
    /// Carried on the aggregate rather than looked up in the projection, so the projection stays a
    /// pure function of rows and a test can assert the wording without a provider registry.
    /// </remarks>
    public string ChangeRequestTerm { get; init; } = VersionControlTerms.ChangeRequestDefault.ChangeRequest;

    /// <summary>The short form, for the chip: <c>PR</c>, <c>MR</c>, <c>CL</c>.</summary>
    public string ChangeRequestTermShort { get; init; } =
        VersionControlTerms.ChangeRequestDefault.ChangeRequestShort;

    /// <summary>The refinement conversation, already projected. See <see cref="IRefinementThreadStore"/>.</summary>
    public IReadOnlyList<RefinementMessageResponse> RefinementMessages { get; init; } = [];

    /// <summary>Who approved the spec, by display name. Never an id (section 7.1).</summary>
    public string? ApprovedByName { get; init; }

    /// <summary>Section 6: <em>Waiting on {approver} to approve</em>.</summary>
    public string? AwaitingApprovalFrom { get; init; }

    public FeedbackResponse? Feedback { get; init; }

    /// <summary>True when there are older events than the ones carried here (section 12 cursoring).</summary>
    public bool HasEarlierEvents { get; init; }

    /// <summary>
    /// Every event in the session, so pane 2 can say <em>"of 12,480"</em>.
    /// </summary>
    /// <remarks>
    /// A count rather than a length: <see cref="Events"/> is one page, and the two disagree on every
    /// page but the first. Zero for a viewer who may not see pane 2 at all, because the rows are not
    /// counted either.
    /// </remarks>
    public long TotalEvents { get; init; }

    /// <summary>
    /// Every repository path this session wrote to, risk-ordered by the caller.
    /// </summary>
    /// <remarks>
    /// Loaded separately from <see cref="Events"/> because pane 3 lists the whole change while pane 2
    /// shows one page of it. Deriving the file list from a page would quietly hide files whose only
    /// write fell outside it.
    /// </remarks>
    public IReadOnlyList<string> ChangedPaths { get; init; } = [];

    /// <summary>Section 14's recap row, when one has been generated and the viewer may see it.</summary>
    public Recap? Recap { get; init; }

    /// <summary>
    /// Who took the session over (section 7.5), by display name.
    /// </summary>
    /// <remarks>
    /// Read from the audit log rather than a column: taking over is an action attributable to a named
    /// human, which is exactly what <see cref="AuditLog"/> is for, and the session row records that
    /// it happened rather than who did it.
    /// </remarks>
    public string? HandedOffByName { get; init; }
}
