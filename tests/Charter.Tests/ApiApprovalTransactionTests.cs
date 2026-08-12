using System.Text.Json;
using Charter.Api.Contracts;
using Charter.Api.Requests;
using Charter.Auth.Audit;
using Charter.Auth.Authorization;
using Charter.Data;
using Charter.Domain;
using Charter.Hubs;
using Charter.Orchestration;
using Charter.VersionControl;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Charter.Tests;

/// <summary>
/// The spend gate writes one transaction, not two (sections 2.3, 7.5).
/// </summary>
/// <remarks>
/// <para>
/// Approval used to save the request's new status and then enqueue the build job separately. Section
/// 2.3 is explicit that the container dies in exactly these windows, and this one leaves the worst
/// residue it could: an approved specification, a thread that says <em>building this now</em>, and
/// nothing anywhere that will dispatch it.
/// </para>
/// <para>
/// <see cref="SessionOrchestrator.RecoverApprovedSpecsAsync"/> sweeps for that and still does — belt
/// and braces is right for section 2.3 — but a recovery sweep is defence, not the fix. <c>JobQueue</c>
/// shares the caller's <c>CharterDbContext</c>, so the job belongs in the same change tracker as the
/// transition and one <c>SaveChangesAsync</c> commits both or neither.
/// </para>
/// </remarks>
public class ApiApprovalTransactionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ApprovalSurvivesAContainerThatDiesTheInstantAfterItsFirstWrite()
    {
        await using var world = await ApprovalWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        // The crash, injected exactly where the gap used to be: the second save this call attempts
        // never lands. With two transactions that killed the build job and left the request claiming
        // to be building. With one there is no second save to kill.
        world.Crash.Arm(allow: 1);

        var approved = await world.Commands().ApproveSpecAsync(world.Approver, world.RequestId, version: 1, Token);

        Assert.True(approved.Succeeded);
        Assert.Equal(1, world.Crash.Saves);

        Assert.Equal(RequestStatus.Queued, await world.RequestStatusAsync());
        Assert.True(await world.SpecApprovedAsync());

        var job = Assert.Single(await world.BuildJobsAsync());
        var payload = SpecBuildPayload.TryParse(job.Payload);

        Assert.NotNull(payload);
        Assert.Equal(world.SpecId, payload.SpecId);
        Assert.Equal(world.RequestId, payload.RequestId);
    }

    [Fact]
    public async Task NeitherHalfLandsWhenTheTransactionIsRolledBack()
    {
        await using var world = await ApprovalWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        // The other side of the same claim. One transaction means the two writes cannot be separated
        // in either direction: rolling back must take the job with the approval, not leave an orphan.
        await using (var transaction = await world.Db.Database.BeginTransactionAsync(Token))
        {
            Assert.True((await world.Commands().ApproveSpecAsync(
                world.Approver,
                world.RequestId,
                version: 1,
                Token)).Succeeded);

            await transaction.RollbackAsync(Token);
        }

        world.Db.ChangeTracker.Clear();

        Assert.Equal(RequestStatus.SpecReady, await world.RequestStatusAsync());
        Assert.False(await world.SpecApprovedAsync());
        Assert.Empty(await world.BuildJobsAsync());
    }

    [Fact]
    public async Task TheRecoverySweepStaysAndFindsNothingToDo()
    {
        await using var world = await ApprovalWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await world.Commands().ApproveSpecAsync(world.Approver, world.RequestId, version: 1, Token);

        // Belt and braces (section 2.3): the sweep is not deleted, and after a clean approval it has
        // nothing to re-queue — the job the API wrote already names this specification, so a second
        // one would be a duplicate dispatch dressed as recovery.
        var openJobs = new HashSet<Guid> { SpecBuildPayload.SessionIdFor(world.SpecId) };
        var recovered = await world.Orchestrator().RecoverApprovedSpecsAsync(
            world.Db,
            new JobQueue(world.Db),
            openJobs,
            Token);

        Assert.Equal(0, recovered);
        Assert.Single(await world.BuildJobsAsync());
    }

    [Fact]
    public async Task TheWorksVerdictNamesTheSessionItIsRecapping()
    {
        await using var world = await ApprovalWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var sessionId = await world.DispatchAsync();

        var verdict = await world.Commands().SubmitFeedbackAsync(
            world.Requester,
            world.RequestId,
            new SubmitFeedbackBody { Verdict = ApiFeedbackVerdict.Works },
            Token);

        Assert.True(verdict.Succeeded);

        var recap = Assert.Single(await world.JobsOfAsync(JobType.Recap));
        var payload = RecapJobPayload.TryParse(recap.Payload);

        // The session is in hand at the call site — `view.Aggregate.Session?.Id` — and it was being
        // withheld, so the handler resolved it back from the request and the specification on every
        // recap. The lookup is not wrong; asking for it is.
        Assert.NotNull(payload);
        Assert.Equal(sessionId, payload.SessionId);
    }

    [Fact]
    public async Task TheNotQuiteVerdictStillNamesTheSpecificationRatherThanTheSessionItRejected()
    {
        await using var world = await ApprovalWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await world.DispatchAsync();

        Assert.True((await world.Commands().SubmitFeedbackAsync(
            world.Requester,
            world.RequestId,
            new SubmitFeedbackBody { Verdict = ApiFeedbackVerdict.NotQuite, Note = "the wrong vertical" },
            Token)).Succeeded);

        var rebuild = Assert.Single(
            await world.JobsOfAsync(JobType.Build),
            job => SpecBuildPayload.TryParse(job.Payload)?.IsRebuild == true);

        // Section 11: "not quite" becomes a *new* session on the same spec. A payload naming the
        // rejected session would have the dispatcher resume the very run the requester turned down.
        var payload = SpecBuildPayload.TryParse(rebuild.Payload);

        Assert.NotNull(payload);
        Assert.Equal(world.SpecId, payload.SpecId);
        Assert.Null(BuildJobPayload.TryParse(rebuild.Payload));
    }
}

/// <summary>One approved-shaped request in a throwaway schema, with the crash injector attached.</summary>
internal sealed class ApprovalWorld : IAsyncDisposable
{
    private readonly TestSchema _schema;

    private ApprovalWorld(TestSchema schema, CharterDbContext db, ApprovalSeed seed, CrashAfterSavesInterceptor crash)
    {
        _schema = schema;
        Db = db;
        Seed = seed;
        Crash = crash;
    }

    public CharterDbContext Db { get; }

    public ApprovalSeed Seed { get; }

    public CrashAfterSavesInterceptor Crash { get; }

    public Guid RequestId => Seed.RequestId;

    public Guid SpecId => Seed.SpecId;

    public MemberSnapshot Requester => MemberSnapshot.From(Seed.RequesterMember);

    public MemberSnapshot Approver => MemberSnapshot.From(Seed.ApproverMember);

    private OrchestrationOptions Options { get; } = new()
    {
        BaseUrl = new Uri("https://charter.example.test/"),
        WorkerId = $"approval-{Guid.NewGuid():N}",
    };

    public RequestQueryService Queries()
        => new(
            Db,
            new CharterAuthorizationService(Db, new AuditWriter(Db, CharterTime.System)),
            new VersionControlProviderRegistry([]),
            CharterTime.System);

    public RequestCommandService Commands()
        => new(
            Db,
            new CharterAuthorizationService(Db, new AuditWriter(Db, CharterTime.System)),
            Queries(),
            new NoStreamPublisher(),
            new JobQueue(Db),
            CharterTime.System);

    public SessionOrchestrator Orchestrator()
        => new(
            new NoScopes(),
            Options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionOrchestrator>.Instance);

    public static async Task<ApprovalWorld?> CreateAsync()
    {
        var schema = await TestSchema.CreateAsync(TestContext.Current.CancellationToken);
        if (schema is null)
        {
            return null;
        }

        var token = TestContext.Current.CancellationToken;
        var crash = new CrashAfterSavesInterceptor();
        var db = schema.NewContext(crash);
        var now = DateTimeOffset.UtcNow.AddMinutes(-10);
        var tag = Guid.CreateVersion7().ToString("N");

        var organization = Organization.Create("Northbeam Solar", OrganizationMode.Organization, now);
        var requesterUser = User.Create($"ayesha+{tag}@example.test", "Ayesha Rahman", TeachingLevel.SkipTheBasics, now);
        var approverUser = User.Create($"tomas+{tag}@example.test", "Tomas Beck", TeachingLevel.JustTheDecisions, now);

        var requester = Member.Create(organization.Id, requesterUser.Id, [MemberRole.Requester], now: now);
        var approver = Member.Create(
            organization.Id,
            approverUser.Id,
            [MemberRole.Approver, MemberRole.Engineer],
            now: now);

        var repo = Repo.Connect(organization.Id, 42, "northbeam/quote-tool", "main", now);
        repo.TransitionTo(RepoStatus.Ready, now);

        var request = Request.File(
            organization.Id,
            repo.Id,
            requesterUser.Id,
            "every time i start a new quote it makes me pick solar again",
            null,
            now);

        request.TransitionTo(RequestStatus.SpecReady, now);

        var spec = Spec.Draft(
            request.Id,
            1,
            "Remember the last selected vertical",
            "When you start a new quote, the vertical you chose last time is already selected.",
            "## Approach\nPersist the selection.",
            JsonSerializer.Serialize(new[] { "Starting a new quote pre-selects the vertical you chose last." }),
            now: now);

        db.Organizations.Add(organization);
        db.Users.Add(requesterUser);
        db.Users.Add(approverUser);
        db.Members.Add(requester);
        db.Members.Add(approver);
        db.Repos.Add(repo);
        db.RepoScopes.Add(RepoScope.ForRole(repo.Id, MemberRole.Requester, canRequest: true, now));
        db.RepoScopes.Add(RepoScope.ForRole(repo.Id, MemberRole.Engineer, canRequest: true, now));
        db.Requests.Add(request);
        db.Specs.Add(spec);

        await db.SaveChangesAsync(token);
        db.ChangeTracker.Clear();

        crash.Reset();

        return new ApprovalWorld(
            schema,
            db,
            new ApprovalSeed(request.Id, spec.Id, requester, approver),
            crash);
    }

    /// <summary>Approves and materialises the session, the way the queue does.</summary>
    public async Task<Guid> DispatchAsync()
    {
        var token = TestContext.Current.CancellationToken;

        await Commands().ApproveSpecAsync(Approver, RequestId, version: 1, token);

        var session = Session.Queue(
            SpecId,
            RunnerKind.Agent,
            "anthropic/claude-opus-5",
            id: SpecBuildPayload.SessionIdFor(SpecId));

        session.Start("a3f9c21deadbeef0000000000000000000000cafe");
        session.TransitionTo(SessionStatus.PreviewReady);

        Db.Sessions.Add(session);

        var request = await Db.Requests.SingleAsync(row => row.Id == RequestId, token);
        request.TransitionTo(RequestStatus.PreviewReady);

        await Db.SaveChangesAsync(token);
        Db.ChangeTracker.Clear();

        return session.Id;
    }

    public async Task<RequestStatus> RequestStatusAsync()
    {
        Db.ChangeTracker.Clear();

        return await Db.Requests
            .AsNoTracking()
            .Where(row => row.Id == RequestId)
            .Select(row => row.Status)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    public async Task<bool> SpecApprovedAsync()
    {
        Db.ChangeTracker.Clear();

        return await Db.Specs
            .AsNoTracking()
            .Where(row => row.Id == SpecId)
            .Select(row => row.ApprovedAt != null)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    public Task<List<Job>> BuildJobsAsync() => JobsOfAsync(JobType.Build);

    public async Task<List<Job>> JobsOfAsync(JobType type)
        => await Db.Jobs
            .AsNoTracking()
            .Where(row => row.Type == type)
            .ToListAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await _schema.DisposeAsync();
    }

    /// <summary>The rows the spend gate acts on.</summary>
    internal sealed record ApprovalSeed(Guid RequestId, Guid SpecId, Member RequesterMember, Member ApproverMember);

    /// <summary>SignalR is a courtesy on top of the rows, never the record (section 2.3).</summary>
    private sealed class NoStreamPublisher : IRequestStreamPublisher
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

    /// <summary>The orchestrator's sweep is called directly here, so it needs no scopes of its own.</summary>
    private sealed class NoScopes : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory
    {
        public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope()
            => throw new NotSupportedException("RecoverApprovedSpecsAsync takes its own context.");
    }
}

/// <summary>
/// Fails the <em>n</em>th <c>SaveChangesAsync</c> onwards, which is what a killed container looks
/// like from inside a service that expected to write twice.
/// </summary>
internal sealed class CrashAfterSavesInterceptor : SaveChangesInterceptor
{
    /// <summary>How many saves have been attempted since the last <see cref="Reset"/>.</summary>
    public int Saves { get; private set; }

    /// <summary>Saves to allow. Null lets everything through.</summary>
    public int? After { get; set; }

    public void Reset()
    {
        Saves = 0;
        After = null;
    }

    /// <summary>Allows <paramref name="allow"/> more saves, then kills the process.</summary>
    public void Arm(int allow)
    {
        Saves = 0;
        After = allow;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Saves++;

        if (After is { } allowed && Saves > allowed)
        {
            throw new DbUpdateException("The container went away between the two writes.");
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
