using Charter.Auth;
using Charter.Auth.Authorization;
using Charter.Domain;
using Charter.VersionControl;

namespace Charter.Tests;

/// <summary>
/// Repository scope: deny by default, and additive roles (sections 7.1, 7.3).
/// </summary>
public class AuthRepoScopeTests
{
    [Theory]
    [InlineData(MemberRole.Requester)]
    [InlineData(MemberRole.Approver)]
    [InlineData(MemberRole.Engineer)]
    [InlineData(MemberRole.Admin)]
    public void ANewlyConnectedRepositoryIsRequestableByNobody(MemberRole role)
    {
        // Section 7.3, guardrail 1. Not "requestable by admins", not "requestable by whoever
        // connected it" - by nobody, until a row says otherwise.
        var member = AuthTestData.Member(role);
        var repo = AuthTestData.Repo();

        Assert.True(RepoAccessPolicy.CanFileRequest(member, repo).IsDenied);
    }

    [Fact]
    public void AMemberGrantMakesTheRepositoryRequestable()
    {
        var member = AuthTestData.Member(MemberRole.Requester);
        var repo = AuthTestData.Repo(RepoStatus.Ready, AuthTestData.GrantTo(member));

        Assert.True(RepoAccessPolicy.CanFileRequest(member, repo).IsAllowed);
    }

    [Fact]
    public void ARoleGrantCoversEveryMemberHoldingThatRoleAndNobodyElse()
    {
        var requester = AuthTestData.Member(MemberRole.Requester);
        var engineer = AuthTestData.Member(MemberRole.Engineer);
        var repo = AuthTestData.Repo(RepoStatus.Ready, AuthTestData.GrantTo(MemberRole.Requester));

        Assert.True(RepoAccessPolicy.CanFileRequest(requester, repo).IsAllowed);
        Assert.True(RepoAccessPolicy.CanFileRequest(engineer, repo).IsDenied);
    }

    [Fact]
    public void RolesAreAdditiveSoOneMatchingRoleIsEnough()
    {
        // Section 7.1: a member may hold several. The check asks "does this member hold the role",
        // never "is this member's role".
        var both = AuthTestData.Member(MemberRole.Engineer, MemberRole.Requester);
        var repo = AuthTestData.Repo(RepoStatus.Ready, AuthTestData.GrantTo(MemberRole.Requester));

        Assert.True(RepoAccessPolicy.CanFileRequest(both, repo).IsAllowed);
        Assert.True(RepoAccessPolicy.CanReadRepository(both, repo).IsAllowed);
    }

    [Fact]
    public void AMemberLevelWithholdingBeatsARoleLevelGrant()
    {
        var member = AuthTestData.Member(MemberRole.Requester);
        var repo = AuthTestData.Repo(
            RepoStatus.Ready,
            AuthTestData.GrantTo(MemberRole.Requester),
            AuthTestData.GrantTo(member, canRequest: false));

        Assert.True(RepoAccessPolicy.CanFileRequest(member, repo).IsDenied);
    }

    [Fact]
    public void TwoRoleGrantsThatDisagreeResolveToTheStricterReading()
    {
        var member = AuthTestData.Member(MemberRole.Requester, MemberRole.Approver);
        var repo = AuthTestData.Repo(
            RepoStatus.Ready,
            AuthTestData.GrantTo(MemberRole.Requester),
            AuthTestData.GrantTo(MemberRole.Approver, canRequest: false));

        Assert.True(RepoAccessPolicy.CanFileRequest(member, repo).IsDenied);
    }

    [Theory]
    [InlineData(RepoStatus.Pending)]
    [InlineData(RepoStatus.Recon)]
    [InlineData(RepoStatus.Configuring)]
    [InlineData(RepoStatus.SmokeTest)]
    [InlineData(RepoStatus.Disabled)]
    public void AGrantDoesNotSurviveARepositoryThatHasNotEarnedReady(RepoStatus status)
    {
        // Section 9: readiness is earned, and it gates requests ahead of any scope row.
        var member = AuthTestData.Member(MemberRole.Requester);
        var repo = AuthTestData.Repo(status, AuthTestData.GrantTo(member));

        Assert.True(RepoAccessPolicy.CanFileRequest(member, repo).IsDenied);
    }

    [Fact]
    public void AGrantNeverReachesAcrossOrganisations()
    {
        var outsider = AuthTestData.MemberIn(AuthTestData.OtherOrgId, MemberRole.Requester);
        var repo = AuthTestData.Repo(RepoStatus.Ready, AuthTestData.GrantTo(MemberRole.Requester));

        Assert.True(RepoAccessPolicy.CanFileRequest(outsider, repo).IsDenied);
        Assert.True(RepoAccessPolicy.CanReadRepository(outsider, repo).IsDenied);
    }

    [Fact]
    public void OnlyAnAdminMayChangeWhoCanFile()
    {
        var repo = AuthTestData.Repo();

        Assert.True(RepoAccessPolicy
            .CanAdministerRepositoryScope(AuthTestData.Member(MemberRole.Engineer), repo).IsDenied);

        var admin = RepoAccessPolicy.CanAdministerRepositoryScope(AuthTestData.Member(MemberRole.Admin), repo);

        Assert.True(admin.IsAllowed);
        Assert.Equal(AuditActions.RepoScopeGranted, admin.AuditAction);
    }
}

/// <summary>
/// Section 7.4: transcript and code visibility is gated on repository read access, not on what the
/// person asked to see.
/// </summary>
public class AuthSessionVisibilityTests
{
    [Fact]
    public void ARequesterSeesTheirOwnThreadButNeverTheTranscriptOrTheCode()
    {
        // The exact bypass section 7.4 names: a requester toggling views. There is no toggle to pass,
        // and being the person who filed the request buys nothing at the repository level.
        var requester = AuthTestData.Member(MemberRole.Requester);
        var repo = AuthTestData.Repo(RepoStatus.Ready, AuthTestData.GrantTo(requester));
        var session = AuthTestData.Session(requester);

        var visible = SessionVisibilityPolicy.For(requester, repo, session);

        Assert.True(visible.StatusThread);
        Assert.True(visible.Milestones);
        Assert.False(visible.Transcript);
        Assert.False(visible.Code);
        Assert.False(visible.RepositoryIdentity);
        Assert.False(visible.Cost);
    }

    [Fact]
    public void AnEngineerSeesEveryPane()
    {
        var engineer = AuthTestData.Member(MemberRole.Engineer);
        var requester = AuthTestData.Member(MemberRole.Requester);
        var repo = AuthTestData.Repo();
        var session = AuthTestData.Session(requester);

        var visible = SessionVisibilityPolicy.For(engineer, repo, session);

        Assert.True(visible.Transcript);
        Assert.True(visible.Code);
        Assert.True(visible.RepositoryIdentity);
        Assert.True(visible.Cost);
    }

    [Fact]
    public void AnApproverSeesCostButNotTheTranscript()
    {
        // Section 7.1: the approver gets a queue of specs with estimated cost, and nothing about the
        // repository. Transcripts leak file paths and environment variable names.
        var approver = AuthTestData.Member(MemberRole.Approver);
        var session = AuthTestData.Session(AuthTestData.Member(MemberRole.Requester));

        var visible = SessionVisibilityPolicy.For(approver, AuthTestData.Repo(), session);

        Assert.True(visible.Cost);
        Assert.True(visible.StatusThread);
        Assert.False(visible.Transcript);
        Assert.False(visible.Code);
    }

    [Fact]
    public void AnAdminWhoIsNotAnEngineerDoesNotGetTheCodePane()
    {
        // Roles are additive: an admin who needs the diff grants themselves the engineer role, which
        // is a deliberate audited act. Section 7.5: trust never escalates on its own.
        var admin = AuthTestData.Member(MemberRole.Admin);
        var session = AuthTestData.Session(AuthTestData.Member(MemberRole.Requester));

        var visible = SessionVisibilityPolicy.For(admin, AuthTestData.Repo(), session);

        Assert.False(visible.Transcript);
        Assert.False(visible.Code);
        Assert.True(visible.RepositoryIdentity);
    }

    [Fact]
    public void AMemberOfAnotherOrganisationSeesNothingAtAll()
    {
        var outsider = AuthTestData.MemberIn(AuthTestData.OtherOrgId, MemberRole.Engineer);
        var session = AuthTestData.Session(AuthTestData.Member(MemberRole.Requester));

        Assert.True(SessionVisibilityPolicy.For(outsider, AuthTestData.Repo(), session).IsEmpty);
    }

    [Fact]
    public void TheTranscriptAndCodeDecisionsAgreeWithTheProjection()
    {
        // The API may either ask a yes/no question or project a response. They must not disagree,
        // because one of them is what actually gets serialised.
        foreach (var member in new[]
                 {
                     AuthTestData.Member(MemberRole.Requester),
                     AuthTestData.Member(MemberRole.Approver),
                     AuthTestData.Member(MemberRole.Engineer),
                     AuthTestData.Member(MemberRole.Admin),
                     AuthTestData.Member(MemberRole.Admin, MemberRole.Engineer),
                 })
        {
            var repo = AuthTestData.Repo();
            var session = AuthTestData.Session(member);
            var visible = SessionVisibilityPolicy.For(member, repo, session);

            Assert.Equal(visible.Transcript, SessionVisibilityPolicy.CanViewTranscript(member, repo, session).IsAllowed);
            Assert.Equal(visible.Code, SessionVisibilityPolicy.CanViewCode(member, repo, session).IsAllowed);
        }
    }
}

/// <summary>The spend gate, and only the spend gate (section 7.5).</summary>
public class AuthSpecApprovalTests
{
    [Fact]
    public void ApprovingNeedsTheApproverRole()
    {
        var requester = AuthTestData.Member(MemberRole.Requester);
        var spec = AuthTestData.Spec(requester);

        Assert.True(SpecApprovalPolicy.CanApprove(requester, AuthTestData.Repo(), spec).IsDenied);
        Assert.True(SpecApprovalPolicy
            .CanApprove(AuthTestData.Member(MemberRole.Approver), AuthTestData.Repo(), spec).IsAllowed);
    }

    [Fact]
    public void AnApprovedSpecificationCannotBeApprovedTwice()
    {
        var approver = AuthTestData.Member(MemberRole.Approver);
        var spec = AuthTestData.Spec(approver, approved: true);

        Assert.True(SpecApprovalPolicy.CanApprove(approver, AuthTestData.Repo(), spec).IsDenied);
    }

    [Fact]
    public void SomeoneHoldingBothRolesMayApproveTheirOwnRequest()
    {
        // Deliberate. The one-member organisation of section 7.2 depends on it, and because the merge
        // gate is immovable the worst case is wasted tokens rather than shipped code.
        var solo = AuthTestData.Member(Member.AllRoles.ToArray());
        var decision = SpecApprovalPolicy.CanApprove(solo, AuthTestData.Repo(), AuthTestData.Spec(solo));

        Assert.True(decision.IsAllowed);
        Assert.Equal(AuditActions.SpecApproved, decision.AuditAction);
    }

    [Fact]
    public void ADisabledRepositoryAcceptsNoApprovals()
    {
        var approver = AuthTestData.Member(MemberRole.Approver);

        Assert.True(SpecApprovalPolicy
            .CanApprove(approver, AuthTestData.Repo(RepoStatus.Disabled), AuthTestData.Spec(approver))
            .IsDenied);
    }
}

/// <summary>
/// Section 26.10: repo creation is a privilege escalation, gated three ways.
/// </summary>
/// <remarks>
/// <para>
/// Only one of the three gates existed. <c>CHARTER_ALLOW_REPO_CREATION</c> parsed and was read by
/// nothing, the provider's own scope was never consulted, and
/// <c>MemberCapability.CanCreateRepo</c> was granted to the first admin and then never checked - so
/// the capability was a row nothing asked about and the instance switch was inert.
/// </para>
/// <para>
/// Every case below asserts the refusal names the gate that stopped it. A dead end that does not say
/// what to change is how an operator concludes the feature is broken rather than off.
/// </para>
/// </remarks>
public class AuthRepoCreationTests
{
    private static VersionControlCapabilities Provider(bool repoCreation)
        => VersionControlCapabilities.None with { RepoCreation = repoCreation };

    private static MemberSnapshot Admin(params MemberCapability[] capabilities)
        => AuthTestData.Member(MemberRole.Admin) with { Capabilities = capabilities };

    [Fact]
    public void AllThreeGatesOpenIsTheOnlyWayThrough()
    {
        var decision = RepoCreationPolicy.CanCreateRepo(
            Admin(MemberCapability.CanCreateRepo),
            instanceAllows: true,
            Provider(repoCreation: true));

        Assert.True(decision.IsAllowed);

        // Section 7.3, guardrail 5: the largest escalation Charter grants leaves a trail.
        Assert.Equal(AuditActions.RepoCreationAuthorized, decision.AuditAction);
    }

    [Fact]
    public void TheInstanceSwitchRefusesEvenAFullyCapableAdmin()
    {
        var decision = RepoCreationPolicy.CanCreateRepo(
            Admin(MemberCapability.CanCreateRepo),
            instanceAllows: false,
            Provider(repoCreation: true));

        Assert.True(decision.IsDenied);
        Assert.Contains("CHARTER_ALLOW_REPO_CREATION", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AProviderWithoutTheScopeRefusesRegardlessOfRole()
    {
        var decision = RepoCreationPolicy.CanCreateRepo(
            Admin(MemberCapability.CanCreateRepo),
            instanceAllows: true,
            Provider(repoCreation: false));

        Assert.True(decision.IsDenied);
        Assert.Contains("code host", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCapabilityIsWhatSeparatesAnAdminFromAnAdminWhoMayCreateRepositories()
    {
        // Section 26.10 calls it a distinct capability rather than a role for exactly this reason:
        // being an admin is not the same as holding it, and it is grantable to an engineer.
        var withoutIt = RepoCreationPolicy.CanCreateRepo(
            Admin(),
            instanceAllows: true,
            Provider(repoCreation: true));

        Assert.True(withoutIt.IsDenied);
        Assert.Contains("can_create_repo", withoutIt.Reason, StringComparison.Ordinal);

        var engineer = AuthTestData.Member(MemberRole.Engineer) with
        {
            Capabilities = [MemberCapability.CanCreateRepo],
        };

        Assert.True(RepoCreationPolicy
            .CanCreateRepo(engineer, instanceAllows: true, Provider(repoCreation: true))
            .IsAllowed);
    }

    [Fact]
    public void ARequesterMayNeverCreateARepository()
    {
        // Section 26.10: requesters may propose projects in Plan mode; they may not create them.
        var requester = AuthTestData.Member(MemberRole.Requester);

        Assert.True(RepoCreationPolicy
            .CanCreateRepo(requester, instanceAllows: true, Provider(repoCreation: true))
            .IsDenied);
    }
}
