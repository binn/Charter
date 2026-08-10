using Charter.Domain;

namespace Charter.Auth.Authorization;

/// <summary>
/// Everything the authorisation rules are allowed to know about the person asking.
/// </summary>
/// <remarks>
/// <para>
/// Section 7.2. There is deliberately no organisation <em>mode</em> here, and no way to reach one:
/// personal mode is an <see cref="Organization"/> with one <see cref="Member"/> holding every role,
/// so the rules below cannot branch on it even if somebody wanted them to. A one-member instance and
/// a two-hundred-member instance produce the same snapshot shape and travel the same code path;
/// only the seeded rows differ.
/// </para>
/// <para>
/// Roles are additive (section 7.1), so this carries the whole array rather than a single "the"
/// role, and every check below asks <see cref="HasRole"/> rather than comparing equality.
/// </para>
/// </remarks>
public sealed record MemberSnapshot
{
    public required Guid MemberId { get; init; }

    public required Guid OrgId { get; init; }

    public required Guid UserId { get; init; }

    /// <summary>Additive (section 7.1). A member may hold several.</summary>
    public required IReadOnlyList<MemberRole> Roles { get; init; }

    /// <summary>The section 26.10 capabilities that are deliberately not roles.</summary>
    public IReadOnlyList<MemberCapability> Capabilities { get; init; } = [];

    public bool HasRole(MemberRole role) => Roles.Contains(role);

    /// <summary>True when the member holds at least one of <paramref name="roles"/>.</summary>
    public bool HasAnyRole(params ReadOnlySpan<MemberRole> roles)
    {
        foreach (var role in roles)
        {
            if (Roles.Contains(role))
            {
                return true;
            }
        }

        return false;
    }

    public bool HasCapability(MemberCapability capability) => Capabilities.Contains(capability);

    public static MemberSnapshot From(Member member)
    {
        ArgumentNullException.ThrowIfNull(member);

        return new MemberSnapshot
        {
            MemberId = member.Id,
            OrgId = member.OrgId,
            UserId = member.UserId,
            Roles = member.Roles,
            Capabilities = member.Capabilities,
        };
    }
}

/// <summary>One row of <c>repo_scopes</c>, addressed either to a member or to a role (section 7.3).</summary>
public sealed record RepoScopeGrant(Guid? MemberId, MemberRole? Role, bool CanRequest)
{
    public static RepoScopeGrant From(RepoScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new RepoScopeGrant(scope.MemberId, scope.Role, scope.CanRequest);
    }
}

/// <summary>
/// A repository and the complete set of scope rows addressed at it.
/// </summary>
/// <remarks>
/// The grants are carried in full rather than pre-filtered, because "there is no row" is the answer
/// deny-by-default depends on being able to observe. A caller that passed only the matching rows
/// could not tell an absent grant from an unloaded one.
/// </remarks>
public sealed record RepoSnapshot
{
    public required Guid RepoId { get; init; }

    public required Guid OrgId { get; init; }

    /// <summary>Section 9: a repository is requestable by nobody until it has earned <c>Ready</c>.</summary>
    public required RepoStatus Status { get; init; }

    public IReadOnlyList<RepoScopeGrant> Grants { get; init; } = [];

    public static RepoSnapshot From(Repo repo, IEnumerable<RepoScope> scopes)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(scopes);

        return new RepoSnapshot
        {
            RepoId = repo.Id,
            OrgId = repo.OrgId,
            Status = repo.Status,
            Grants = [.. scopes.Select(RepoScopeGrant.From)],
        };
    }
}

/// <summary>The part of a <see cref="Spec"/> the spend gate needs (section 7.5).</summary>
public sealed record SpecSnapshot
{
    public required Guid SpecId { get; init; }

    public required Guid OrgId { get; init; }

    public required Guid RepoId { get; init; }

    /// <summary>The person who filed the underlying request.</summary>
    public required Guid RequesterUserId { get; init; }

    public required bool IsApproved { get; init; }
}

/// <summary>The part of a <see cref="Session"/> the section 7.4 visibility rules need.</summary>
public sealed record SessionSnapshot
{
    public required Guid SessionId { get; init; }

    public required Guid OrgId { get; init; }

    public required Guid RepoId { get; init; }

    /// <summary>The person who filed the request this session builds.</summary>
    public required Guid RequesterUserId { get; init; }
}
