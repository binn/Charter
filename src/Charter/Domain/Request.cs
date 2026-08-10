namespace Charter.Domain;

/// <summary>
/// The state machine of section 6, as it reads on the request thread. One thread per request,
/// forever (section 11), so the request carries the state a requester sees.
/// </summary>
public enum RequestStatus
{
    Draft,

    /// <summary>Shown as <em>Let's figure out what you need</em>.</summary>
    Refining,

    /// <summary>Shown as <em>Waiting on {approver} to approve</em>; skipped when auto-dispatch applies.</summary>
    SpecReady,

    Rejected,

    /// <summary>Queued and Running both read as <em>Building this now</em>.</summary>
    Queued,

    Running,

    /// <summary>The one mid-flight state that notifies: <em>Question for you</em>.</summary>
    NeedsInput,

    PrOpen,

    /// <summary>The second state that notifies: <em>Ready to try</em>.</summary>
    PreviewReady,

    InReview,

    Merged,

    /// <summary>
    /// Terminal, and a success: the agent ran, ran correctly, and found nothing to change.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Failed"/> because it is a different event, and because
    /// <see cref="Failed"/>'s copy — <em>this turned out to be bigger than expected</em> — would be
    /// actively wrong here. Section 6: the usual cause is that what the requester asked for already
    /// works, which section 10b treats as the cheapest possible outcome; finding that out one step
    /// later does not turn it into a failure. Nothing is notified and no engineer is paged.
    /// </remarks>
    NoChangesNeeded,

    /// <summary>Shown as <em>This turned out to be bigger than expected</em>. Never a stack trace.</summary>
    Failed,

    Cancelled,

    Stale,
}

/// <summary>What a person actually asked for, in their own words (section 5).</summary>
/// <remarks>
/// <see cref="RawText"/> is never handed to the agent. The agent sees the refined, human-approved
/// <see cref="Spec"/>, which is model-authored — refinement is the sanitisation boundary of
/// section 16 and the strongest security property Charter has.
/// </remarks>
public sealed class Request
{
    private Request()
    {
    }

    private Request(
        Guid id,
        Guid orgId,
        Guid repoId,
        Guid requesterId,
        string rawText,
        string? templateId,
        RequestStatus status,
        DateTimeOffset createdAt)
    {
        Id = id;
        OrgId = orgId;
        RepoId = repoId;
        RequesterId = requesterId;
        RawText = rawText;
        TemplateId = templateId;
        Status = status;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid OrgId { get; private set; }

    public Guid RepoId { get; private set; }

    public Guid RequesterId { get; private set; }

    /// <summary>Untrusted input. Never reaches the agent (section 16).</summary>
    public string RawText { get; private set; } = string.Empty;

    /// <summary>The <c>.charter/templates/</c> entry the requester picked, if any (section 8).</summary>
    public string? TemplateId { get; private set; }

    public RequestStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Request File(
        Guid orgId,
        Guid repoId,
        Guid requesterId,
        string rawText,
        string? templateId = null,
        DateTimeOffset? now = null,
        Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawText);

        return new Request(
            id ?? Guid.CreateVersion7(),
            orgId,
            repoId,
            requesterId,
            rawText,
            string.IsNullOrWhiteSpace(templateId) ? null : templateId.Trim(),
            RequestStatus.Draft,
            DomainTime.Resolve(now));
    }

    public void TransitionTo(RequestStatus status, DateTimeOffset? now = null)
    {
        Status = status;
        UpdatedAt = DomainTime.Resolve(now);
    }
}
