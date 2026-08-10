using Charter.Data;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;

namespace Charter.Onboarding;

/// <summary>A repository a requester may actually file against.</summary>
/// <param name="RepoId">The repository.</param>
/// <param name="OrgId">Its organisation.</param>
/// <param name="ConfigSnapshotJson">Its stored <c>.charter/config.yml</c> snapshot, for presentation.</param>
/// <param name="PrimerMarkdown">Its published primer.</param>
public sealed record RequestableRepo(Guid RepoId, Guid OrgId, string? ConfigSnapshotJson, string? PrimerMarkdown);

/// <summary>
/// The query path for "which repositories may this member file against" (sections 7.3, 9).
/// </summary>
/// <remarks>
/// <para>
/// Section 9 says a repository is invisible to requesters until its smoke test passes, and the
/// enforcement point is here rather than in the UI. Two independent conditions, both expressed in SQL
/// so neither can be forgotten by a caller:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <c>status = ready</c>. Readiness is earned. A repository mid-onboarding does not appear in a
/// project list, cannot be named in a request, and is not enumerable — a requester cannot learn that
/// it exists.
/// </description>
/// </item>
/// <item>
/// <description>
/// A <c>repo_scopes</c> row grants this member, or one of their roles, <c>can_request</c>. Deny by
/// default: the absence of a row is a refusal, and a row with <c>can_request = false</c> beats a
/// permissive one at the same specificity.
/// </description>
/// </item>
/// </list>
/// <para>
/// This is the list query. The single-repository decision belongs to
/// <c>Charter.Auth.Authorization.RepoAccessPolicy</c>, which applies the same two rules to a snapshot;
/// the two agree because they are two expressions of one sentence, and the tests assert that a
/// repository excluded here is also refused there.
/// </para>
/// </remarks>
public sealed class RequestableRepoQuery
{
    private readonly CharterDbContext _database;

    public RequestableRepoQuery(CharterDbContext database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>
    /// The repositories this member may file against, as an <see cref="IQueryable{T}"/> so a caller
    /// can page or project without the filter being optional.
    /// </summary>
    public IQueryable<Repo> Requestable(Guid orgId, Guid memberId, IReadOnlyCollection<MemberRole> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        var roleList = roles.ToList();

        return _database.Repos
            .Where(repo => repo.OrgId == orgId)

            // Section 9. First, not last: a repository that has not earned readiness is not merely
            // ungranted, it is not there at all.
            .Where(repo => repo.Status == RepoStatus.Ready)

            // Section 7.3, deny by default. A withholding row anywhere beats every granting row,
            // which is the only safe reading of a conflict.
            .Where(repo => !_database.RepoScopes.Any(scope =>
                scope.RepoId == repo.Id
                && !scope.CanRequest
                && (scope.MemberId == memberId || (scope.Role != null && roleList.Contains(scope.Role.Value)))))
            .Where(repo => _database.RepoScopes.Any(scope =>
                scope.RepoId == repo.Id
                && scope.CanRequest
                && (scope.MemberId == memberId || (scope.Role != null && roleList.Contains(scope.Role.Value)))));
    }

    /// <summary>The requestable repositories, materialised.</summary>
    public async Task<IReadOnlyList<RequestableRepo>> ListAsync(
        Guid orgId,
        Guid memberId,
        IReadOnlyCollection<MemberRole> roles,
        CancellationToken cancellationToken = default)
        => await Requestable(orgId, memberId, roles)
            .OrderBy(repo => repo.FullName)
            .Select(repo => new RequestableRepo(repo.Id, repo.OrgId, repo.CharterConfigSnapshot, repo.PrimerMd))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Whether this member may file against this one repository.
    /// </summary>
    /// <remarks>
    /// Expressed as the list query narrowed to one id rather than as a second rule, so the two can
    /// never disagree about what "visible" means.
    /// </remarks>
    public Task<bool> CanFileAgainstAsync(
        Guid orgId,
        Guid memberId,
        IReadOnlyCollection<MemberRole> roles,
        Guid repoId,
        CancellationToken cancellationToken = default)
        => Requestable(orgId, memberId, roles).AnyAsync(repo => repo.Id == repoId, cancellationToken);
}
