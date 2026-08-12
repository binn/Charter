using Charter.Auth.Authorization;
using Charter.Auth.Setup;
using Charter.Configuration;
using Charter.Domain;

namespace Charter.Tests;

/// <summary>
/// Section 7.2: personal mode is not a mode.
/// </summary>
/// <remarks>
/// <para>
/// This is the regression test the design exists for. Personal mode is an organisation with one
/// member holding all roles and approval gates auto-satisfied by policy — same tables, same
/// authorisation code path, same checks, only the seeded defaults differ. The failure it guards
/// against is the branch <c>if (personalMode) skipPermissionCheck</c>, which works perfectly and
/// quietly turns organisation mode into the untested case.
/// </para>
/// <para>
/// The assertions come in three kinds: the same questions asked of a one-member organisation and a
/// twenty-member one give the same answers; flipping an organisation's mode changes nothing; and the
/// authorisation source does not mention the mode at all.
/// </para>
/// </remarks>
public class AuthPersonalModeTests
{
    /// <summary>Every question the API asks, applied to one member and one repository.</summary>
    private static IReadOnlyList<(string Question, bool Answer)> AskEverything(
        MemberSnapshot member,
        RepoSnapshot repo,
        IReadOnlyList<AutoDispatchPolicySnapshot> policies)
    {
        var session = AuthTestData.Session(member);
        var spec = AuthTestData.Spec(member);
        var visible = SessionVisibilityPolicy.For(member, repo, session);
        var dispatch = AutoDispatchPolicyResolver.Resolve(member, repo, policies);

        return
        [
            ("file", RepoAccessPolicy.CanFileRequest(member, repo).IsAllowed),
            ("read_repo", RepoAccessPolicy.CanReadRepository(member, repo).IsAllowed),
            ("administer_scope", RepoAccessPolicy.CanAdministerRepositoryScope(member, repo).IsAllowed),
            ("approve", SpecApprovalPolicy.CanApprove(member, repo, spec).IsAllowed),
            ("transcript", visible.Transcript),
            ("code", visible.Code),
            ("status_thread", visible.StatusThread),
            ("cost", visible.Cost),
            ("repo_identity", visible.RepositoryIdentity),
            ("auto_dispatch", dispatch.Enabled),
            ("auto_dispatch_at_1usd", dispatch.PermitsCost(1m)),
        ];
    }

    [Fact]
    public void AOneMemberOrganisationAndAMultiMemberOrganisationAnswerIdentically()
    {
        // The personal instance: one member, every role, an enabled org-default policy - exactly what
        // PersonalOrganizationSeeder writes.
        var solo = AuthTestData.Member(Member.AllRoles.ToArray());
        var soloRepo = AuthTestData.Repo(RepoStatus.Ready, AuthTestData.GrantTo(solo));
        var soloPolicies = new[] { AuthTestData.Policy(enabled: true) };

        // The organisation: several members, and one of them happens to hold every role. Nothing
        // about the shape of the rows is different - there are simply more of them.
        var admin = AuthTestData.Member(Member.AllRoles.ToArray());
        var otherRequester = AuthTestData.Member(MemberRole.Requester);
        var otherEngineer = AuthTestData.Member(MemberRole.Engineer);

        var orgRepo = AuthTestData.Repo(
            RepoStatus.Ready,
            AuthTestData.GrantTo(admin),
            AuthTestData.GrantTo(otherRequester),
            AuthTestData.GrantTo(otherEngineer, canRequest: false));

        var orgPolicies = new[]
        {
            AuthTestData.Policy(enabled: true),
            AuthTestData.Policy(enabled: false, userId: otherRequester.UserId),
        };

        Assert.Equal(
            AskEverything(solo, soloRepo, soloPolicies),
            AskEverything(admin, orgRepo, orgPolicies));
    }

    [Fact]
    public void PromotingAnOrganisationChangesNoAnswer()
    {
        // "Inviting a second user must be the only thing that changes, and it must need no
        // migration." The proof is that the mode column is not an input: flipping it moves nothing.
        var seeded = PersonalOrganizationSeeder.Seed("Solo", "solo@example.com", "Solo", "hash");

        var member = MemberSnapshot.From(seeded.Member);
        var repo = new RepoSnapshot
        {
            RepoId = AuthTestData.RepoId,
            OrgId = seeded.Organization.Id,
            Status = RepoStatus.Ready,
            Grants = [new RepoScopeGrant(seeded.Member.Id, null, true)],
        };

        var policies = new[] { AutoDispatchPolicySnapshot.From(seeded.AutoDispatch) };

        var before = AskEverything(member, repo, policies);

        Assert.Equal(OrganizationMode.Personal, seeded.Organization.Mode);
        seeded.Organization.PromoteToOrganization();
        Assert.Equal(OrganizationMode.Organization, seeded.Organization.Mode);

        Assert.Equal(before, AskEverything(member, repo, policies));
    }

    [Fact]
    public void ASecondMemberJoiningNeedsNothingBeyondARow()
    {
        // What actually changes when the second person arrives: a Member row with fewer roles, and a
        // scope row if they are to file anywhere. No schema change, no mode flip required.
        var seeded = PersonalOrganizationSeeder.Seed("Team", "first@example.com", "First", "hash");

        var newcomer = Member.Create(seeded.Organization.Id, Guid.CreateVersion7(), [MemberRole.Requester]);
        var newcomerSnapshot = MemberSnapshot.From(newcomer);

        var repoWithoutGrant = new RepoSnapshot
        {
            RepoId = AuthTestData.RepoId,
            OrgId = seeded.Organization.Id,
            Status = RepoStatus.Ready,
            Grants = [new RepoScopeGrant(seeded.Member.Id, null, true)],
        };

        // Deny by default still holds for the newcomer, in exactly the same organisation.
        Assert.True(RepoAccessPolicy.CanFileRequest(newcomerSnapshot, repoWithoutGrant).IsDenied);

        var repoWithGrant = repoWithoutGrant with
        {
            Grants = [.. repoWithoutGrant.Grants, new RepoScopeGrant(newcomer.Id, null, true)],
        };

        Assert.True(RepoAccessPolicy.CanFileRequest(newcomerSnapshot, repoWithGrant).IsAllowed);

        // And the roles they were not given are genuinely not held.
        Assert.True(RepoAccessPolicy.CanReadRepository(newcomerSnapshot, repoWithGrant).IsDenied);
        Assert.True(SpecApprovalPolicy
            .CanApprove(newcomerSnapshot, repoWithGrant, AuthTestData.Spec(newcomerSnapshot) with
            {
                OrgId = seeded.Organization.Id,
            })
            .IsDenied);
    }

    [Fact]
    public void SeedingAPersonalInstanceGivesOneMemberEveryRole()
    {
        var seeded = PersonalOrganizationSeeder.Seed("Solo", "solo@example.com", "Solo", "hash");

        Assert.Equal(Member.AllRoles.Order(), seeded.Member.Roles.Order());
        Assert.Equal(seeded.Organization.Id, seeded.Member.OrgId);
        Assert.Equal(seeded.User.Id, seeded.Member.UserId);

        // Section 7.5: "Personal mode - everything auto-dispatches. There is nobody to approve."
        Assert.True(seeded.AutoDispatch.Enabled);
        Assert.Null(seeded.AutoDispatch.RepoId);
        Assert.Null(seeded.AutoDispatch.Role);
        Assert.Null(seeded.AutoDispatch.UserId);

        // The password identity holds a hash, and the subject is the user's own id - there is no
        // external authority for the password provider to name.
        Assert.Equal(IdentityProviderKind.Password, seeded.Identity.Provider);
        Assert.Equal("hash", seeded.Identity.SecretHash);
        Assert.Equal(seeded.User.Id.ToString(), seeded.Identity.ProviderUserId);
    }

    [Fact]
    public void SeedingAnOrganisationDiffersOnlyInTheSeededPolicy()
    {
        var personal = PersonalOrganizationSeeder.Seed("A", "a@example.com", "A", "hash", CharterMode.Personal);
        var organisation = PersonalOrganizationSeeder.Seed("B", "b@example.com", "B", "hash", CharterMode.Organization);

        // Same member shape. This is the whole claim of section 7.2.
        Assert.Equal(personal.Member.Roles.Order(), organisation.Member.Roles.Order());
        Assert.Equal(personal.Member.Capabilities, organisation.Member.Capabilities);

        // Only the seeded default differs: an organisation starts with approval required.
        Assert.True(personal.AutoDispatch.Enabled);
        Assert.False(organisation.AutoDispatch.Enabled);
    }

    [Fact]
    public void TheAuthorisationRulesCannotSeeTheOrganisationMode()
    {
        // A behaviour test can only show that today's rules ignore the mode. This shows that
        // tomorrow's cannot start reading it without the test noticing - which is the actual
        // requirement, because the branch is added later, by someone in a hurry.
        var directory = Path.Combine(RepositoryRoot(), "src", "Charter", "Auth", "Authorization");

        Assert.True(Directory.Exists(directory), $"Expected the authorisation rules at {directory}.");

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            var body = StripComments(File.ReadAllText(file));

            if (body.Contains("OrganizationMode", StringComparison.Ordinal)
                || body.Contains("CharterMode", StringComparison.Ordinal)
                || body.Contains(".Mode", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Section 7.2: the authorisation rules must not branch on how an organisation is operated. "
            + $"Found a reference in: {string.Join(", ", offenders)}");
    }

    /// <summary>Drops comments so the prose above a rule can discuss the mode without failing this.</summary>
    private static string StripComments(string source)
    {
        var kept = new List<string>();
        var inBlock = false;

        foreach (var line in source.Split('\n'))
        {
            var trimmed = line.TrimStart();

            if (inBlock)
            {
                if (trimmed.Contains("*/", StringComparison.Ordinal))
                {
                    inBlock = false;
                }

                continue;
            }

            if (trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                inBlock = !trimmed.Contains("*/", StringComparison.Ordinal);
                continue;
            }

            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            kept.Add(line);
        }

        return string.Join('\n', kept);
    }

    /// <summary>
    /// The repository root, found by walking up from the test assembly until the solution file
    /// appears.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>[CallerFilePath]</c>. <c>Directory.Build.props</c> turns on
    /// <c>DeterministicSourcePaths</c> whenever <c>CI=true</c>, which rewrites every embedded source
    /// path to <c>/_/…</c> — so a caller path resolves to a directory that exists on no machine, and
    /// this test failed in CI while passing on every developer's laptop. Walking the filesystem is
    /// the only form of this that is true in both places.
    /// </remarks>
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Charter.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"No Charter.sln above {AppContext.BaseDirectory}, so the source tree cannot be located.");
    }
}
