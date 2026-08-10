using System.Text.Json;
using Charter.Api.Contracts;
using Charter.Api.Requests;
using Charter.Auth.Audit;
using Charter.Auth.Authorization;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Charter.Hubs;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Charter.Tests;

/// <summary>
/// Section 7.5's four post-hoc actions, and the one of them that must not be undoable.
/// </summary>
/// <remarks>
/// <em>"An agent and a human editing the same branch concurrently is the one genuinely destructive
/// failure mode in this design."</em> So the take-over tests below are not about the button — they are
/// about whether anything downstream can still write to that branch afterwards, which is why they run
/// against a real database and inspect the session row and the job queue rather than the response.
/// </remarks>
public class ApiSessionActionTests
{
    private const string DatabaseUrlVariable = "CHARTER_TEST_DATABASE_URL";

    [Fact]
    public void TheWholePanelIsAbsentForAViewerWhoMayPerformNoneOfIt()
    {
        // Section 7.4: absent rather than present-and-empty, so the panel does not render disabled.
        var scenario = ApiScenario.Build();

        Assert.Null(SessionActionsProjection.Panel(
            scenario.Session,
            scenario.ChangeRequest,
            scenario.VisibilityFor(scenario.Requester),
            handedOffByName: null));

        Assert.NotNull(SessionActionsProjection.Panel(
            scenario.Session,
            scenario.ChangeRequest,
            scenario.VisibilityFor(scenario.Engineer),
            handedOffByName: null));
    }

    [Fact]
    public void ThePanelIsAbsentWhenNoBranchCanBeNamed()
    {
        // "Stops writes to that branch" is only meaningful if the reader can see which branch, so a
        // session with no known ref offers nothing rather than an unnamed confirmation.
        var scenario = ApiScenario.Build();

        Assert.Null(SessionActionsProjection.Panel(
            scenario.Session,
            changeRequest: null,
            scenario.VisibilityFor(scenario.Engineer),
            handedOffByName: null));
    }

    [Fact]
    public void SteerAndReviseAreWithdrawnForGoodOnceSomebodyHasTakenOver()
    {
        var scenario = ApiScenario.Build();
        scenario.Session.HandOff(DateTimeOffset.UtcNow);

        var panel = SessionActionsProjection.Panel(
            scenario.Session,
            scenario.ChangeRequest,
            scenario.VisibilityFor(scenario.Engineer),
            handedOffByName: "Tomas Beck");

        Assert.NotNull(panel);
        Assert.False(panel.CanSteer);
        Assert.False(panel.CanRevise);
        Assert.False(panel.CanTakeOver);
        Assert.False(panel.CanApprove);

        Assert.NotNull(panel.HandedOff);
        Assert.Equal("Tomas Beck", panel.HandedOff.ByName);
    }

    [Fact]
    public async Task TheBranchIsNamedAndTheHandOffBlockIsAbsentUntilItHappens()
    {
        var scenario = ApiScenario.Build();

        var body = await ApiPayloads.RenderAsync(SessionActionsProjection.Panel(
            scenario.Session,
            scenario.ChangeRequest,
            scenario.VisibilityFor(scenario.Engineer),
            handedOffByName: null));

        using var document = JsonDocument.Parse(body);

        Assert.Equal(ApiScenario.HeadBranch, document.RootElement.GetProperty("branch").GetString());
        Assert.False(document.RootElement.TryGetProperty("handedOff", out _));
    }

    [Fact]
    public async Task TakingOverMarksTheSessionHandedOffAndCancelsQueuedWork()
    {
        await using var fixture = await SessionActionFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        // A build job sitting in the queue is the case that matters: the button is pressed a second
        // before the dispatcher would have picked it up.
        var queued = await fixture.QueueBuildAsync();

        var outcome = await fixture.Commands().TakeOverSessionAsync(
            fixture.Engineer,
            fixture.RequestId,
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);

        var session = await fixture.SessionAsync();
        Assert.Equal(SessionStatus.HandedOff, session.Status);
        Assert.True(session.IsTerminal);

        // The cancel latch too, so a runner already holding the session stops rather than finishing
        // its current write.
        Assert.NotNull(session.CancelRequestedAt);

        var job = await fixture.JobAsync(queued);
        Assert.Equal(JobStatus.Cancelled, job.Status);
    }

    [Fact]
    public async Task ATakenOverSessionRefusesEverySubsequentAgentWrite()
    {
        await using var fixture = await SessionActionFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        await fixture.Commands().TakeOverSessionAsync(
            fixture.Engineer,
            fixture.RequestId,
            TestContext.Current.CancellationToken);

        // Not a flag a later dispatch can ignore: each of the three commands that would write to the
        // branch refuses, and refuses with a sentence rather than a stack trace.
        var steer = await fixture.Commands().SteerSessionAsync(
            fixture.Engineer,
            fixture.RequestId,
            new SteerSessionBody { Instruction = "carry on" },
            TestContext.Current.CancellationToken);

        var revise = await fixture.Commands().ReviseSessionAsync(
            fixture.Engineer,
            fixture.RequestId,
            new ReviseSessionBody { RevisedSpecMd = "# A different plan" },
            TestContext.Current.CancellationToken);

        var again = await fixture.Commands().TakeOverSessionAsync(
            fixture.Engineer,
            fixture.RequestId,
            TestContext.Current.CancellationToken);

        foreach (var refused in new[] { steer, revise, again })
        {
            Assert.False(refused.Succeeded);
            Assert.Equal(StatusCodes.Status409Conflict, refused.Status);
            Assert.Contains("taken this branch over", refused.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TakingOverIsAttributableToANamedHumanAndReadsBackAsOne()
    {
        await using var fixture = await SessionActionFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        await fixture.Commands().TakeOverSessionAsync(
            fixture.Engineer,
            fixture.RequestId,
            TestContext.Current.CancellationToken);

        // Section 7.3, guardrail 5. The session row records that a hand-off happened; the audit row
        // is what names who, and the panel reads it back rather than inventing "an engineer".
        var view = await fixture.Queries().LoadAsync(
            fixture.Engineer,
            fixture.RequestId,
            TestContext.Current.CancellationToken);

        Assert.NotNull(view);

        var detail = RequestProjection.Detail(view.Aggregate, view.Visibility, DateTimeOffset.UtcNow);

        Assert.NotNull(detail.SessionActions?.HandedOff);
        Assert.Equal("Tomas Beck", detail.SessionActions.HandedOff.ByName);
    }

    [Fact]
    public async Task ARequesterCannotSteerTheirOwnSessionEvenThoughTheyFiledIt()
    {
        await using var fixture = await SessionActionFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        // The panel is absent for them, and the command refuses regardless of what the client drew.
        var outcome = await fixture.Commands().SteerSessionAsync(
            fixture.Requester,
            fixture.RequestId,
            new SteerSessionBody { Instruction = "make it blue" },
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Equal(StatusCodes.Status403Forbidden, outcome.Status);
    }

    [Fact]
    public async Task SteeringWithNothingToSayIsRefusedBeforeAnythingIsSpent()
    {
        await using var fixture = await SessionActionFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var outcome = await fixture.Commands().SteerSessionAsync(
            fixture.Engineer,
            fixture.RequestId,
            new SteerSessionBody { Instruction = "   " },
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Equal(StatusCodes.Status400BadRequest, outcome.Status);
    }

    [Fact]
    public async Task RevisingForksTheSpecRatherThanEditingTheApprovedOne()
    {
        await using var fixture = await SessionActionFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var outcome = await fixture.Commands().ReviseSessionAsync(
            fixture.Engineer,
            fixture.RequestId,
            new ReviseSessionBody { RevisedSpecMd = "# Remember the vertical\n\nDo it per person." },
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);

        var specs = await fixture.SpecsAsync();

        // Two versions, and the one somebody approved is still readable next to the one that was
        // built. Section 10b's acceptance criteria come across untouched: an engineer editing the
        // markdown is editing the instruction, not re-authoring the contract.
        Assert.Equal(2, specs.Count);
        Assert.Equal(specs[0].AcceptanceCriteria, specs[1].AcceptanceCriteria);
        Assert.Contains("Do it per person.", specs[^1].BodyMd, StringComparison.Ordinal);
        Assert.True(specs[^1].IsApproved);
    }

    private sealed class SessionActionFixture : IAsyncDisposable
    {
        private readonly CharterDbContext db;
        private readonly ApiScenario scenario;
        private readonly Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction;

        private SessionActionFixture(
            CharterDbContext db,
            ApiScenario scenario,
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction)
        {
            this.db = db;
            this.scenario = scenario;
            this.transaction = transaction;
        }

        public Guid RequestId => scenario.Request.Id;

        public MemberSnapshot Requester => scenario.Requester;

        public MemberSnapshot Engineer => scenario.Engineer;

        public RequestQueryService Queries()
            => new(
                db,
                new CharterAuthorizationService(db, new AuditWriter(db, TimeProvider.System)),
                new Charter.VersionControl.VersionControlProviderRegistry([]),
                TimeProvider.System);

        public RequestCommandService Commands()
            => new(
                db,
                new CharterAuthorizationService(db, new AuditWriter(db, TimeProvider.System)),
                Queries(),
                new SilentPublisher(),
                new JobQueue(db),
                TimeProvider.System);

        public async Task<Guid> QueueBuildAsync()
        {
            var job = await new JobQueue(db).EnqueueAsync(
                JobType.Build,
                $$"""{"session_id":"{{scenario.Session.Id:D}}"}""",
                cancellationToken: TestContext.Current.CancellationToken);

            db.ChangeTracker.Clear();
            return job.Id;
        }

        public async Task<Session> SessionAsync()
        {
            db.ChangeTracker.Clear();
            return await db.Sessions.AsNoTracking().SingleAsync(
                row => row.Id == scenario.Session.Id,
                TestContext.Current.CancellationToken);
        }

        public async Task<Job> JobAsync(Guid jobId)
        {
            db.ChangeTracker.Clear();
            return await db.Jobs.AsNoTracking().SingleAsync(
                row => row.Id == jobId,
                TestContext.Current.CancellationToken);
        }

        public async Task<IReadOnlyList<Spec>> SpecsAsync()
        {
            db.ChangeTracker.Clear();
            return await db.Specs
                .AsNoTracking()
                .Where(row => row.RequestId == RequestId)
                .OrderBy(row => row.Version)
                .ToListAsync(TestContext.Current.CancellationToken);
        }

        public static async Task<SessionActionFixture?> CreateAsync()
        {
            var url = Environment.GetEnvironmentVariable(DatabaseUrlVariable);
            if (string.IsNullOrWhiteSpace(url))
            {
                Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the session action tests.");
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
            db.ChangeRequests.Add(scenario.ChangeRequest);

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.ChangeTracker.Clear();

            return new SessionActionFixture(db, scenario, transaction);
        }

        public async ValueTask DisposeAsync()
        {
            await transaction.RollbackAsync();
            await transaction.DisposeAsync();
            await db.DisposeAsync();
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
