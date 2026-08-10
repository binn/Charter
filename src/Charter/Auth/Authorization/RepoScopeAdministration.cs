using Charter.Auth.Audit;
using Charter.Data;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;

namespace Charter.Auth.Authorization;

/// <summary>
/// The only way a repository stops being requestable by nobody (section 7.3).
/// </summary>
/// <remarks>
/// <para>
/// Deny-by-default is only structural if there is one place that can undo it. Connecting a repository
/// writes no scope rows at all, so the absence of a grant is the refusal; this service is the single
/// method that writes one, it requires the admin role, and it audits every grant and revocation.
/// A future code path that wants to make a repository requestable has to come through here.
/// </para>
/// <para>
/// Section 7.2: <see cref="GrantOnConnectAsync"/> is what a personal instance uses when its one
/// member connects their first repository, and it is the same method an organisation's admin uses
/// when connecting a repository for an engineer. There is no personal-mode branch, and there is no
/// seeded grant that anticipates a repository that does not exist yet.
/// </para>
/// </remarks>
public interface IRepoScopeAdministration
{
    /// <summary>Grants or withholds one member's ability to file against one repository.</summary>
    Task<AuthorizationDecision> SetMemberScopeAsync(
        MemberSnapshot actor,
        Guid repoId,
        Guid targetMemberId,
        bool canRequest,
        CancellationToken cancellationToken = default);

    /// <summary>Grants or withholds a whole role's ability to file against one repository.</summary>
    Task<AuthorizationDecision> SetRoleScopeAsync(
        MemberSnapshot actor,
        Guid repoId,
        MemberRole role,
        bool canRequest,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The one grant a newly connected repository gets: the person who connected it. Everyone else
    /// is added deliberately, one row at a time.
    /// </summary>
    Task<AuthorizationDecision> GrantOnConnectAsync(
        MemberSnapshot connector,
        Guid repoId,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IRepoScopeAdministration" />
public sealed class RepoScopeAdministration : IRepoScopeAdministration
{
    private readonly CharterDbContext database;
    private readonly IAuditWriter audit;

    public RepoScopeAdministration(CharterDbContext database, IAuditWriter audit)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(audit);

        this.database = database;
        this.audit = audit;
    }

    /// <inheritdoc />
    public Task<AuthorizationDecision> SetMemberScopeAsync(
        MemberSnapshot actor,
        Guid repoId,
        Guid targetMemberId,
        bool canRequest,
        CancellationToken cancellationToken = default)
        => WriteAsync(actor, repoId, targetMemberId, role: null, canRequest, cancellationToken);

    /// <inheritdoc />
    public Task<AuthorizationDecision> SetRoleScopeAsync(
        MemberSnapshot actor,
        Guid repoId,
        MemberRole role,
        bool canRequest,
        CancellationToken cancellationToken = default)
        => WriteAsync(actor, repoId, memberId: null, role, canRequest, cancellationToken);

    /// <inheritdoc />
    public Task<AuthorizationDecision> GrantOnConnectAsync(
        MemberSnapshot connector,
        Guid repoId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connector);
        return WriteAsync(connector, repoId, connector.MemberId, role: null, canRequest: true, cancellationToken);
    }

    private async Task<AuthorizationDecision> WriteAsync(
        MemberSnapshot actor,
        Guid repoId,
        Guid? memberId,
        MemberRole? role,
        bool canRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var repo = await database.Repos
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == repoId, cancellationToken);

        if (repo is null)
        {
            return AuthorizationDecision.Deny("no repository by that id is open to you");
        }

        var snapshot = RepoSnapshot.From(repo, []);
        var permitted = RepoAccessPolicy.CanAdministerRepositoryScope(actor, snapshot);
        if (permitted.IsDenied)
        {
            return permitted;
        }

        var existing = await database.RepoScopes.SingleOrDefaultAsync(
            row => row.RepoId == repoId && row.MemberId == memberId && row.Role == role,
            cancellationToken);

        if (existing is null)
        {
            database.RepoScopes.Add(memberId is { } target
                ? RepoScope.ForMember(repoId, target, canRequest)
                : RepoScope.ForRole(repoId, role!.Value, canRequest));
        }
        else if (canRequest)
        {
            existing.Allow();
        }
        else
        {
            existing.Deny();
        }

        await database.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(
            new AuditEntry
            {
                OrgId = actor.OrgId,
                ActorUserId = actor.UserId,
                Action = canRequest ? AuditActions.RepoScopeGranted : AuditActions.RepoScopeRevoked,
                TargetType = "repo",
                TargetId = repoId.ToString(),
                Metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["member_id"] = memberId?.ToString(),
                    ["role"] = role?.ToString(),
                },
            },
            cancellationToken);

        return canRequest
            ? AuthorizationDecision.Allow("the scope grant was written", AuditActions.RepoScopeGranted)
            : AuthorizationDecision.Allow("the scope grant was withdrawn", AuditActions.RepoScopeRevoked);
    }
}
