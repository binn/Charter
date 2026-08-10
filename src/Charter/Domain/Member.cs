using System.Runtime.Serialization;

namespace Charter.Domain;

/// <summary>The four roles of section 7.1. Roles are additive; a member may hold several.</summary>
public enum MemberRole
{
    /// <summary>Text box, refinement, status thread, preview button. Never a repo name or a diff.</summary>
    Requester,

    /// <summary>Gates spend, not code quality (section 7.5).</summary>
    Approver,

    /// <summary>Sessions, transcripts, diffs, steering, repo and scope configuration.</summary>
    Engineer,

    /// <summary>Members, roles, budgets, repo connections, model selection, audit log.</summary>
    Admin,
}

/// <summary>A user's membership of an organisation, carrying their roles (section 5, section 7.1).</summary>
/// <remarks>
/// Roles are a Postgres <c>text[]</c> rather than a join table. Section 7.1 makes them additive and
/// they are always read together with the member, so an array is both the natural shape and the one
/// that keeps the permission check a single row read. Postgres containment operators keep
/// "members holding the approver role" an indexable query.
/// </remarks>
public sealed class Member
{
    private Member()
    {
    }

    private Member(
        Guid id,
        Guid orgId,
        Guid userId,
        IReadOnlyList<MemberRole> roles,
        IReadOnlyList<MemberCapability> capabilities,
        DateTimeOffset createdAt)
    {
        Id = id;
        OrgId = orgId;
        UserId = userId;
        Roles = roles;
        Capabilities = capabilities;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid OrgId { get; private set; }

    public Guid UserId { get; private set; }

    public IReadOnlyList<MemberRole> Roles { get; private set; } = [];

    /// <summary>Capabilities that are deliberately not roles, such as repo creation (section 26.10).</summary>
    public IReadOnlyList<MemberCapability> Capabilities { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Every role, for the single member of a personal-mode organisation (section 7.2).</summary>
    public static IReadOnlyList<MemberRole> AllRoles { get; } =
        [MemberRole.Requester, MemberRole.Approver, MemberRole.Engineer, MemberRole.Admin];

    public static Member Create(
        Guid orgId,
        Guid userId,
        IEnumerable<MemberRole> roles,
        IEnumerable<MemberCapability>? capabilities = null,
        DateTimeOffset? now = null,
        Guid? id = null)
    {
        ArgumentNullException.ThrowIfNull(roles);

        var normalized = Normalize(roles);
        if (normalized.Count == 0)
        {
            throw new ArgumentException("A member must hold at least one role.", nameof(roles));
        }

        IReadOnlyList<MemberCapability> grantedCapabilities = capabilities is null
            ? []
            : [.. capabilities.Distinct().Order()];

        return new Member(
            id ?? Guid.CreateVersion7(),
            orgId,
            userId,
            normalized,
            grantedCapabilities,
            DomainTime.Resolve(now));
    }

    public bool HasRole(MemberRole role) => Roles.Contains(role);

    public bool HasCapability(MemberCapability capability) => Capabilities.Contains(capability);

    public void GrantCapability(MemberCapability capability)
    {
        if (!Capabilities.Contains(capability))
        {
            Capabilities = [.. Capabilities.Append(capability).Distinct().Order()];
        }
    }

    public void RevokeCapability(MemberCapability capability)
        => Capabilities = [.. Capabilities.Where(existing => existing != capability)];

    /// <summary>Additive by design (section 7.1); granting a role a member already holds is a no-op.</summary>
    public void GrantRole(MemberRole role)
    {
        if (!Roles.Contains(role))
        {
            Roles = Normalize([.. Roles, role]);
        }
    }

    public void RevokeRole(MemberRole role)
    {
        if (!Roles.Contains(role))
        {
            return;
        }

        var remaining = Normalize(Roles.Where(existing => existing != role));
        if (remaining.Count == 0)
        {
            throw new InvalidOperationException("A member must retain at least one role.");
        }

        Roles = remaining;
    }

    private static IReadOnlyList<MemberRole> Normalize(IEnumerable<MemberRole> roles)
        => [.. roles.Distinct().Order()];
}

/// <summary>
/// The capability of section 26.10 that is not a role: repo creation is a privilege escalation and
/// is granted deliberately.
/// </summary>
public enum MemberCapability
{
    [EnumMember(Value = "can_create_repo")]
    CanCreateRepo,
}
