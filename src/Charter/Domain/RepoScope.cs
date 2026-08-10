namespace Charter.Domain;

/// <summary>
/// Who may file against a repository (section 7.3, guardrail 1). Deny by default: a newly connected
/// repo is requestable by nobody, so the absence of a row is a refusal.
/// </summary>
/// <remarks>
/// A grant is addressed either to one member or to a role, never both. The exclusivity is enforced
/// by a check constraint as well as by this factory, because the database is the boundary that a
/// future code path cannot forget.
/// </remarks>
public sealed class RepoScope
{
    private RepoScope()
    {
    }

    private RepoScope(
        Guid id,
        Guid repoId,
        Guid? memberId,
        MemberRole? role,
        bool canRequest,
        DateTimeOffset createdAt)
    {
        Id = id;
        RepoId = repoId;
        MemberId = memberId;
        Role = role;
        CanRequest = canRequest;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid RepoId { get; private set; }

    public Guid? MemberId { get; private set; }

    public MemberRole? Role { get; private set; }

    public bool CanRequest { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static RepoScope ForMember(
        Guid repoId,
        Guid memberId,
        bool canRequest = true,
        DateTimeOffset? now = null,
        Guid? id = null)
        => new(id ?? Guid.CreateVersion7(), repoId, memberId, null, canRequest, DomainTime.Resolve(now));

    public static RepoScope ForRole(
        Guid repoId,
        MemberRole role,
        bool canRequest = true,
        DateTimeOffset? now = null,
        Guid? id = null)
        => new(id ?? Guid.CreateVersion7(), repoId, null, role, canRequest, DomainTime.Resolve(now));

    public void Allow() => CanRequest = true;

    public void Deny() => CanRequest = false;
}
