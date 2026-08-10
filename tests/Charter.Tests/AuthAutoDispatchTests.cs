using Charter.Auth.Authorization;
using Charter.Domain;

namespace Charter.Tests;

/// <summary>
/// Section 7.5: most-specific-wins resolution, and the composition rule that a repository may only
/// tighten.
/// </summary>
public class AuthAutoDispatchTests
{
    private static readonly MemberSnapshot Requester = AuthTestData.Member(MemberRole.Requester);

    private static AutoDispatchDecision Resolve(
        IEnumerable<AutoDispatchPolicySnapshot> policies,
        RepoAutoDispatchRestriction? restriction = null,
        MemberSnapshot? member = null,
        RepoStatus status = RepoStatus.Ready)
        => AutoDispatchPolicyResolver.Resolve(
            member ?? Requester,
            AuthTestData.Repo(status),
            policies,
            restriction);

    [Fact]
    public void NoPolicyMeansNoAutoDispatch()
    {
        // Deny by default here too: the specification queues for an approver rather than dispatching.
        Assert.False(Resolve([]).Enabled);
    }

    [Fact]
    public void ARepositoryThatHasNotPassedItsSmokeTestIsNeverAutoDispatchable()
    {
        // Section 7.5 lists this among the things auto-dispatch never bypasses.
        Assert.False(Resolve([AuthTestData.Policy(enabled: true)], status: RepoStatus.SmokeTest).Enabled);
    }

    [Fact]
    public void AUserOverrideBeatsARoleWhichBeatsARepositoryDefaultWhichBeatsTheOrganisationDefault()
    {
        var policies = new[]
        {
            AuthTestData.Policy(maxCostUsd: 1m),
            AuthTestData.Policy(repoId: AuthTestData.RepoId, maxCostUsd: 2m),
            AuthTestData.Policy(role: MemberRole.Requester, maxCostUsd: 3m),
            AuthTestData.Policy(userId: Requester.UserId, maxCostUsd: 5m),
        };

        Assert.Equal(5m, Resolve(policies).MaxCostUsd);
        Assert.Equal(3m, Resolve(policies[..3]).MaxCostUsd);
        Assert.Equal(2m, Resolve(policies[..2]).MaxCostUsd);
        Assert.Equal(1m, Resolve(policies[..1]).MaxCostUsd);
    }

    [Fact]
    public void APolicyAddressedAtSomebodyElseDoesNotApply()
    {
        var policies = new[]
        {
            AuthTestData.Policy(maxCostUsd: 1m),
            AuthTestData.Policy(userId: Guid.CreateVersion7(), maxCostUsd: 500m),
            AuthTestData.Policy(role: MemberRole.Admin, maxCostUsd: 500m),
            AuthTestData.Policy(repoId: Guid.CreateVersion7(), maxCostUsd: 500m),
        };

        Assert.Equal(1m, Resolve(policies).MaxCostUsd);
    }

    [Fact]
    public void AMostSpecificPolicyThatIsTurnedOffTurnsAutoDispatchOff()
    {
        // "New hire - approval required for their first month" sits on top of a permissive default.
        var policies = new[]
        {
            AuthTestData.Policy(enabled: true, maxCostUsd: 20m),
            AuthTestData.Policy(enabled: false, userId: Requester.UserId),
        };

        Assert.False(Resolve(policies).Enabled);
    }

    [Fact]
    public void TwoPoliciesAtTheSameSpecificityResolveToTheStricterOne()
    {
        // A member holding two roles that each carry a policy. A tie must never resolve to the looser
        // side, whichever order the rows came back in.
        var member = AuthTestData.Member(MemberRole.Requester, MemberRole.Engineer);

        var policies = new[]
        {
            AuthTestData.Policy(role: MemberRole.Requester, maxCostUsd: 2m),
            AuthTestData.Policy(role: MemberRole.Engineer, maxCostUsd: 50m),
        };

        Assert.Equal(2m, Resolve(policies, member: member).MaxCostUsd);
        Assert.Equal(2m, Resolve(policies.Reverse(), member: member).MaxCostUsd);
    }

    [Fact]
    public void ARepositoryCanTightenACostCeiling()
    {
        var policies = new[] { AuthTestData.Policy(maxCostUsd: 10m) };
        var tightened = Resolve(policies, new RepoAutoDispatchRestriction { MaxCostUsdCeiling = 2m });

        Assert.Equal(2m, tightened.MaxCostUsd);
        Assert.True(tightened.TightenedByRepository);
        Assert.True(tightened.PermitsCost(2m));
        Assert.False(tightened.PermitsCost(3m));
    }

    [Fact]
    public void ARepositoryCannotLoosenACostCeiling()
    {
        // The whole point of the composition rule. No admin setting, and no repository setting,
        // raises a limit somebody else set lower.
        var policies = new[] { AuthTestData.Policy(maxCostUsd: 2m) };

        Assert.Equal(2m, Resolve(policies, new RepoAutoDispatchRestriction { MaxCostUsdCeiling = 50m }).MaxCostUsd);
    }

    [Fact]
    public void ARepositoryCanForbidAutoDispatchOutrightAndNothingOverridesThat()
    {
        // "A sensitive repository can require approval regardless of org policy."
        var policies = new[]
        {
            AuthTestData.Policy(enabled: true, maxCostUsd: 500m),
            AuthTestData.Policy(enabled: true, userId: Requester.UserId, maxCostUsd: 500m),
        };

        var decision = Resolve(policies, new RepoAutoDispatchRestriction { DisallowAutoDispatch = true });

        Assert.False(decision.Enabled);
        Assert.True(decision.TightenedByRepository);
        Assert.False(decision.PermitsCost(0.01m));
    }

    [Fact]
    public void TheRestrictionTypeCannotExpressLoosening()
    {
        // Structural, not behavioural: RepoAutoDispatchRestriction has no "enabled" and no floors,
        // only a disallow flag and ceilings. If somebody adds one, this fails.
        var settable = typeof(RepoAutoDispatchRestriction)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(property => property.Name)
            .Order()
            .ToArray();

        Assert.Equal(
            [
                "DisallowAutoDispatch",
                "MaxConcurrentSessionsCeiling",
                "MaxCostUsdCeiling",
                "PathAllowList",
                "ProjectTypeAllowList",
                "RequireApprovalAboveUsdCeiling",
            ],
            settable);
    }

    public static TheoryData<RepoAutoDispatchRestriction> Restrictions
    {
        get
        {
            var data = new TheoryData<RepoAutoDispatchRestriction>();

            foreach (var restriction in new RepoAutoDispatchRestriction[]
                     {
                         RepoAutoDispatchRestriction.None,
                         new() { DisallowAutoDispatch = true },
                         new() { MaxCostUsdCeiling = 0.5m },
                         new() { MaxCostUsdCeiling = 1000m },
                         new() { MaxConcurrentSessionsCeiling = 1 },
                         new() { MaxConcurrentSessionsCeiling = 1000 },
                         new() { RequireApprovalAboveUsdCeiling = 0.1m },
                         new() { RequireApprovalAboveUsdCeiling = 1000m },
                         new() { PathAllowList = ["src/Features/**"] },
                         new() { PathAllowList = ["**"] },
                         new() { ProjectTypeAllowList = [ProjectType.Web] },
                         new() { ProjectTypeAllowList = [ProjectType.Web, ProjectType.Api, ProjectType.Library] },
                     })
            {
                data.Add(restriction);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Restrictions))]
    public void ApplyingARepositoryRestrictionIsAlwaysAtLeastAsRestrictive(RepoAutoDispatchRestriction restriction)
    {
        // The composition rule as a property rather than a list of cases: whatever the repository
        // says, the composed decision never permits something the uncomposed one refused.
        var policies = new[]
        {
            AuthTestData.Policy(
                enabled: true,
                maxCostUsd: 5m,
                maxConcurrentSessions: 3,
                allowedPaths: ["src/**"],
                projectTypes: [ProjectType.Web, ProjectType.Api],
                requireApprovalAboveUsd: 4m),
        };

        var loose = Resolve(policies);
        var composed = Resolve(policies, restriction);

        Assert.True(
            composed.IsAtLeastAsRestrictiveAs(loose),
            $"A repository restriction loosened the policy: {composed}");
    }

    [Fact]
    public void PathsNarrowToWhicheverSideIsTighter()
    {
        var wideOrg = new[] { AuthTestData.Policy(allowedPaths: ["src/**"]) };
        var narrowOrg = new[] { AuthTestData.Policy(allowedPaths: ["src/Features/**"]) };

        Assert.Equal(
            ["src/Features/**"],
            Resolve(wideOrg, new RepoAutoDispatchRestriction { PathAllowList = ["src/Features/**"] }).AllowedPaths);

        Assert.Equal(
            ["src/Features/**"],
            Resolve(narrowOrg, new RepoAutoDispatchRestriction { PathAllowList = ["src/**"] }).AllowedPaths);
    }

    [Fact]
    public void PathsThatDoNotOverlapAtAllBlockRatherThanFallBackToEverything()
    {
        // The dangerous reading: an empty intersection means "no restriction". It means the opposite.
        var policies = new[] { AuthTestData.Policy(allowedPaths: ["src/Billing/**"]) };
        var decision = Resolve(policies, new RepoAutoDispatchRestriction { PathAllowList = ["src/Features/**"] });

        Assert.False(decision.Enabled);
        Assert.False(decision.PermitsPath("src/Features/Thing.cs"));
    }

    [Fact]
    public void AnUnrestrictedPolicyPicksUpTheRepositorysOwnPaths()
    {
        // Section 7.5: allowed_paths is a subset of the repo scope, never a superset.
        var decision = Resolve(
            [AuthTestData.Policy()],
            new RepoAutoDispatchRestriction { PathAllowList = ["src/Features/**"] });

        Assert.Equal(["src/Features/**"], decision.AllowedPaths);
        Assert.True(decision.PermitsPath("src/Features/Billing/Invoice.cs"));
        Assert.False(decision.PermitsPath("src/Auth/Tokens.cs"));
    }

    [Fact]
    public void ProjectTypesIntersectAndAnEmptyIntersectionBlocks()
    {
        var policies = new[] { AuthTestData.Policy(projectTypes: [ProjectType.Web, ProjectType.Api]) };

        Assert.Equal(
            [ProjectType.Web],
            Resolve(policies, new RepoAutoDispatchRestriction { ProjectTypeAllowList = [ProjectType.Web] })
                .ProjectTypes);

        Assert.False(
            Resolve(policies, new RepoAutoDispatchRestriction { ProjectTypeAllowList = [ProjectType.Unity] })
                .Enabled);
    }

    [Fact]
    public void ApprovalThresholdsAlsoOnlyMoveDown()
    {
        var policies = new[] { AuthTestData.Policy(maxCostUsd: 100m, requireApprovalAboveUsd: 10m) };

        var decision = Resolve(policies, new RepoAutoDispatchRestriction { RequireApprovalAboveUsdCeiling = 2m });

        Assert.Equal(2m, decision.RequireApprovalAboveUsd);
        Assert.True(decision.PermitsCost(2m));
        Assert.False(decision.PermitsCost(5m));
    }

    [Fact]
    public void PersonalModeAutoDispatchesEverythingThroughTheOrdinaryResolver()
    {
        // Section 7.5's first example. No branch, no special case - one enabled organisation-default
        // row, resolved by the same code an organisation uses.
        var solo = AuthTestData.Member(Member.AllRoles.ToArray());

        var decision = Resolve([AuthTestData.Policy(enabled: true)], member: solo);

        Assert.True(decision.Enabled);
        Assert.True(decision.PermitsCost(999m));
        Assert.True(decision.PermitsPath("anything/at/all.cs"));
    }
}

/// <summary>The repository's own <c>.charter/config.yml</c>, read as a tightening (sections 8, 7.5).</summary>
public class AuthRepoCharterConfigTests
{
    [Fact]
    public void NoSnapshotRestrictsNothing()
        => Assert.Equal(
            RepoAutoDispatchRestriction.None,
            RepoCharterConfig.ReadAutoDispatchRestriction((string?)null));

    [Fact]
    public void TheScopeAllowListBecomesThePathCeiling()
    {
        var restriction = RepoCharterConfig.ReadAutoDispatchRestriction(
            """{"scopes":{"allow":["src/Features/**","src/Web/Components/**"],"deny":["src/Auth/**"]}}""");

        Assert.Equal(["src/Features/**", "src/Web/Components/**"], restriction.PathAllowList);
    }

    [Fact]
    public void TheSessionLimitBecomesTheCostCeiling()
    {
        var restriction = RepoCharterConfig.ReadAutoDispatchRestriction("""{"limits":{"max_session_usd":5.0}}""");

        Assert.Equal(5.0m, restriction.MaxCostUsdCeiling);
    }

    [Fact]
    public void ARepositoryCanTurnAutoDispatchOffForItself()
    {
        var restriction = RepoCharterConfig.ReadAutoDispatchRestriction("""{"auto_dispatch":{"enabled":false}}""");

        Assert.True(restriction.DisallowAutoDispatch);
    }

    [Fact]
    public void ARepositoryCannotTurnAutoDispatchOn()
    {
        // `enabled: true` in the file is not a grant - it cannot be, because the restriction type has
        // no way to say it. The organisation policy still decides.
        var restriction = RepoCharterConfig.ReadAutoDispatchRestriction("""{"auto_dispatch":{"enabled":true}}""");

        Assert.False(restriction.DisallowAutoDispatch);

        var decision = AutoDispatchPolicyResolver.Resolve(
            AuthTestData.Member(MemberRole.Requester),
            AuthTestData.Repo(),
            [AuthTestData.Policy(enabled: false)],
            restriction);

        Assert.False(decision.Enabled);
    }

    [Fact]
    public void AnUnreadableSnapshotFailsClosed()
    {
        Assert.True(RepoCharterConfig.ReadAutoDispatchRestriction("not json at all").DisallowAutoDispatch);
        Assert.True(RepoCharterConfig.ReadAutoDispatchRestriction("[1,2,3]").DisallowAutoDispatch);
    }

    [Fact]
    public void UnknownKeysAreIgnoredRatherThanFatal()
    {
        // Section 8: unknown keys warn, never fail, so an old Charter reads a newer file.
        var restriction = RepoCharterConfig.ReadAutoDispatchRestriction(
            """{"version":1,"something_from_the_future":{"nested":true},"limits":{"max_session_usd":3}}""");

        Assert.False(restriction.DisallowAutoDispatch);
        Assert.Equal(3m, restriction.MaxCostUsdCeiling);
    }
}

/// <summary>The prefix-glob arithmetic the composition rule leans on.</summary>
public class AuthPathScopeTests
{
    [Theory]
    [InlineData("src/**", "src/Features/**", true)]
    [InlineData("src/**", "src/Features/Thing.cs", true)]
    [InlineData("src/Features/**", "src/**", false)]
    [InlineData("src/Features/**", "src/FeaturesOther/Thing.cs", false)]
    [InlineData("**", "anything", true)]
    [InlineData("src/Features/**", "src/Features/**", true)]
    public void CoverageIsPrefixShaped(string pattern, string candidate, bool covers)
        => Assert.Equal(covers, PathScope.Covers(pattern, candidate));

    [Fact]
    public void AnEmptyListMeansUnrestricted()
    {
        Assert.True(PathScope.Permits([], "anything/at/all"));
        Assert.Equal(["src/**"], PathScope.Intersect([], ["src/**"]));
        Assert.Equal(["src/**"], PathScope.Intersect(["src/**"], []));
    }
}
