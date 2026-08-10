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
}
