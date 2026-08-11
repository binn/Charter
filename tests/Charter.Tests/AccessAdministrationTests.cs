using Charter.Api.Accounts;
using Charter.Api.Contracts;
using Charter.Api.Requests;
using Charter.Auth;
using Charter.Auth.Audit;
using Charter.Auth.Authorization;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Charter.Onboarding;
using Charter.VersionControl;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Charter.Tests;

/// <summary>
/// Members, roles and the audit log, against a real Postgres (sections 7.1, 7.3, 7.4).
/// </summary>
/// <remarks>
/// <para>
/// <c>member.role.granted</c> and <c>member.role.revoked</c> existed as constants with no writer.
/// Privilege escalation is the single thing an audit log must never miss, so the first thing these
/// assert is that a role change leaves a row naming the administrator who made it — and that a
/// change which changes nothing leaves none, because a log full of no-ops is one nobody reads.
/// </para>
/// <para>
/// The rest is section 7.4 on the wire: a member who may not read a surface receives nothing, not a
/// filtered copy of it. They run only when <c>CHARTER_TEST_DATABASE_URL</c> points at a throwaway
/// Postgres, and each builds its own organisation.
/// </para>
/// </remarks>
public class AccessAdministrationTests
{
    [Fact]
    public async Task GrantingARoleWritesTheAuditVerbThatHadNoWriter()
    {
        await using var world = await AdminWorld.CreateAsync();

        var (outcome, updated) = await world.Members.SetRoleAsync(
            world.Admin,
            world.RequesterMemberId,
            new SetMemberRoleBody { Role = ApiRole.Engineer, Granted = true },
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.NotNull(updated);

        // Roles are additive (section 7.1): the one they had is still there.
        Assert.Contains(ApiRole.Engineer, updated.Roles);
        Assert.Contains(ApiRole.Requester, updated.Roles);

        var entry = await world.Db.AuditLogs
            .AsNoTracking()
            .Where(row => row.Action == AuditActions.MemberRoleGranted
                && row.TargetId == world.RequesterMemberId.ToString())
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(world.Admin.UserId, entry.ActorUserId);
        Assert.Contains("engineer", entry.Metadata ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevokingARoleIsAuditedToo()
    {
        await using var world = await AdminWorld.CreateAsync();

        await world.Members.SetRoleAsync(
            world.Admin,
            world.RequesterMemberId,
            new SetMemberRoleBody { Role = ApiRole.Approver, Granted = true },
            TestContext.Current.CancellationToken);

        var (outcome, updated) = await world.Members.SetRoleAsync(
            world.Admin,
            world.RequesterMemberId,
            new SetMemberRoleBody { Role = ApiRole.Approver, Granted = false },
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.NotNull(updated);
        Assert.DoesNotContain(ApiRole.Approver, updated.Roles);

        Assert.True(
            await world.Db.AuditLogs.AnyAsync(
                row => row.Action == AuditActions.MemberRoleRevoked
                    && row.TargetId == world.RequesterMemberId.ToString(),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARoleChangeThatChangesNothingWritesNothing()
    {
        await using var world = await AdminWorld.CreateAsync();

        var (outcome, _) = await world.Members.SetRoleAsync(
            world.Admin,
            world.RequesterMemberId,
            new SetMemberRoleBody { Role = ApiRole.Requester, Granted = true },
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);

        Assert.False(
            await world.Db.AuditLogs.AnyAsync(
                row => row.Action == AuditActions.MemberRoleGranted && row.OrgId == world.OrgId,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheLastAdministratorCannotBeRemoved()
    {
        await using var world = await AdminWorld.CreateAsync();

        var (outcome, updated) = await world.Members.SetRoleAsync(
            world.Admin,
            world.Admin.MemberId,
            new SetMemberRoleBody { Role = ApiRole.Admin, Granted = false },
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Equal(StatusCodes.Status409Conflict, outcome.Status);
        Assert.Contains("last administrator", outcome.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(updated);

        // And the row is untouched, not merely the response.
        var still = await world.Db.Members
            .AsNoTracking()
            .SingleAsync(row => row.Id == world.Admin.MemberId, TestContext.Current.CancellationToken);

        Assert.Contains(MemberRole.Admin, still.Roles);
    }

    [Fact]
    public async Task ASecondAdministratorMakesTheFirstDemotable()
    {
        await using var world = await AdminWorld.CreateAsync();

        await world.Members.SetRoleAsync(
            world.Admin,
            world.RequesterMemberId,
            new SetMemberRoleBody { Role = ApiRole.Admin, Granted = true },
            TestContext.Current.CancellationToken);

        var (outcome, updated) = await world.Members.SetRoleAsync(
            world.Admin,
            world.Admin.MemberId,
            new SetMemberRoleBody { Role = ApiRole.Admin, Granted = false },
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.NotNull(updated);
        Assert.DoesNotContain(ApiRole.Admin, updated.Roles);
    }

    [Fact]
    public async Task AMemberCannotBeLeftWithNoRoleAtAll()
    {
        await using var world = await AdminWorld.CreateAsync();

        var (outcome, _) = await world.Members.SetRoleAsync(
            world.Admin,
            world.RequesterMemberId,
            new SetMemberRoleBody { Role = ApiRole.Requester, Granted = false },
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Equal(StatusCodes.Status409Conflict, outcome.Status);
        Assert.Contains("no role at all", outcome.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MembersAndTheAuditLogAreRefusedToEverybodyBelowAdministrator()
    {
        await using var world = await AdminWorld.CreateAsync();
        var engineer = world.Admin with { Roles = [MemberRole.Engineer] };

        var (memberOutcome, listed) = await world.Members.ListAsync(
            engineer,
            TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status403Forbidden, memberOutcome.Status);

        // Section 7.4: not an empty list, not a list of nulls. Nothing.
        Assert.Null(listed);

        var (auditOutcome, log) = await world.Audit.ListAsync(engineer, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status403Forbidden, auditOutcome.Status);
        Assert.Null(log);
    }

    [Fact]
    public async Task TheMemberListNamesEverybodyAndSaysWhichOneIsYou()
    {
        await using var world = await AdminWorld.CreateAsync();

        var (outcome, listed) = await world.Members.ListAsync(world.Admin, TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.NotNull(listed);
        Assert.Equal(2, listed.Members.Count);

        var you = Assert.Single(listed.Members, member => member.IsYou);
        Assert.Equal(world.Admin.MemberId.ToString(), you.Id);
        Assert.Contains(ApiRole.Admin, you.Roles);
    }

    [Fact]
    public async Task TheAuditLogReadsBackWithTheActorNamedAndTheVerbBesideTheSentence()
    {
        await using var world = await AdminWorld.CreateAsync();

        await world.Members.SetRoleAsync(
            world.Admin,
            world.RequesterMemberId,
            new SetMemberRoleBody { Role = ApiRole.Engineer, Granted = true },
            TestContext.Current.CancellationToken);

        var (outcome, log) = await world.Audit.ListAsync(world.Admin, TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.NotNull(log);

        var entry = Assert.Single(log.Entries, row => row.Action == AuditActions.MemberRoleGranted);

        Assert.Equal("Ada Okafor", entry.ActorName);
        Assert.Contains("an engineer", entry.Summary, StringComparison.Ordinal);
        Assert.Equal("member.role.granted", entry.Action);
    }

    [Fact]
    public async Task AnEntryNobodyIsNamedForStillAppears()
    {
        await using var world = await AdminWorld.CreateAsync();

        // Section 7.3: the agent never acts on its own initiative, so the few entries with nobody's
        // name on them are exactly the ones an operator wants to be able to find.
        await world.Audits.RecordAsync(
            new AuditEntry
            {
                OrgId = world.OrgId,
                Action = OnboardingAuditActions.RepoReady,
                TargetType = nameof(Repo),
                TargetId = Guid.CreateVersion7().ToString(),
            },
            TestContext.Current.CancellationToken);

        var (_, log) = await world.Audit.ListAsync(world.Admin, TestContext.Current.CancellationToken);

        Assert.NotNull(log);

        var entry = Assert.Single(log.Entries, row => row.Action == OnboardingAuditActions.RepoReady);

        Assert.Null(entry.ActorName);
        Assert.Contains("requestable", entry.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAuditLogLeavesTheReconProposalOutOfItsDetails()
    {
        await using var world = await AdminWorld.CreateAsync();

        // A scope proposal writes a structured payload into its metadata. It belongs to the wizard
        // that renders it, not to a list of what happened.
        await world.Audits.RecordAsync(
            new AuditEntry
            {
                OrgId = world.OrgId,
                ActorUserId = world.Admin.UserId,
                Action = OnboardingAuditActions.ScopeProposed,
                TargetType = nameof(Repo),
                TargetId = Guid.CreateVersion7().ToString(),
                Metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["pull_request"] = "42",
                    ["recon"] = ReconSnapshot
                        .From(
                            new ReconReport { DetectedStack = ["dotnet:10"], ProposedAllow = ["src/Features/**"] },
                            ScopeProposal.Propose(["src/Features/**"]))
                        .ToJson(),
                },
            },
            TestContext.Current.CancellationToken);

        var (_, log) = await world.Audit.ListAsync(world.Admin, TestContext.Current.CancellationToken);

        Assert.NotNull(log);

        var entry = Assert.Single(log.Entries, row => row.Action == OnboardingAuditActions.ScopeProposed);

        Assert.NotNull(entry.Details);
        Assert.Equal("42", entry.Details["pull_request"]);
        Assert.DoesNotContain("recon", entry.Details.Keys);
    }

    /// <summary>
    /// Section 7.1, on an endpoint this work did not write.
    /// </summary>
    /// <remarks>
    /// The assertion is about the <em>contract</em> rather than about one projection: a requester
    /// "never sees a repo name, branch, diff or token count", so <c>owner/repo</c> appearing anywhere
    /// in the project list would break the rule however it got in. Read from the rendered bytes for
    /// the reason section 7.4 gives — a shape-aware read would pass on a body containing the key
    /// with a null value, and that body would leak the fact that there is something to hide.
    /// </remarks>
    [Fact]
    public async Task ARequestersProjectListCarriesNoRepositoryNameOrBranch()
    {
        await using var world = await AdminWorld.CreateAsync();
        var requester = await world.MakeRequestableAsync();

        var projects = await world.Requests.ListProjectsAsync(requester, TestContext.Current.CancellationToken);

        Assert.NotEmpty(projects);

        var body = await ApiPayloads.RenderAsync(projects);
        var keys = ApiPayloads.Keys(body);

        Assert.DoesNotContain("fullName", keys);
        Assert.DoesNotContain("baseBranch", keys);
        Assert.DoesNotContain("status", keys);
        Assert.DoesNotContain(AdminWorld.RepositoryFullName, body, StringComparison.Ordinal);
    }

    /// <summary>One organisation, one administrator and one requester, on a real database.</summary>
    private sealed class AdminWorld : IAsyncDisposable
    {
        public const string RepositoryFullName = "northbeam/quote-tool";

        private readonly Organization organization;
        private readonly Member requesterMember;

        private AdminWorld(CharterDbContext db, Organization organization, Member requesterMember)
        {
            Db = db;
            this.organization = organization;
            this.requesterMember = requesterMember;

            Audits = new AuditWriter(db, TimeProvider.System);
            Members = new MembersService(db, Audits);
            Audit = new AuditQueryService(db);

            Requests = new RequestQueryService(
                db,
                new CharterAuthorizationService(db, Audits),
                new VersionControlProviderRegistry([]),
                TimeProvider.System);
        }

        public CharterDbContext Db { get; }

        public IAuditWriter Audits { get; }

        public MembersService Members { get; }

        public AuditQueryService Audit { get; }

        public RequestQueryService Requests { get; }

        public MemberSnapshot Admin { get; private set; } = null!;

        public Guid OrgId => organization.Id;

        public Guid RequesterMemberId => requesterMember.Id;

        public static async Task<AdminWorld> CreateAsync()
        {
            var url = Environment.GetEnvironmentVariable("CHARTER_TEST_DATABASE_URL");

            Assert.SkipWhen(
                string.IsNullOrWhiteSpace(url),
                "Set CHARTER_TEST_DATABASE_URL to a throwaway Postgres to run the access tests.");

            var options = new DbContextOptionsBuilder<CharterDbContext>();
            DataServiceCollectionExtensions.ConfigureNpgsql(options, DatabaseUrl.ToNpgsql(url!));

            var db = new CharterDbContext(options.Options);
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

            var tag = Guid.CreateVersion7().ToString("N");
            var organization = Organization.Create($"access-{tag}", OrganizationMode.Organization);

            var adminUser = User.Create($"ada-{tag}@example.test", "Ada Okafor");
            var requesterUser = User.Create($"priya-{tag}@example.test", "Priya Raman");

            // Two roles, so the last-administrator guard is the one under test rather than the
            // "a member must keep at least one role" guard that would otherwise fire first.
            var adminMember = Member.Create(organization.Id, adminUser.Id, [MemberRole.Admin, MemberRole.Engineer]);
            var requesterMember = Member.Create(organization.Id, requesterUser.Id, [MemberRole.Requester]);

            db.Organizations.Add(organization);
            db.Users.AddRange(adminUser, requesterUser);
            db.Members.AddRange(adminMember, requesterMember);

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.ChangeTracker.Clear();

            return new AdminWorld(db, organization, requesterMember)
            {
                Admin = MemberSnapshot.From(adminMember),
            };
        }

        /// <summary>A ready repository the requester is scoped to, so the project list has a row.</summary>
        public async Task<MemberSnapshot> MakeRequestableAsync()
        {
            var repo = Repo.Connect(organization.Id, 4242, RepositoryFullName);
            repo.TransitionTo(RepoStatus.Ready);

            Db.Repos.Add(repo);
            Db.RepoScopes.Add(RepoScope.ForMember(repo.Id, requesterMember.Id, canRequest: true));

            await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
            Db.ChangeTracker.Clear();

            return MemberSnapshot.From(requesterMember);
        }

        public async ValueTask DisposeAsync() => await Db.DisposeAsync();
    }
}
