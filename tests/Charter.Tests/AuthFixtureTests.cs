using Charter.Auth.Authorization;
using Charter.Domain;

namespace Charter.Tests;

/// <summary>
/// Builders for the authorisation tests.
/// </summary>
/// <remarks>
/// Everything the section 7 rules take is a snapshot, so a scenario is a few records rather than a
/// database. That is the point: the rules that decide who may see a diff are testable without a
/// Postgres, which is why there are a lot of these tests and why they run in milliseconds.
/// </remarks>
internal static class AuthTestData
{
    public static Guid OrgId { get; } = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    public static Guid OtherOrgId { get; } = Guid.Parse("00000000-0000-0000-0000-0000000000a2");

    public static Guid RepoId { get; } = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

    public static MemberSnapshot Member(
        params MemberRole[] roles)
        => new()
        {
            MemberId = Guid.CreateVersion7(),
            OrgId = OrgId,
            UserId = Guid.CreateVersion7(),
            Roles = roles,
        };

    public static MemberSnapshot MemberIn(Guid orgId, params MemberRole[] roles)
        => Member(roles) with { OrgId = orgId };

    /// <summary>
    /// A repository as it is the moment it is connected: ready to be requested against by nobody,
    /// because there are no scope rows (section 7.3).
    /// </summary>
    public static RepoSnapshot Repo(RepoStatus status = RepoStatus.Ready, params RepoScopeGrant[] grants)
        => new()
        {
            RepoId = RepoId,
            OrgId = OrgId,
            Status = status,
            Grants = grants,
        };

    public static RepoScopeGrant GrantTo(MemberSnapshot member, bool canRequest = true)
        => new(member.MemberId, null, canRequest);

    public static RepoScopeGrant GrantTo(MemberRole role, bool canRequest = true)
        => new(null, role, canRequest);

    public static SessionSnapshot Session(MemberSnapshot requester)
        => new()
        {
            SessionId = Guid.CreateVersion7(),
            OrgId = OrgId,
            RepoId = RepoId,
            RequesterUserId = requester.UserId,
        };

    public static SpecSnapshot Spec(MemberSnapshot requester, bool approved = false)
        => new()
        {
            SpecId = Guid.CreateVersion7(),
            OrgId = OrgId,
            RepoId = RepoId,
            RequesterUserId = requester.UserId,
            IsApproved = approved,
        };

    public static AutoDispatchPolicySnapshot Policy(
        bool enabled = true,
        Guid? repoId = null,
        MemberRole? role = null,
        Guid? userId = null,
        decimal? maxCostUsd = null,
        int? maxConcurrentSessions = null,
        IReadOnlyList<string>? allowedPaths = null,
        IReadOnlyList<ProjectType>? projectTypes = null,
        decimal? requireApprovalAboveUsd = null)
        => new()
        {
            PolicyId = Guid.CreateVersion7(),
            OrgId = OrgId,
            RepoId = repoId,
            Role = role,
            UserId = userId,
            Enabled = enabled,
            MaxCostUsd = maxCostUsd,
            MaxConcurrentSessions = maxConcurrentSessions,
            AllowedPaths = allowedPaths ?? [],
            ProjectTypes = projectTypes ?? [],
            RequireApprovalAboveUsd = requireApprovalAboveUsd,
        };
}

/// <summary>
/// The one property everything else in section 7 is built on: an unpopulated decision refuses.
/// </summary>
public class AuthFixtureTests
{
    [Fact]
    public void AnUninitialisedDecisionIsADenial()
    {
        // Section 7.3 is deny-by-default. If `default` were an allow, every future policy method that
        // returned early, threw, or forgot a branch would be a grant.
        AuthorizationDecision uninitialised = default;

        Assert.True(uninitialised.IsDenied);
        Assert.False(uninitialised.IsAllowed);
        Assert.Equal(AuthorizationDecision.NoGrantReason, uninitialised.Reason);
        Assert.Equal(uninitialised, AuthorizationDecision.Denied);
    }

    [Fact]
    public void ADenialCarriesNoAuditVerbEvenIfOneWasAsked()
    {
        Assert.Null(AuthorizationDecision.Deny("nope").AuditAction);
        Assert.False(AuthorizationDecision.Deny("nope").IsNotable);
        Assert.Equal("spec.approved", AuthorizationDecision.Allow("yes", "spec.approved").AuditAction);
    }

    [Fact]
    public void CompositionOnlyEverNarrows()
    {
        var allow = AuthorizationDecision.Allow("granted");
        var deny = AuthorizationDecision.Deny("refused");

        Assert.True(allow.And(allow).IsAllowed);
        Assert.True(allow.And(deny).IsDenied);
        Assert.True(deny.And(allow).IsDenied);
        Assert.Equal("refused", allow.And(deny).Reason);
    }
}
