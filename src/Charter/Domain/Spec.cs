namespace Charter.Domain;

/// <summary>
/// The structured specification of sections 5 and 10b — the single source of truth, with two
/// renderings.
/// </summary>
/// <remarks>
/// The requester view renders <see cref="Title"/>, <see cref="Outcome"/> and
/// <see cref="AcceptanceCriteria"/>; the engineer view renders everything. The acceptance criteria
/// are authored in plain language and shared verbatim by both views, because they are the contract:
/// if the two renderings can drift, <em>"the spec said X"</em> stops meaning anything.
/// </remarks>
public sealed class Spec
{
    private Spec()
    {
    }

    private Spec(
        Guid id,
        Guid requestId,
        int version,
        string title,
        string outcome,
        string bodyMd,
        string acceptanceCriteria,
        DateTimeOffset createdAt)
    {
        Id = id;
        RequestId = requestId;
        Version = version;
        Title = title;
        Outcome = outcome;
        BodyMd = bodyMd;
        AcceptanceCriteria = acceptanceCriteria;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid RequestId { get; private set; }

    /// <summary>Specs are revised rather than edited in place; a fork gets the next version.</summary>
    public int Version { get; private set; }

    public string Title { get; private set; } = string.Empty;

    /// <summary>Plain language: what the requester will see change (section 10b).</summary>
    public string Outcome { get; private set; } = string.Empty;

    public string BodyMd { get; private set; } = string.Empty;

    /// <summary>
    /// A jsonb array of plain-language criteria. Rendered verbatim as the requester's
    /// "what to check" list (section 27.7) — never regenerated.
    /// </summary>
    public string AcceptanceCriteria { get; private set; } = "[]";

    /// <summary>Engineer-facing. Never rendered in the requester view.</summary>
    public string? TechnicalApproach { get; private set; }

    /// <summary>jsonb <c>{ files, paths }</c> (section 10b).</summary>
    public string? Scope { get; private set; }

    /// <summary>jsonb array of risks (section 10b).</summary>
    public string? Risks { get; private set; }

    /// <summary>jsonb array of open questions. A spec with any of these must not dispatch.</summary>
    public string? OpenQuestions { get; private set; }

    public Guid? ApprovedBy { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsApproved => ApprovedAt is not null;

    public static Spec Draft(
        Guid requestId,
        int version,
        string title,
        string outcome,
        string bodyMd,
        string acceptanceCriteria,
        string? technicalApproach = null,
        string? scope = null,
        string? risks = null,
        string? openQuestions = null,
        DateTimeOffset? now = null,
        Guid? id = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        ArgumentException.ThrowIfNullOrWhiteSpace(bodyMd);
        ArgumentException.ThrowIfNullOrWhiteSpace(acceptanceCriteria);

        return new Spec(
            id ?? Guid.CreateVersion7(),
            requestId,
            version,
            title.Trim(),
            outcome,
            bodyMd,
            acceptanceCriteria,
            DomainTime.Resolve(now))
        {
            TechnicalApproach = technicalApproach,
            Scope = scope,
            Risks = risks,
            OpenQuestions = openQuestions,
        };
    }

    /// <summary>
    /// The spend gate of section 7.5, and nothing else. Approving a spec says the work is worth
    /// burning tokens on; it says nothing about whether the resulting code may ship.
    /// </summary>
    public void Approve(Guid approverId, DateTimeOffset? now = null)
    {
        if (IsApproved)
        {
            throw new InvalidOperationException("This specification has already been approved.");
        }

        ApprovedBy = approverId;
        ApprovedAt = DomainTime.Resolve(now);
    }
}
