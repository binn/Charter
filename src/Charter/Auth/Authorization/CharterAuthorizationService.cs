using Charter.Auth.Audit;
using Charter.Data;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;

namespace Charter.Auth.Authorization;

/// <summary>
/// The questions the API asks before it answers anything.
/// </summary>
/// <remarks>
/// <para>
/// This type loads rows and delegates every judgement to the pure policies in this namespace. It
/// contains no rules of its own, which is deliberate: the rules are then testable without a database
/// and cannot quietly acquire a dependency on request context, on a user preference (section 7.4) or
/// on an organisation's mode (section 7.2).
/// </para>
/// <para>
/// A member who is not a member returns <c>null</c> from <see cref="ResolveMemberAsync"/> and every
/// question below then has no subject to answer for, which is the same refusal an unmatched grant
/// produces.
/// </para>
/// </remarks>
public interface ICharterAuthorizationService
{
    /// <summary>Loads the acting member, or null when this user is not in that organisation.</summary>
    Task<MemberSnapshot?> ResolveMemberAsync(Guid orgId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Every organisation this user belongs to, for a sign-in that has to choose one.</summary>
    Task<IReadOnlyList<MemberSnapshot>> ResolveMembershipsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>May this member file a request against this repository? (section 7.3)</summary>
    Task<AuthorizationDecision> CanFileRequestAsync(
        MemberSnapshot member,
        Guid repoId,
        CancellationToken cancellationToken = default);

    /// <summary>May this member approve this specification? (section 7.5, spend gate only)</summary>
    Task<AuthorizationDecision> CanApproveSpecAsync(
        MemberSnapshot member,
        Guid specId,
        CancellationToken cancellationToken = default);

    /// <summary>What may this member be shown of this session? (section 7.4)</summary>
    Task<SessionVisibility> ResolveSessionVisibilityAsync(
        MemberSnapshot member,
        Guid sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>Pane 2 (section 7.4).</summary>
    Task<AuthorizationDecision> CanViewTranscriptAsync(
        MemberSnapshot member,
        Guid sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>Pane 3 (section 7.4).</summary>
    Task<AuthorizationDecision> CanViewCodeAsync(
        MemberSnapshot member,
        Guid sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>The effective auto-dispatch policy for this member and repository (section 7.5).</summary>
    Task<AutoDispatchDecision> ResolveAutoDispatchAsync(
        MemberSnapshot member,
        Guid repoId,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ICharterAuthorizationService" />
public sealed class CharterAuthorizationService : ICharterAuthorizationService
{
    private readonly CharterDbContext database;
    private readonly IAuditWriter audit;

    public CharterAuthorizationService(CharterDbContext database, IAuditWriter audit)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(audit);

        this.database = database;
        this.audit = audit;
    }

    /// <inheritdoc />
    public async Task<MemberSnapshot?> ResolveMemberAsync(
        Guid orgId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var member = await database.Members
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.OrgId == orgId && row.UserId == userId, cancellationToken);

        return member is null ? null : MemberSnapshot.From(member);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemberSnapshot>> ResolveMembershipsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var members = await database.Members
            .AsNoTracking()
            .Where(row => row.UserId == userId)
            .OrderBy(row => row.CreatedAt)
            .ToListAsync(cancellationToken);

        return [.. members.Select(MemberSnapshot.From)];
    }

    /// <inheritdoc />
    public async Task<AuthorizationDecision> CanFileRequestAsync(
        MemberSnapshot member,
        Guid repoId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        var repo = await LoadRepoAsync(repoId, cancellationToken);

        // Section 7.3: an unknown repository is refused with the same words as an unscoped one, so
        // the API never turns a permission answer into a repository-existence oracle.
        return repo is null
            ? AuthorizationDecision.Deny("no repository by that id is open to you")
            : RepoAccessPolicy.CanFileRequest(member, repo);
    }

    /// <inheritdoc />
    public async Task<AuthorizationDecision> CanApproveSpecAsync(
        MemberSnapshot member,
        Guid specId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        var spec = await LoadSpecAsync(specId, cancellationToken);
        if (spec is null)
        {
            return AuthorizationDecision.Deny("no specification by that id is open to you");
        }

        var repo = await LoadRepoAsync(spec.RepoId, cancellationToken);
        if (repo is null)
        {
            return AuthorizationDecision.Deny("no specification by that id is open to you");
        }

        return SpecApprovalPolicy.CanApprove(member, repo, spec);
    }

    /// <inheritdoc />
    public async Task<SessionVisibility> ResolveSessionVisibilityAsync(
        MemberSnapshot member,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        var session = await LoadSessionAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return SessionVisibility.None;
        }

        var repo = await LoadRepoAsync(session.RepoId, cancellationToken);

        return repo is null ? SessionVisibility.None : SessionVisibilityPolicy.For(member, repo, session);
    }

    /// <inheritdoc />
    public async Task<AuthorizationDecision> CanViewTranscriptAsync(
        MemberSnapshot member,
        Guid sessionId,
        CancellationToken cancellationToken = default)
        => (await ResolveSessionVisibilityAsync(member, sessionId, cancellationToken)).Transcript
            ? AuthorizationDecision.Allow("the member has read access to the repository")
            : AuthorizationDecision.Deny("viewing a transcript needs read access to the repository");

    /// <inheritdoc />
    public async Task<AuthorizationDecision> CanViewCodeAsync(
        MemberSnapshot member,
        Guid sessionId,
        CancellationToken cancellationToken = default)
        => (await ResolveSessionVisibilityAsync(member, sessionId, cancellationToken)).Code
            ? AuthorizationDecision.Allow("the member has read access to the repository")
            : AuthorizationDecision.Deny("viewing the code pane needs read access to the repository");

    /// <inheritdoc />
    public async Task<AutoDispatchDecision> ResolveAutoDispatchAsync(
        MemberSnapshot member,
        Guid repoId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        var repoRow = await database.Repos
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == repoId, cancellationToken);

        if (repoRow is null)
        {
            return AutoDispatchDecision.Blocked("no repository by that id is open to you");
        }

        var scopes = await database.RepoScopes
            .AsNoTracking()
            .Where(row => row.RepoId == repoId)
            .ToListAsync(cancellationToken);

        var policies = await database.AutoDispatchPolicies
            .AsNoTracking()
            .Where(row => row.OrgId == member.OrgId && (row.RepoId == null || row.RepoId == repoId))
            .ToListAsync(cancellationToken);

        return AutoDispatchPolicyResolver.Resolve(
            member,
            RepoSnapshot.From(repoRow, scopes),
            policies.Select(AutoDispatchPolicySnapshot.From),
            RepoCharterConfig.ReadAutoDispatchRestriction(repoRow));
    }

    /// <summary>
    /// Records a grant that labelled itself notable. Callers that act on a decision pass it here
    /// rather than deciding for themselves whether this one was worth writing down.
    /// </summary>
    public Task AuditAsync(
        MemberSnapshot member,
        AuthorizationDecision decision,
        string targetType,
        string? targetId = null,
        CancellationToken cancellationToken = default)
        => audit.RecordGrantAsync(member, decision, targetType, targetId, metadata: null, cancellationToken);

    private async Task<RepoSnapshot?> LoadRepoAsync(Guid repoId, CancellationToken cancellationToken)
    {
        var repo = await database.Repos
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == repoId, cancellationToken);

        if (repo is null)
        {
            return null;
        }

        var scopes = await database.RepoScopes
            .AsNoTracking()
            .Where(row => row.RepoId == repoId)
            .ToListAsync(cancellationToken);

        return RepoSnapshot.From(repo, scopes);
    }

    private Task<SpecSnapshot?> LoadSpecAsync(Guid specId, CancellationToken cancellationToken)
        => (from spec in database.Specs.AsNoTracking()
            join request in database.Requests.AsNoTracking() on spec.RequestId equals request.Id
            where spec.Id == specId
            select new SpecSnapshot
            {
                SpecId = spec.Id,
                OrgId = request.OrgId,
                RepoId = request.RepoId,
                RequesterUserId = request.RequesterId,
                IsApproved = spec.ApprovedAt != null,
            })
            .SingleOrDefaultAsync(cancellationToken);

    private Task<SessionSnapshot?> LoadSessionAsync(Guid sessionId, CancellationToken cancellationToken)
        => (from session in database.Sessions.AsNoTracking()
            join spec in database.Specs.AsNoTracking() on session.SpecId equals spec.Id
            join request in database.Requests.AsNoTracking() on spec.RequestId equals request.Id
            where session.Id == sessionId
            select new SessionSnapshot
            {
                SessionId = session.Id,
                OrgId = request.OrgId,
                RepoId = request.RepoId,
                RequesterUserId = request.RequesterId,
            })
            .SingleOrDefaultAsync(cancellationToken);
}
