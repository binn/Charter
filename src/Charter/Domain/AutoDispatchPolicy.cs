using System.Runtime.Serialization;

namespace Charter.Domain;

/// <summary>The project types of section 27.2, which decide what verification a session can produce.</summary>
public enum ProjectType
{
    Web,

    [EnumMember(Value = "api")]
    Api,

    MobileIos,

    MobileExpo,

    DesktopWin,

    DesktopMac,

    [EnumMember(Value = "maui")]
    Maui,

    Unity,

    GameServer,

    Embedded,

    Library,
}

/// <summary>
/// Conditional auto-dispatch of <c>SpecReady → Queued</c> (section 7.5): trust this person, up to
/// this much, in this area.
/// </summary>
/// <remarks>
/// Resolution is most-specific-wins — user override, then role, then repo default, then org default
/// — which <see cref="Specificity"/> ranks. This governs the spend gate only. The merge gate lives
/// in GitHub branch protection and is not represented in Charter's data model at all.
/// </remarks>
public sealed class AutoDispatchPolicy
{
    private AutoDispatchPolicy()
    {
    }

    private AutoDispatchPolicy(Guid id, Guid orgId, DateTimeOffset createdAt)
    {
        Id = id;
        OrgId = orgId;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid OrgId { get; private set; }

    public Guid? RepoId { get; private set; }

    public MemberRole? Role { get; private set; }

    public Guid? UserId { get; private set; }

    public bool Enabled { get; private set; }

    /// <summary>Per session. Null means the policy imposes no ceiling of its own.</summary>
    public decimal? MaxCostUsd { get; private set; }

    public int? MaxConcurrentSessions { get; private set; }

    /// <summary>A subset of the repo scope, never a superset (section 7.5).</summary>
    public IReadOnlyList<string> AllowedPaths { get; private set; } = [];

    public IReadOnlyList<ProjectType> ProjectTypes { get; private set; } = [];

    /// <summary>Spend above this acquires a human decision instead of dispatching (section 34.5).</summary>
    public decimal? RequireApprovalAboveUsd { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Ranks the policy for most-specific-wins resolution: user override beats role, which beats a
    /// repo default, which beats the org default. Not persisted — it is a pure function of the scope
    /// columns, and storing it would let the two disagree.
    /// </summary>
    public int Specificity
        => (UserId is not null ? 8 : 0) + (Role is not null ? 4 : 0) + (RepoId is not null ? 2 : 0);

    public static AutoDispatchPolicy Create(
        Guid orgId,
        bool enabled,
        Guid? repoId = null,
        MemberRole? role = null,
        Guid? userId = null,
        decimal? maxCostUsd = null,
        int? maxConcurrentSessions = null,
        IEnumerable<string>? allowedPaths = null,
        IEnumerable<ProjectType>? projectTypes = null,
        decimal? requireApprovalAboveUsd = null,
        DateTimeOffset? now = null,
        Guid? id = null)
    {
        if (maxCostUsd is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCostUsd), maxCostUsd, "A cost ceiling cannot be negative.");
        }

        if (requireApprovalAboveUsd is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requireApprovalAboveUsd),
                requireApprovalAboveUsd,
                "An approval threshold cannot be negative.");
        }

        if (maxConcurrentSessions is < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrentSessions),
                maxConcurrentSessions,
                "A concurrency limit must be at least one.");
        }

        return new AutoDispatchPolicy(id ?? Guid.CreateVersion7(), orgId, DomainTime.Resolve(now))
        {
            RepoId = repoId,
            Role = role,
            UserId = userId,
            Enabled = enabled,
            MaxCostUsd = maxCostUsd,
            MaxConcurrentSessions = maxConcurrentSessions,
            AllowedPaths = allowedPaths is null ? [] : [.. allowedPaths],
            ProjectTypes = projectTypes is null ? [] : [.. projectTypes.Distinct()],
            RequireApprovalAboveUsd = requireApprovalAboveUsd,
        };
    }

    public void Enable(DateTimeOffset? now = null)
    {
        Enabled = true;
        UpdatedAt = DomainTime.Resolve(now);
    }

    public void Disable(DateTimeOffset? now = null)
    {
        Enabled = false;
        UpdatedAt = DomainTime.Resolve(now);
    }
}
