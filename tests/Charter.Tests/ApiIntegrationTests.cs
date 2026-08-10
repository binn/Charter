using System.Text.Json;
using Charter.Api.Contracts;
using Charter.Api.Requests;
using Charter.Auth.Audit;
using Charter.Auth.Authorization;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Charter.Hubs;
using Microsoft.EntityFrameworkCore;

namespace Charter.Tests;

/// <summary>
/// The read and write paths against a real Postgres.
/// </summary>
/// <remarks>
/// The projection tests prove what the serialiser writes; these prove that the rows reaching it were
/// loaded and filtered correctly, which is the other half of section 7.4 and cannot be checked
/// without a database. They run only when <c>CHARTER_TEST_DATABASE_URL</c> points at a throwaway
/// Postgres and skip otherwise, so a developer without Docker still gets a green build. Each test
/// creates its own organisation, so a shared database stays usable.
/// </remarks>
public class ApiIntegrationTests
{
    private const string DatabaseUrlVariable = "CHARTER_TEST_DATABASE_URL";

    [Fact]
    public async Task ARequesterSeesTheirOwnRequestAndAnOutsiderGetsNothing()
    {
        await using var fixture = await ApiFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var queries = fixture.Queries();

        var mine = await queries.LoadAsync(fixture.Requester, fixture.RequestId, TestContext.Current.CancellationToken);
        Assert.NotNull(mine);
        Assert.False(mine.Visibility.Transcript);

        // Section 7.3: another organisation's member gets the same answer as "no such request".
        var theirs = await queries.LoadAsync(fixture.Outsider, fixture.RequestId, TestContext.Current.CancellationToken);
        Assert.Null(theirs);
    }

    [Fact]
    public async Task TheLoadedBodyOmitsTheEngineerKeysForARequesterAndCarriesThemForAnEngineer()
    {
        await using var fixture = await ApiFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var queries = fixture.Queries();

        var requesterView = await queries.LoadAsync(
            fixture.Requester,
            fixture.RequestId,
            TestContext.Current.CancellationToken);
        var engineerView = await queries.LoadAsync(
            fixture.Engineer,
            fixture.RequestId,
            TestContext.Current.CancellationToken);

        Assert.NotNull(requesterView);
        Assert.NotNull(engineerView);

        var requesterBody = await ApiPayloads.RenderAsync(
            RequestProjection.Detail(requesterView.Aggregate, requesterView.Visibility, queries.Now()));
        var engineerBody = await ApiPayloads.RenderAsync(
            RequestProjection.Detail(engineerView.Aggregate, engineerView.Visibility, queries.Now()));

        var requesterKeys = ApiPayloads.Keys(requesterBody);

        Assert.DoesNotContain("transcript", requesterKeys);
        Assert.DoesNotContain("changes", requesterKeys);
        Assert.DoesNotContain("details", requesterKeys);
        Assert.DoesNotContain(ApiFixture.CommitSha, requesterBody, StringComparison.Ordinal);

        var engineerKeys = ApiPayloads.Keys(engineerBody);
        Assert.Contains("transcript", engineerKeys);
        Assert.Contains("changes", engineerKeys);
    }

    [Fact]
    public async Task AProjectIsInvisibleUntilItsSmokeTestPassesAndSomebodyIsScopedToIt()
    {
        await using var fixture = await ApiFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var queries = fixture.Queries();

        var visible = await queries.ListProjectsAsync(fixture.Requester, TestContext.Current.CancellationToken);
        var project = Assert.Single(visible);

        // Section 7.1: the display name comes from the committed config, never `owner/repo`.
        Assert.Equal("Quote tool", project.Name);
        Assert.DoesNotContain("/", project.Name, StringComparison.Ordinal);
        Assert.Single(project.Templates);

        // Section 9: readiness is earned. Take it away and the project simply vanishes.
        var repo = await fixture.Db.Repos.SingleAsync(
            row => row.Id == fixture.RepoId,
            TestContext.Current.CancellationToken);
        repo.TransitionTo(RepoStatus.SmokeTest);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await queries.ListProjectsAsync(fixture.Requester, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheApprovalQueueOnlyListsWhatThisMemberMayApprove()
    {
        await using var fixture = await ApiFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        await fixture.PutSpecUpForApprovalAsync();
        var queries = fixture.Queries();

        // Section 7.5: the spend gate belongs to the approver role.
        Assert.Empty(await queries.ListPendingApprovalsAsync(fixture.Requester, TestContext.Current.CancellationToken));

        var waiting = await queries.ListPendingApprovalsAsync(
            fixture.Engineer,
            TestContext.Current.CancellationToken);

        var approval = Assert.Single(waiting);
        Assert.Equal("Remember the last selected vertical", approval.Title);
        Assert.Equal(3.4m, approval.EstimatedCostUsd);
    }

    [Fact]
    public async Task IntakeRefusesARepositoryNobodyScopedThisMemberTo()
    {
        await using var fixture = await ApiFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        await fixture.RemoveScopesAsync();

        var (outcome, _) = await fixture.Commands().CreateAsync(
            fixture.Requester,
            new CreateRequestBody { ProjectId = fixture.RepoId.ToString(), RawText = "please change the label" },
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Equal(403, outcome.Status);
    }

    [Fact]
    public async Task IntakeFilesARequestAndQueuesRefinementRatherThanABuild()
    {
        await using var fixture = await ApiFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var before = await fixture.Db.Jobs.CountAsync(TestContext.Current.CancellationToken);

        var (outcome, requestId) = await fixture.Commands().CreateAsync(
            fixture.Requester,
            new CreateRequestBody
            {
                ProjectId = fixture.RepoId.ToString(),
                RawText = "the submit button should say what it does",
            },
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);

        var filed = await fixture.Db.Requests.SingleAsync(
            row => row.Id == requestId,
            TestContext.Current.CancellationToken);

        // Section 10: nothing reaches an agent without passing through refinement first.
        Assert.Equal(RequestStatus.Refining, filed.Status);

        var after = await fixture.Db.Jobs.CountAsync(TestContext.Current.CancellationToken);
        Assert.Equal(before + 1, after);

        var job = await fixture.Db.Jobs
            .OrderByDescending(row => row.CreatedAt)
            .FirstAsync(TestContext.Current.CancellationToken);

        Assert.Equal(JobType.Refine, job.Type);
    }

    [Fact]
    public async Task ARequesterCannotApproveTheirOwnSpendGateWithoutTheApproverRole()
    {
        await using var fixture = await ApiFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        await fixture.PutSpecUpForApprovalAsync();

        var refused = await fixture.Commands().ApproveSpecAsync(
            fixture.Requester,
            fixture.RequestId,
            version: 2,
            TestContext.Current.CancellationToken);

        Assert.False(refused.Succeeded);
        Assert.Equal(403, refused.Status);
        Assert.Contains("approver role", refused.Reason, StringComparison.Ordinal);

        var allowed = await fixture.Commands().ApproveSpecAsync(
            fixture.Engineer,
            fixture.RequestId,
            version: 2,
            TestContext.Current.CancellationToken);

        Assert.True(allowed.Succeeded);

        var request = await fixture.Db.Requests.SingleAsync(
            row => row.Id == fixture.RequestId,
            TestContext.Current.CancellationToken);
        Assert.Equal(RequestStatus.Queued, request.Status);
    }

    [Fact]
    public async Task CancellingLatchesInPostgresRatherThanInMemory()
    {
        await using var fixture = await ApiFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        // Section 11: there has to be a runner to kill. A session that already produced a preview is
        // refused, and that refusal is the same rule the projection uses to hide the button.
        var settled = await fixture.Commands().CancelAsync(
            fixture.Requester,
            fixture.RequestId,
            TestContext.Current.CancellationToken);

        Assert.False(settled.Succeeded);
        Assert.Equal(409, settled.Status);

        await fixture.PutSessionBackToRunningAsync();

        var outcome = await fixture.Commands().CancelAsync(
            fixture.Requester,
            fixture.RequestId,
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);

        // Section 2.3: whoever picks the session up after a container restart has to be able to see
        // that it was cancelled, so the latch is a column.
        var session = await fixture.Db.Sessions.SingleAsync(
            row => row.Id == fixture.SessionId,
            TestContext.Current.CancellationToken);

        Assert.NotNull(session.CancelRequestedAt);
        Assert.Equal(SessionStatus.Cancelled, session.Status);
    }

    [Fact]
    public async Task TheListShowsTheRequestersOwnWorkWithNoEngineerFields()
    {
        await using var fixture = await ApiFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var summaries = await fixture.Queries().ListRequestsAsync(
            fixture.Requester,
            TestContext.Current.CancellationToken);

        var summary = Assert.Single(summaries);
        Assert.Equal("Remember the last selected vertical", summary.Title);
        Assert.Equal(ApiRequestStatus.PreviewReady, summary.Status);

        // Section 6: only NeedsInput and PreviewReady notify.
        Assert.True(summary.NeedsAttention);

        var body = await ApiPayloads.RenderAsync(summaries);
        var keys = ApiPayloads.Keys(body);
        Assert.DoesNotContain("costUsd", keys);
        Assert.DoesNotContain("commitSha", keys);

        Assert.Empty(await fixture.Queries().ListRequestsAsync(
            fixture.Outsider,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheViewersCapabilitiesComeFromThePoliciesRatherThanFromRoleArithmetic()
    {
        await using var fixture = await ApiFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var viewers = fixture.Viewers();

        var requester = await viewers.CapabilitiesAsync(fixture.Requester, TestContext.Current.CancellationToken);
        Assert.True(requester.CanFileRequests);
        Assert.False(requester.CanReadRepos);
        Assert.False(requester.CanApproveSpend);
        Assert.False(requester.CanAdminister);

        var engineer = await viewers.CapabilitiesAsync(fixture.Engineer, TestContext.Current.CancellationToken);
        Assert.True(engineer.CanReadRepos);
        Assert.True(engineer.CanApproveSpend);

        // Take the scope row away and the file button goes with it (section 7.3, deny by default).
        await fixture.RemoveScopesAsync();
        var unscoped = await viewers.CapabilitiesAsync(fixture.Requester, TestContext.Current.CancellationToken);
        Assert.False(unscoped.CanFileRequests);
    }

    [Fact]
    public async Task TheViewerPayloadNamesTheOrganisationAndTheRolesTheMemberHolds()
    {
        await using var fixture = await ApiFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var viewer = await fixture.Viewers().DescribeAsync(fixture.Requester, TestContext.Current.CancellationToken);
        Assert.NotNull(viewer);

        var body = await ApiPayloads.RenderAsync(viewer);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal("Ayesha Rahman", root.GetProperty("displayName").GetString());
        Assert.Equal("Northbeam Solar", root.GetProperty("organization").GetProperty("name").GetString());
        Assert.Equal("requester", root.GetProperty("roles")[0].GetString());
        Assert.Equal("skip_the_basics", root.GetProperty("preferences").GetProperty("teachingLevel").GetString());

        // Section 30.4: absent until the three screens are done, never a null.
        Assert.False(root.TryGetProperty("requesterOnboardingCompletedAt", out _));
    }

    /// <summary>
    /// The whole scenario, written to Postgres inside a transaction that is always rolled back.
    /// </summary>
    /// <remarks>
    /// The rollback is not tidiness. The job queue is a shared table and its own integration suite
    /// claims untagged jobs, so a row this fixture leaves behind would be claimed by a test in another
    /// class and fail it. Holding everything in an uncommitted transaction keeps these rows invisible
    /// to every other connection for as long as they exist.
    /// </remarks>
    private sealed class ApiFixture : IAsyncDisposable
    {
        public const string CommitSha = "a3f9c21deadbeef";

        private readonly ApiScenario scenario;
        private readonly Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction;

        private ApiFixture(
            CharterDbContext db,
            ApiScenario scenario,
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction)
        {
            Db = db;
            this.scenario = scenario;
            this.transaction = transaction;
        }

        public CharterDbContext Db { get; }

        public Guid RepoId => scenario.Repo.Id;

        public Guid RequestId => scenario.Request.Id;

        public Guid SessionId => scenario.Session.Id;

        public MemberSnapshot Requester => scenario.Requester;

        public MemberSnapshot Engineer => scenario.Engineer;

        public MemberSnapshot Outsider => scenario.Outsider;

        public RequestQueryService Queries()
            => new(Db, new CharterAuthorizationService(Db, new AuditWriter(Db, TimeProvider.System)), TimeProvider.System);

        public Charter.Api.Viewer.ViewerService Viewers()
            => new(Db, new Charter.Api.Viewer.UserRecordPreferencesStore(Db), Queries());

        public RequestCommandService Commands()
            => new(
                Db,
                new CharterAuthorizationService(Db, new AuditWriter(Db, TimeProvider.System)),
                Queries(),
                new SilentPublisher(),
                new JobQueue(Db),
                TimeProvider.System);

        public async Task PutSpecUpForApprovalAsync()
        {
            var request = await Db.Requests.SingleAsync(row => row.Id == RequestId, TestContext.Current.CancellationToken);
            request.TransitionTo(RequestStatus.SpecReady);

            // Undo the fixture's approval so the spend gate is genuinely open.
            await Db.Specs
                .Where(row => row.RequestId == RequestId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(row => row.ApprovedAt, (DateTimeOffset?)null)
                        .SetProperty(row => row.ApprovedBy, (Guid?)null),
                    TestContext.Current.CancellationToken);

            await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
            Db.ChangeTracker.Clear();
        }

        /// <summary>Puts the fixture's session back mid-flight, so there is something to cancel.</summary>
        public async Task PutSessionBackToRunningAsync()
        {
            var session = await Db.Sessions.SingleAsync(
                row => row.Id == SessionId,
                TestContext.Current.CancellationToken);

            session.TransitionTo(SessionStatus.Running);
            await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
            Db.ChangeTracker.Clear();
        }

        public async Task RemoveScopesAsync()
        {
            await Db.RepoScopes
                .Where(row => row.RepoId == RepoId)
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);

            Db.ChangeTracker.Clear();
        }

        public static async Task<ApiFixture?> CreateAsync()
        {
            var url = Environment.GetEnvironmentVariable(DatabaseUrlVariable);
            if (string.IsNullOrWhiteSpace(url))
            {
                Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the API integration tests.");
                return null;
            }

            var options = new DbContextOptionsBuilder<CharterDbContext>();
            DataServiceCollectionExtensions.ConfigureNpgsql(options, DatabaseUrl.ToNpgsql(url));

            var db = new CharterDbContext(options.Options);
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

            var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            var scenario = ApiScenario.Build();

            db.Organizations.Add(scenario.Organization);
            db.Users.Add(scenario.RequesterUser);
            db.Users.Add(scenario.EngineerUser);
            db.Members.Add(scenario.RequesterMember);
            db.Members.Add(scenario.EngineerMember);
            db.Repos.Add(scenario.Repo);
            db.RepoScopes.AddRange(scenario.Scopes);
            db.Requests.Add(scenario.Request);
            db.Specs.Add(scenario.Spec);
            db.Sessions.Add(scenario.Session);
            db.Events.AddRange(scenario.Events);
            db.Milestones.AddRange(scenario.Milestones);
            db.VerificationArtifacts.AddRange(scenario.Artifacts);
            db.PullRequests.Add(scenario.PullRequest);

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.ChangeTracker.Clear();

            return new ApiFixture(db, scenario, transaction);
        }

        public async ValueTask DisposeAsync()
        {
            await transaction.RollbackAsync();
            await transaction.DisposeAsync();
            await Db.DisposeAsync();
        }

        private sealed class SilentPublisher : IRequestStreamPublisher
        {
            public Task PublishAsync(
                Guid requestId,
                RequestStreamEvent frame,
                CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task PublishAsync(
                Guid requestId,
                RequestStreamEvent requesterFrame,
                RequestStreamEvent engineerFrame,
                CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }
}
