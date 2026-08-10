using Charter.Domain;

namespace Charter.Auth.Authorization;

/// <summary>One <c>auto_dispatch_policies</c> row, as the resolver sees it (section 7.5).</summary>
public sealed record AutoDispatchPolicySnapshot
{
    public required Guid PolicyId { get; init; }

    public required Guid OrgId { get; init; }

    /// <summary>Null for an organisation-wide policy.</summary>
    public Guid? RepoId { get; init; }

    /// <summary>Null when the policy is not addressed at a role.</summary>
    public MemberRole? Role { get; init; }

    /// <summary>Null when the policy is not a per-user override.</summary>
    public Guid? UserId { get; init; }

    public required bool Enabled { get; init; }

    public decimal? MaxCostUsd { get; init; }

    public int? MaxConcurrentSessions { get; init; }

    public IReadOnlyList<string> AllowedPaths { get; init; } = [];

    public IReadOnlyList<ProjectType> ProjectTypes { get; init; } = [];

    public decimal? RequireApprovalAboveUsd { get; init; }

    /// <summary>User override beats role, which beats a repository default, which beats the org default.</summary>
    public int Specificity
        => (UserId is not null ? 8 : 0) + (Role is not null ? 4 : 0) + (RepoId is not null ? 2 : 0);

    public static AutoDispatchPolicySnapshot From(AutoDispatchPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return new AutoDispatchPolicySnapshot
        {
            PolicyId = policy.Id,
            OrgId = policy.OrgId,
            RepoId = policy.RepoId,
            Role = policy.Role,
            UserId = policy.UserId,
            Enabled = policy.Enabled,
            MaxCostUsd = policy.MaxCostUsd,
            MaxConcurrentSessions = policy.MaxConcurrentSessions,
            AllowedPaths = policy.AllowedPaths,
            ProjectTypes = policy.ProjectTypes,
            RequireApprovalAboveUsd = policy.RequireApprovalAboveUsd,
        };
    }
}

/// <summary>
/// What a repository says about auto-dispatch in its own <c>.charter/config.yml</c>.
/// </summary>
/// <remarks>
/// <para>
/// Section 7.5: <b>a repository may only tighten, never loosen.</b> That is enforced by the shape of
/// this type, not by discipline in the resolver. There is no <c>Enabled</c> here, only
/// <see cref="DisallowAutoDispatch"/>; there is no minimum, only ceilings; there are no extra paths,
/// only an allow-list to intersect with. A repository literally cannot express "let more through",
/// so no admin setting can be overridden in the permissive direction and no future edit to the
/// resolver can accidentally allow it.
/// </para>
/// <para>
/// The strictest guardrail therefore always lives in the reviewable file, which is the section 8
/// principle this rule exists to keep intact.
/// </para>
/// </remarks>
public sealed record RepoAutoDispatchRestriction
{
    /// <summary>A sensitive repository requiring approval regardless of organisation policy.</summary>
    public bool DisallowAutoDispatch { get; init; }

    /// <summary>An upper bound applied as a minimum against the policy's own ceiling.</summary>
    public decimal? MaxCostUsdCeiling { get; init; }

    public int? MaxConcurrentSessionsCeiling { get; init; }

    /// <summary>Intersected with the policy's paths. Null leaves the policy's paths alone.</summary>
    public IReadOnlyList<string>? PathAllowList { get; init; }

    /// <summary>Intersected with the policy's project types. Null leaves them alone.</summary>
    public IReadOnlyList<ProjectType>? ProjectTypeAllowList { get; init; }

    /// <summary>Lowers the threshold above which a human decision is required.</summary>
    public decimal? RequireApprovalAboveUsdCeiling { get; init; }

    /// <summary>A repository that says nothing, which changes nothing.</summary>
    public static RepoAutoDispatchRestriction None { get; } = new();

    /// <summary>Projects a peer policy into a restriction, so ties fold with the same arithmetic.</summary>
    internal static RepoAutoDispatchRestriction FromPeerPolicy(AutoDispatchPolicySnapshot policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return new RepoAutoDispatchRestriction
        {
            DisallowAutoDispatch = !policy.Enabled,
            MaxCostUsdCeiling = policy.MaxCostUsd,
            MaxConcurrentSessionsCeiling = policy.MaxConcurrentSessions,
            PathAllowList = policy.AllowedPaths.Count == 0 ? null : policy.AllowedPaths,
            ProjectTypeAllowList = policy.ProjectTypes.Count == 0 ? null : policy.ProjectTypes,
            RequireApprovalAboveUsdCeiling = policy.RequireApprovalAboveUsd,
        };
    }
}

/// <summary>The resolved, composed answer for one member against one repository (section 7.5).</summary>
public sealed record AutoDispatchDecision
{
    /// <summary>False means <c>SpecReady</c> waits for a human. It never means "fail open".</summary>
    public required bool Enabled { get; init; }

    /// <summary>Plain language, safe to show the requester as "waiting on someone to approve".</summary>
    public required string Reason { get; init; }

    /// <summary>The policy that won most-specific-wins, or null when nothing applied.</summary>
    public Guid? PolicyId { get; init; }

    public int Specificity { get; init; }

    /// <summary>True when the repository's own file tightened the winning policy.</summary>
    public bool TightenedByRepository { get; init; }

    public decimal? MaxCostUsd { get; init; }

    public int? MaxConcurrentSessions { get; init; }

    /// <summary>Empty means the policy adds no path restriction of its own.</summary>
    public IReadOnlyList<string> AllowedPaths { get; init; } = [];

    /// <summary>Empty means the policy adds no project-type restriction of its own.</summary>
    public IReadOnlyList<ProjectType> ProjectTypes { get; init; } = [];

    public decimal? RequireApprovalAboveUsd { get; init; }

    /// <summary>The refusal every "nothing applies" path returns. Deny by default.</summary>
    public static AutoDispatchDecision Blocked(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new AutoDispatchDecision { Enabled = false, Reason = reason };
    }

    /// <summary>True when a session estimated at <paramref name="estimatedUsd"/> may start without a human.</summary>
    public bool PermitsCost(decimal estimatedUsd)
        => Enabled
           && (MaxCostUsd is null || estimatedUsd <= MaxCostUsd.Value)
           && (RequireApprovalAboveUsd is null || estimatedUsd <= RequireApprovalAboveUsd.Value);

    /// <summary>True when <paramref name="path"/> is inside the effective path allow-list.</summary>
    public bool PermitsPath(string path) => Enabled && PathScope.Permits(AllowedPaths, path);

    /// <summary>True when <paramref name="projectType"/> is inside the effective project-type list.</summary>
    public bool PermitsProjectType(ProjectType projectType)
        => Enabled && (ProjectTypes.Count == 0 || ProjectTypes.Contains(projectType));

    /// <summary>
    /// True when this decision permits nothing that <paramref name="other"/> forbids.
    /// </summary>
    /// <remarks>
    /// The property the section 7.5 composition rule is: applying a repository restriction must
    /// always produce a decision that is at least as restrictive as the one without it. This is what
    /// the test asserts, rather than re-listing the individual field rules.
    /// </remarks>
    public bool IsAtLeastAsRestrictiveAs(AutoDispatchDecision other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (!Enabled)
        {
            return true;
        }

        if (!other.Enabled)
        {
            return false;
        }

        return AtMost(MaxCostUsd, other.MaxCostUsd)
            && AtMost(MaxConcurrentSessions, other.MaxConcurrentSessions)
            && AtMost(RequireApprovalAboveUsd, other.RequireApprovalAboveUsd)
            && PathsNoWiderThan(other)
            && ProjectTypesNoWiderThan(other);
    }

    private bool PathsNoWiderThan(AutoDispatchDecision other)
    {
        if (other.AllowedPaths.Count == 0)
        {
            return true;
        }

        return AllowedPaths.Count != 0
            && AllowedPaths.All(mine => other.AllowedPaths.Any(theirs => PathScope.Covers(theirs, mine)));
    }

    private bool ProjectTypesNoWiderThan(AutoDispatchDecision other)
    {
        if (other.ProjectTypes.Count == 0)
        {
            return true;
        }

        return ProjectTypes.Count != 0 && ProjectTypes.All(other.ProjectTypes.Contains);
    }

    private static bool AtMost(decimal? mine, decimal? theirs)
        => theirs is null || (mine is not null && mine.Value <= theirs.Value);

    private static bool AtMost(int? mine, int? theirs)
        => theirs is null || (mine is not null && mine.Value <= theirs.Value);
}

/// <summary>
/// Most-specific-wins resolution of <c>SpecReady → Queued</c>, then the repository's tightening
/// (section 7.5).
/// </summary>
public static class AutoDispatchPolicyResolver
{
    /// <summary>
    /// Resolves the effective policy: user override → role → repository default → organisation
    /// default, then composed with whatever the repository's own file restricts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing applying is a refusal, not a permission: a specification with no policy behind it
    /// queues for a human. Personal mode reaches "everything auto-dispatches" by having an enabled
    /// organisation-default row seeded at setup — the same row an organisation would write, resolved
    /// by the same code — and not by a branch (section 7.2).
    /// </para>
    /// <para>
    /// Section 7.5 also lists what auto-dispatch never bypasses. Repository readiness is one of them
    /// and is checked here; path scope, migration classification, budget caps and rate limits are
    /// enforced elsewhere and are deliberately not re-implemented in this resolver.
    /// </para>
    /// </remarks>
    public static AutoDispatchDecision Resolve(
        MemberSnapshot member,
        RepoSnapshot repo,
        IEnumerable<AutoDispatchPolicySnapshot> policies,
        RepoAutoDispatchRestriction? repoRestriction = null)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(policies);

        var restriction = repoRestriction ?? RepoAutoDispatchRestriction.None;

        if (member.OrgId != repo.OrgId)
        {
            return AutoDispatchDecision.Blocked("that repository belongs to a different organisation");
        }

        // Section 7.5: a repo that has not passed its smoke test is never auto-dispatchable.
        if (repo.Status != RepoStatus.Ready)
        {
            return AutoDispatchDecision.Blocked("the repository has not passed its smoke test");
        }

        var applicable = policies
            .Where(policy => Applies(policy, member, repo))
            .ToList();

        if (applicable.Count == 0)
        {
            return AutoDispatchDecision.Blocked(
                "no auto-dispatch policy applies, so the specification waits for an approver");
        }

        var specificity = applicable.Max(policy => policy.Specificity);
        var winners = applicable.Where(policy => policy.Specificity == specificity).ToList();
        var winner = winners.OrderBy(policy => policy.PolicyId).First();

        var decision = Start(winner);

        // Two policies at the same specificity - a member holding two roles that both carry a policy
        // - fold with the same tightening arithmetic. A tie must never resolve to the looser side.
        foreach (var peer in winners.Where(policy => policy.PolicyId != winner.PolicyId))
        {
            decision = Tighten(decision, RepoAutoDispatchRestriction.FromPeerPolicy(peer), byRepository: false);
        }

        return Tighten(decision, restriction, byRepository: true);
    }

    /// <summary>
    /// Applies a restriction to a decision. Every combinator moves in one direction only: AND for
    /// the switch, minimum for the ceilings, intersection for the lists.
    /// </summary>
    public static AutoDispatchDecision Tighten(
        AutoDispatchDecision decision,
        RepoAutoDispatchRestriction restriction)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(restriction);

        return Tighten(decision, restriction, byRepository: true);
    }

    private static AutoDispatchDecision Tighten(
        AutoDispatchDecision decision,
        RepoAutoDispatchRestriction restriction,
        bool byRepository)
    {
        if (restriction.DisallowAutoDispatch)
        {
            return AutoDispatchDecision.Blocked(byRepository
                ? "the repository's own configuration requires approval regardless of organisation policy"
                : "one of the policies that applies to this member is disabled")
                with
            {
                PolicyId = decision.PolicyId,
                Specificity = decision.Specificity,
                TightenedByRepository = byRepository,
            };
        }

        var paths = restriction.PathAllowList is null
            ? decision.AllowedPaths
            : PathScope.Intersect(decision.AllowedPaths, restriction.PathAllowList);

        if (paths.Count == 0 && decision.AllowedPaths.Count != 0 && restriction.PathAllowList is { Count: > 0 })
        {
            return AutoDispatchDecision.Blocked(
                "the repository's path scope and the organisation policy's paths do not overlap")
                with
            {
                PolicyId = decision.PolicyId,
                Specificity = decision.Specificity,
                TightenedByRepository = byRepository,
            };
        }

        var projectTypes = restriction.ProjectTypeAllowList is null
            ? decision.ProjectTypes
            : IntersectProjectTypes(decision.ProjectTypes, restriction.ProjectTypeAllowList);

        if (projectTypes.Count == 0
            && decision.ProjectTypes.Count != 0
            && restriction.ProjectTypeAllowList is { Count: > 0 })
        {
            return AutoDispatchDecision.Blocked(
                "the repository's project types and the organisation policy's project types do not overlap")
                with
            {
                PolicyId = decision.PolicyId,
                Specificity = decision.Specificity,
                TightenedByRepository = byRepository,
            };
        }

        var tightened = decision with
        {
            MaxCostUsd = Lower(decision.MaxCostUsd, restriction.MaxCostUsdCeiling),
            MaxConcurrentSessions = Lower(decision.MaxConcurrentSessions, restriction.MaxConcurrentSessionsCeiling),
            RequireApprovalAboveUsd = Lower(
                decision.RequireApprovalAboveUsd,
                restriction.RequireApprovalAboveUsdCeiling),
            AllowedPaths = paths,
            ProjectTypes = projectTypes,
        };

        return byRepository && !Equals(tightened, decision)
            ? tightened with { TightenedByRepository = true }
            : tightened;
    }

    private static AutoDispatchDecision Start(AutoDispatchPolicySnapshot policy)
        => policy.Enabled
            ? new AutoDispatchDecision
            {
                Enabled = true,
                Reason = "an auto-dispatch policy covers this member and repository",
                PolicyId = policy.PolicyId,
                Specificity = policy.Specificity,
                MaxCostUsd = policy.MaxCostUsd,
                MaxConcurrentSessions = policy.MaxConcurrentSessions,
                AllowedPaths = policy.AllowedPaths,
                ProjectTypes = policy.ProjectTypes,
                RequireApprovalAboveUsd = policy.RequireApprovalAboveUsd,
            }
            : AutoDispatchDecision.Blocked("the policy that applies to this member has auto-dispatch turned off")
                with
            {
                PolicyId = policy.PolicyId,
                Specificity = policy.Specificity,
            };

    private static bool Applies(AutoDispatchPolicySnapshot policy, MemberSnapshot member, RepoSnapshot repo)
        => policy.OrgId == member.OrgId
           && (policy.RepoId is null || policy.RepoId == repo.RepoId)
           && (policy.UserId is null || policy.UserId == member.UserId)
           && (policy.Role is null || member.HasRole(policy.Role.Value));

    private static IReadOnlyList<ProjectType> IntersectProjectTypes(
        IReadOnlyList<ProjectType> left,
        IReadOnlyList<ProjectType> right)
    {
        if (left.Count == 0)
        {
            return right;
        }

        if (right.Count == 0)
        {
            return left;
        }

        return [.. left.Where(right.Contains).Distinct()];
    }

    private static decimal? Lower(decimal? left, decimal? right)
        => left is null ? right : right is null ? left : Math.Min(left.Value, right.Value);

    private static int? Lower(int? left, int? right)
        => left is null ? right : right is null ? left : Math.Min(left.Value, right.Value);
}
