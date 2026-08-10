using Charter.Auth;
using Charter.Auth.Audit;
using Charter.Auth.Authorization;
using Charter.Auth.Providers;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>
/// The authorisation service, the scope administration path and password sign-in, against a real
/// Postgres.
/// </summary>
/// <remarks>
/// The rules themselves are tested without a database elsewhere; what these cover is the wiring —
/// that the service loads the rows the rules need, that a missing scope row really does reach the
/// deny-by-default path, and that a notable grant leaves an audit entry. Skips unless
/// <c>CHARTER_TEST_DATABASE_URL</c> is set, following the job queue's pattern.
/// </remarks>
public class AuthServiceIntegrationTests
{
    [Fact]
    public async Task ANewlyConnectedRepositoryIsRequestableByNobodyThroughTheService()
    {
        await using var world = await AuthWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var repo = await world.ConnectRepoAsync(RepoStatus.Ready);

        // Section 7.3: no repo_scopes rows exist, so nobody may file - not even the admin who owns
        // every role in the organisation.
        Assert.True((await world.Authorization.CanFileRequestAsync(
            world.Admin,
            repo.Id,
            TestContext.Current.CancellationToken)).IsDenied);

        Assert.True((await world.Authorization.CanFileRequestAsync(
            world.Requester,
            repo.Id,
            TestContext.Current.CancellationToken)).IsDenied);
    }

    [Fact]
    public async Task GrantingScopeNeedsTheAdminRoleAndLeavesAnAuditTrail()
    {
        await using var world = await AuthWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var repo = await world.ConnectRepoAsync(RepoStatus.Ready);

        // An engineer cannot widen who may file. Section 7.1 puts that with the admin.
        Assert.True((await world.Scopes.SetMemberScopeAsync(
            world.Engineer,
            repo.Id,
            world.Requester.MemberId,
            canRequest: true,
            TestContext.Current.CancellationToken)).IsDenied);

        Assert.True((await world.Authorization.CanFileRequestAsync(
            world.Requester,
            repo.Id,
            TestContext.Current.CancellationToken)).IsDenied);

        var granted = await world.Scopes.SetMemberScopeAsync(
            world.Admin,
            repo.Id,
            world.Requester.MemberId,
            canRequest: true,
            TestContext.Current.CancellationToken);

        Assert.True(granted.IsAllowed);

        Assert.True((await world.Authorization.CanFileRequestAsync(
            world.Requester,
            repo.Id,
            TestContext.Current.CancellationToken)).IsAllowed);

        // Section 7.3, guardrail 5: attributable to a named human.
        var entry = await world.Db.AuditLogs.SingleAsync(
            row => row.Action == AuditActions.RepoScopeGranted,
            TestContext.Current.CancellationToken);

        Assert.Equal(world.Admin.UserId, entry.ActorUserId);
        Assert.Equal(repo.Id.ToString(), entry.TargetId);
        Assert.Contains(world.Requester.MemberId.ToString(), entry.Metadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevokingScopeClosesTheRepositoryAgain()
    {
        await using var world = await AuthWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var repo = await world.ConnectRepoAsync(RepoStatus.Ready);

        await world.Scopes.GrantOnConnectAsync(world.Admin, repo.Id, TestContext.Current.CancellationToken);
        Assert.True((await world.Authorization.CanFileRequestAsync(
            world.Admin,
            repo.Id,
            TestContext.Current.CancellationToken)).IsAllowed);

        await world.Scopes.SetMemberScopeAsync(
            world.Admin,
            repo.Id,
            world.Admin.MemberId,
            canRequest: false,
            TestContext.Current.CancellationToken);

        Assert.True((await world.Authorization.CanFileRequestAsync(
            world.Admin,
            repo.Id,
            TestContext.Current.CancellationToken)).IsDenied);

        Assert.True(await world.Db.AuditLogs.AnyAsync(
            row => row.Action == AuditActions.RepoScopeRevoked,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task VisibilityAndApprovalResolveThroughTheRealRowGraph()
    {
        await using var world = await AuthWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var repo = await world.ConnectRepoAsync(RepoStatus.Ready);
        var session = await world.FileAndBuildAsync(repo, world.Requester);

        // Section 7.4: the requester follows their own thread and sees neither pane.
        var requesterView = await world.Authorization.ResolveSessionVisibilityAsync(
            world.Requester,
            session.Id,
            TestContext.Current.CancellationToken);

        Assert.True(requesterView.StatusThread);
        Assert.False(requesterView.Transcript);
        Assert.False(requesterView.Code);

        var engineerView = await world.Authorization.ResolveSessionVisibilityAsync(
            world.Engineer,
            session.Id,
            TestContext.Current.CancellationToken);

        Assert.True(engineerView.Transcript);
        Assert.True(engineerView.Code);

        Assert.True((await world.Authorization.CanViewTranscriptAsync(
            world.Requester,
            session.Id,
            TestContext.Current.CancellationToken)).IsDenied);

        Assert.True((await world.Authorization.CanViewCodeAsync(
            world.Engineer,
            session.Id,
            TestContext.Current.CancellationToken)).IsAllowed);

        // Section 7.5: the spend gate needs the approver role, and the admin here holds it.
        Assert.True((await world.Authorization.CanApproveSpecAsync(
            world.Requester,
            session.SpecId,
            TestContext.Current.CancellationToken)).IsDenied);

        var approve = await world.Authorization.CanApproveSpecAsync(
            world.Admin,
            session.SpecId,
            TestContext.Current.CancellationToken);

        Assert.True(approve.IsAllowed);
        Assert.Equal(AuditActions.SpecApproved, approve.AuditAction);

        // The decision already knows its own audit verb, so recording it is one call.
        await world.Audit.RecordGrantAsync(
            world.Admin,
            approve,
            "spec",
            session.SpecId.ToString(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(await world.Db.AuditLogs.AnyAsync(
            row => row.Action == AuditActions.SpecApproved && row.ActorUserId == world.Admin.UserId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnUnknownRepositoryIsRefusedInTheSameWordsAsAnUnscopedOne()
    {
        await using var world = await AuthWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        // Otherwise the permission answer becomes a repository-existence oracle for a requester who
        // is never supposed to learn a repository name at all (section 7.1).
        var unknown = await world.Authorization.CanFileRequestAsync(
            world.Requester,
            Guid.CreateVersion7(),
            TestContext.Current.CancellationToken);

        Assert.True(unknown.IsDenied);
        Assert.Equal("no repository by that id is open to you", unknown.Reason);
    }

    [Fact]
    public async Task AutoDispatchResolvesFromRealPolicyRowsAndTheRepositorysOwnFile()
    {
        await using var world = await AuthWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var repo = await world.ConnectRepoAsync(RepoStatus.Ready);

        world.Db.AutoDispatchPolicies.Add(AutoDispatchPolicy.Create(
            world.OrgId,
            enabled: true,
            maxCostUsd: 20m));

        await world.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var loose = await world.Authorization.ResolveAutoDispatchAsync(
            world.Requester,
            repo.Id,
            TestContext.Current.CancellationToken);

        Assert.True(loose.Enabled);
        Assert.Equal(20m, loose.MaxCostUsd);

        // The repository tightens itself in its own reviewable file, and wins.
        var tracked = await world.Db.Repos.SingleAsync(
            row => row.Id == repo.Id,
            TestContext.Current.CancellationToken);

        tracked.RecordConfigSnapshot("""{"limits":{"max_session_usd":2},"scopes":{"allow":["src/Features/**"]}}""");
        await world.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var tightened = await world.Authorization.ResolveAutoDispatchAsync(
            world.Requester,
            repo.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(2m, tightened.MaxCostUsd);
        Assert.Equal(["src/Features/**"], tightened.AllowedPaths);
        Assert.True(tightened.IsAtLeastAsRestrictiveAs(loose));
    }

    [Fact]
    public async Task PasswordSignInAcceptsTheRightPasswordAndRefusesEverythingElseIdentically()
    {
        await using var world = await AuthWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var provider = world.PasswordProvider;

        Assert.True(await provider.SetPasswordAsync(
            world.Admin.UserId,
            new Secret("a-long-enough-password"),
            TestContext.Current.CancellationToken));

        var ok = await provider.BeginAsync(
            new IdentityAuthenticationAttempt
            {
                Email = world.AdminEmail,
                Password = new Secret("a-long-enough-password"),
            },
            TestContext.Current.CancellationToken);

        var authenticated = Assert.IsType<IdentityAuthenticationResult.Authenticated>(ok);
        Assert.Equal(IdentityProviderKind.Password, authenticated.Identity.Provider);
        Assert.Equal(world.AdminEmail, authenticated.Identity.Email);

        var wrongPassword = await provider.BeginAsync(
            new IdentityAuthenticationAttempt
            {
                Email = world.AdminEmail,
                Password = new Secret("a-long-enough-passwerd"),
            },
            TestContext.Current.CancellationToken);

        var unknownUser = await provider.BeginAsync(
            new IdentityAuthenticationAttempt
            {
                Email = "nobody@example.com",
                Password = new Secret("a-long-enough-password"),
            },
            TestContext.Current.CancellationToken);

        // The same refusal either way: never tell a stranger which half was wrong.
        var wrong = Assert.IsType<IdentityAuthenticationResult.Failed>(wrongPassword);
        var unknown = Assert.IsType<IdentityAuthenticationResult.Failed>(unknownUser);

        Assert.Equal(IdentityFailureReason.InvalidCredentials, wrong.Reason);
        Assert.Equal(IdentityFailureReason.InvalidCredentials, unknown.Reason);
        Assert.Equal(wrong.Message, unknown.Message);
    }

    [Fact]
    public async Task SignInIsThrottledAfterEnoughWrongGuesses()
    {
        await using var world = await AuthWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await world.PasswordProvider.SetPasswordAsync(
            world.Admin.UserId,
            new Secret("a-long-enough-password"),
            TestContext.Current.CancellationToken);

        IdentityAuthenticationResult last = new IdentityAuthenticationResult.Failed(
            IdentityFailureReason.MalformedAttempt,
            "unused");

        for (var attempt = 0; attempt < SignInThrottle.DefaultMaxFailures + 1; attempt++)
        {
            last = await world.PasswordProvider.BeginAsync(
                new IdentityAuthenticationAttempt
                {
                    Email = world.AdminEmail,
                    Password = new Secret("wrong-but-long-enough"),
                    ClientKey = "203.0.113.7",
                },
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(IdentityFailureReason.Throttled, Assert.IsType<IdentityAuthenticationResult.Failed>(last).Reason);
    }

    [Fact]
    public async Task AFederatedSignInLinksToAnExistingUserAndNeverCreatesOne()
    {
        await using var world = await AuthWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var stranger = await world.Linker.ResolveAsync(
            new ExternalIdentity(IdentityProviderKind.GitHub, "999", "stranger@example.com", "Stranger"),
            TestContext.Current.CancellationToken);

        // Open registration through the side door is exactly what section 30.1 exists to prevent.
        Assert.IsType<IdentityResolution.NoAccount>(stranger);

        var first = await world.Linker.ResolveAsync(
            new ExternalIdentity(IdentityProviderKind.GitHub, "123", world.AdminEmail, "Admin"),
            TestContext.Current.CancellationToken);

        Assert.Equal(world.Admin.UserId, Assert.IsType<IdentityResolution.NewlyLinked>(first).User.Id);

        var second = await world.Linker.ResolveAsync(
            new ExternalIdentity(IdentityProviderKind.GitHub, "123", null, null),
            TestContext.Current.CancellationToken);

        // Second time round the subject is what matches, not the email.
        Assert.Equal(world.Admin.UserId, Assert.IsType<IdentityResolution.Linked>(second).User.Id);

        Assert.True(await world.Db.AuditLogs.AnyAsync(
            row => row.Action == AuditActions.IdentityLinked,
            TestContext.Current.CancellationToken));
    }

    /// <summary>An organisation with three members, in a Postgres schema of its own.</summary>
    private sealed class AuthWorld : IAsyncDisposable
    {
        private const string DatabaseUrlVariable = "CHARTER_TEST_DATABASE_URL";

        private readonly string schema;

        private AuthWorld(CharterDbContext db, string schema)
        {
            Db = db;
            this.schema = schema;
            Audit = new AuditWriter(db, TimeProvider.System);
            Authorization = new CharterAuthorizationService(db, Audit);
            Scopes = new RepoScopeAdministration(db, Audit);
            Linker = new IdentityLinker(db, Audit);
            PasswordProvider = new PasswordIdentityProvider(
                db,
                new CharterPasswordHasher(iterationCount: 1_000),
                new SignInThrottle(TimeProvider.System),
                NullLogger<PasswordIdentityProvider>.Instance);
        }

        public CharterDbContext Db { get; }

        public IAuditWriter Audit { get; }

        public ICharterAuthorizationService Authorization { get; }

        public IRepoScopeAdministration Scopes { get; }

        public IIdentityLinker Linker { get; }

        public PasswordIdentityProvider PasswordProvider { get; }

        public Guid OrgId { get; private set; }

        public MemberSnapshot Admin { get; private set; } = null!;

        public MemberSnapshot Engineer { get; private set; } = null!;

        public MemberSnapshot Requester { get; private set; } = null!;

        public string AdminEmail { get; private set; } = string.Empty;

        public static async Task<AuthWorld?> CreateAsync()
        {
            var url = Environment.GetEnvironmentVariable(DatabaseUrlVariable);
            if (string.IsNullOrWhiteSpace(url))
            {
                Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the authorisation service tests.");
                return null;
            }

            var schema = $"auth_svc_{Guid.CreateVersion7():N}";

            var admin = new CharterDbContext(Configure(url, schema: null));
            await admin.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            var create = string.Concat("CREATE SCHEMA ", Quote(schema));
            await admin.Database.ExecuteSqlRawAsync(create, TestContext.Current.CancellationToken);
            await admin.DisposeAsync();

            var db = new CharterDbContext(Configure(url, schema));
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

            var world = new AuthWorld(db, schema);
            await world.SeedAsync();

            return world;
        }

        public async Task<Repo> ConnectRepoAsync(RepoStatus status)
        {
            var repo = Repo.Connect(OrgId, githubInstallationId: 42, fullName: "acme/widgets");
            repo.TransitionTo(status);

            Db.Repos.Add(repo);
            await Db.SaveChangesAsync(TestContext.Current.CancellationToken);

            return repo;
        }

        /// <summary>A request, a specification and a session, so the visibility graph is real.</summary>
        public async Task<Session> FileAndBuildAsync(Repo repo, MemberSnapshot requester)
        {
            var request = Request.File(OrgId, repo.Id, requester.UserId, "Make the button blue");
            var spec = Spec.Draft(request.Id, 1, "Blue button", "The button turns blue", "body", "[]");
            var session = Session.Queue(spec.Id, RunnerKind.Agent, "anthropic/claude-opus-5");

            Db.Requests.Add(request);
            Db.Specs.Add(spec);
            Db.Sessions.Add(session);
            await Db.SaveChangesAsync(TestContext.Current.CancellationToken);

            return session;
        }

        private async Task SeedAsync()
        {
            var organization = Organization.Create("Acme", OrganizationMode.Organization);
            OrgId = organization.Id;
            AdminEmail = $"admin-{schema}@example.com";

            var adminUser = User.Create(AdminEmail, "Admin");
            var engineerUser = User.Create($"engineer-{schema}@example.com", "Engineer");
            var requesterUser = User.Create($"requester-{schema}@example.com", "Requester");

            var adminMember = Member.Create(
                organization.Id,
                adminUser.Id,
                [MemberRole.Admin, MemberRole.Approver]);
            var engineerMember = Member.Create(organization.Id, engineerUser.Id, [MemberRole.Engineer]);
            var requesterMember = Member.Create(organization.Id, requesterUser.Id, [MemberRole.Requester]);

            Db.Organizations.Add(organization);
            Db.Users.AddRange(adminUser, engineerUser, requesterUser);
            Db.Members.AddRange(adminMember, engineerMember, requesterMember);
            await Db.SaveChangesAsync(TestContext.Current.CancellationToken);

            Admin = MemberSnapshot.From(adminMember);
            Engineer = MemberSnapshot.From(engineerMember);
            Requester = MemberSnapshot.From(requesterMember);
        }

        private static DbContextOptions<CharterDbContext> Configure(string url, string? schema)
        {
            var builder = new Npgsql.NpgsqlConnectionStringBuilder(DatabaseUrl.ToNpgsql(url));

            if (schema is not null)
            {
                builder.SearchPath = schema;
            }

            var options = new DbContextOptionsBuilder<CharterDbContext>();
            DataServiceCollectionExtensions.ConfigureNpgsql(options, builder.ConnectionString);

            return options.Options;
        }

        private static string Quote(string identifier)
            => string.Concat("\"", identifier.Replace("\"", "\"\"", StringComparison.Ordinal), "\"");

        public async ValueTask DisposeAsync()
        {
            var drop = string.Concat("DROP SCHEMA IF EXISTS ", Quote(schema), " CASCADE");
            await Db.Database.ExecuteSqlRawAsync(drop);
            await Db.DisposeAsync();
        }
    }
}
