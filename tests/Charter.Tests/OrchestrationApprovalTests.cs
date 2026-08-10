using Charter.Data;
using Charter.Domain;
using Charter.Orchestration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Charter.Tests;

/// <summary>
/// The step between the spend gate and the execution plane: an approved specification becoming a
/// session (sections 7.5, 23, 2.3).
/// </summary>
/// <remarks>
/// <para>
/// The approval writes a build job naming a specification; the dispatcher turns it into a session and
/// hands it to a backend. Everything here is about the seam between those two facts, because that is
/// where a container restart lands — <em>the control plane can restart between them</em>, and the
/// three outcomes it must never produce are a lost session, a doubled one, and a stranded one.
/// </para>
/// <para>
/// One class, so xUnit runs them serially: the orchestrator's recovery sweep reads every request in
/// its database, and two of these in parallel would recover each other's rows.
/// </para>
/// </remarks>
public class OrchestrationApprovalTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly TimeSpan ExpiredLease = TimeSpan.FromMilliseconds(1);

    [Fact]
    public async Task AnApprovedSpecificationBecomesExactlyOneSessionAndIsDispatched()
    {
        await using var database = await OrchestrationDatabase.CreateAsync(Token);
        if (database is null)
        {
            return;
        }

        await using var instance = ControlPlaneInstance.Create(database);

        var approved = await instance.SeedApprovedSpecAsync(Token);
        await instance.EnqueueSpecBuildAsync(approved, Token);

        Assert.Empty(await instance.SessionsAsync(Token));

        Assert.True(await instance.Dispatcher.TryBecomeLeaderAsync(Token));
        Assert.Equal(1, await instance.Dispatcher.DispatchOnceAsync(Token));

        var session = Assert.Single(await instance.SessionsAsync(Token));

        Assert.Equal(approved.SpecId, session.SpecId);
        Assert.Equal(SessionStatus.Running, session.Status);

        // Section 7.5: a human approved this specification, so nothing about it is unreviewed.
        Assert.False(session.AutoDispatched);

        Assert.Single(instance.Runner.Dispatches, dispatch => dispatch.SessionId == session.Id);
    }

    [Fact]
    public async Task TheSessionIdIsAPureFunctionOfTheSpecificationSoARestartCannotMintASecondOne()
    {
        await using var database = await OrchestrationDatabase.CreateAsync(Token);
        if (database is null)
        {
            return;
        }

        // The backend outlives both control planes. It is the only honest count of how many times the
        // work was really started.
        var runner = new RecordingRunner();
        SpendGateSeed approved;
        Guid firstSession;

        // ---- Instance A: creates the session from the approved specification and is killed before ---
        // it dispatches. The job it was working from is still in the queue, so the work is not lost.
        var first = ControlPlaneInstance.Create(database, runner, lease: ExpiredLease);
        try
        {
            approved = await first.SeedApprovedSpecAsync(Token);
            await first.EnqueueSpecBuildAsync(approved, Token);

            firstSession = await first.InScopeAsync(async provider =>
            {
                var coordinator = provider.GetRequiredService<SessionCoordinator>();

                var materialization = await coordinator.EnsureSessionAsync(
                    new SpecBuildPayload { RequestId = approved.RequestId, SpecId = approved.SpecId },
                    Token);

                Assert.True(materialization.Created);
                return materialization.Session!.Id;
            });
        }
        finally
        {
            await first.KillAsync();
        }

        // ---- Instance B: a brand new process, sharing nothing but rows. ---------------------------
        await using var second = ControlPlaneInstance.Create(database, runner, lease: TimeSpan.FromMinutes(5));

        Assert.True(await second.Dispatcher.TryBecomeLeaderAsync(Token));
        Assert.Equal(1, await second.Dispatcher.DispatchOnceAsync(Token));

        var session = Assert.Single(await second.SessionsAsync(Token));

        Assert.Equal(firstSession, session.Id);
        Assert.Equal(SpecBuildPayload.SessionIdFor(approved.SpecId), session.Id);
        Assert.Single(runner.Dispatches, dispatch => dispatch.SessionId == session.Id);
    }

    [Fact]
    public async Task RunningTheSameBuildJobTwiceDispatchesOnce()
    {
        await using var database = await OrchestrationDatabase.CreateAsync(Token);
        if (database is null)
        {
            return;
        }

        await using var instance = ControlPlaneInstance.Create(database);

        var approved = await instance.SeedApprovedSpecAsync(Token);

        // Two identical jobs is what a crash between "enqueue" and "complete the claim" leaves, and
        // what the dispatcher's own deferral path writes on purpose.
        await instance.EnqueueSpecBuildAsync(approved, Token);
        await instance.EnqueueSpecBuildAsync(approved, Token);

        Assert.True(await instance.Dispatcher.TryBecomeLeaderAsync(Token));
        Assert.Equal(2, await instance.Dispatcher.DispatchOnceAsync(Token));

        Assert.Single(await instance.SessionsAsync(Token));
        Assert.Single(instance.Runner.Dispatches);
    }

    [Fact]
    public async Task ARestartBetweenApprovalAndTheBuildJobLeavesTheRequestRecoverable()
    {
        await using var database = await OrchestrationDatabase.CreateAsync(Token);
        if (database is null)
        {
            return;
        }

        var runner = new RecordingRunner();
        SpendGateSeed approved;

        // ---- Instance A: the approval commits, and the process dies before the job is written. ----
        // Section 2.3's worst case for this seam: one of the two writes landed and the other did not.
        var first = ControlPlaneInstance.Create(database, runner);
        try
        {
            approved = await first.SeedApprovedSpecAsync(Token);
            Assert.Empty(await first.JobsAsync(Token));
        }
        finally
        {
            await first.KillAsync();
        }

        await using var second = ControlPlaneInstance.Create(database, runner);

        // The recovery sweep is not a special startup mode; it is the steady state.
        await second.Orchestrator.ReconcileAsync(startup: true, Token);

        var job = Assert.Single(await second.JobsAsync(Token));

        Assert.Equal(JobType.Build, job.Type);
        Assert.Equal(approved.SpecId, SpecBuildPayload.TryParse(job.Payload)!.SpecId);

        Assert.True(await second.Dispatcher.TryBecomeLeaderAsync(Token));
        Assert.Equal(1, await second.Dispatcher.DispatchOnceAsync(Token));

        var session = Assert.Single(await second.SessionsAsync(Token));
        Assert.Single(runner.Dispatches, dispatch => dispatch.SessionId == session.Id);

        // And it does not keep re-queueing what it has already recovered.
        await second.Orchestrator.ReconcileAsync(startup: false, Token);
        Assert.Single(await second.JobsAsync(Token), row => row.Type == JobType.Build);
    }

    [Fact]
    public async Task AnUnapprovedSpecificationWaitsForAHumanRatherThanBuilding()
    {
        await using var database = await OrchestrationDatabase.CreateAsync(Token);
        if (database is null)
        {
            return;
        }

        await using var instance = ControlPlaneInstance.Create(database);

        // Nobody approved it, and no auto-dispatch policy covers anybody. Section 7.5: nothing
        // applying is a refusal, not a permission.
        var pending = await instance.SeedApprovedSpecAsync(Token, approved: false);
        await instance.EnqueueSpecBuildAsync(pending, Token);

        Assert.True(await instance.Dispatcher.TryBecomeLeaderAsync(Token));
        Assert.Equal(1, await instance.Dispatcher.DispatchOnceAsync(Token));

        Assert.Empty(await instance.SessionsAsync(Token));
        Assert.Empty(instance.Runner.Dispatches);

        // Deferred, never failed: an approver pressing the button is what unblocks it, and a failed
        // job would have burned its attempts before they got round to it.
        var jobs = await instance.JobsAsync(Token);
        Assert.Contains(jobs, job => job.Type == JobType.Build && job.Status == JobStatus.Pending);
    }

    [Fact]
    public async Task AnAutoDispatchedSpecificationBuildsWithoutAnApproverAndSaysSo()
    {
        await using var database = await OrchestrationDatabase.CreateAsync(Token);
        if (database is null)
        {
            return;
        }

        await using var instance = ControlPlaneInstance.Create(database);

        // The same unapproved specification, with an organisation-default policy behind it — which is
        // exactly how section 7.2 says personal mode reaches "everything auto-dispatches".
        var pending = await instance.SeedApprovedSpecAsync(Token, approved: false, autoDispatchPolicy: true);
        await instance.EnqueueSpecBuildAsync(pending, Token);

        Assert.True(await instance.Dispatcher.TryBecomeLeaderAsync(Token));
        Assert.Equal(1, await instance.Dispatcher.DispatchOnceAsync(Token));

        var session = Assert.Single(await instance.SessionsAsync(Token));

        // Section 7.5's post-hoc review hangs off this flag: the label, and the recap's lead.
        Assert.True(session.AutoDispatched);
        Assert.Single(instance.Runner.Dispatches);
    }

    [Fact]
    public async Task CancellingASessionAlsoTakesItsApprovalShapedJobOutOfTheQueue()
    {
        await using var database = await OrchestrationDatabase.CreateAsync(Token);
        if (database is null)
        {
            return;
        }

        await using var instance = ControlPlaneInstance.Create(database);

        var approved = await instance.SeedApprovedSpecAsync(Token);
        await instance.EnqueueSpecBuildAsync(approved, Token);

        var sessionId = SpecBuildPayload.SessionIdFor(approved.SpecId);

        // The session exists but the job has not been claimed — a cancel pressed while it waited.
        await instance.InScopeAsync(async provider =>
        {
            var db = provider.GetRequiredService<CharterDbContext>();
            db.Sessions.Add(Session.Queue(
                approved.SpecId,
                RunnerKind.GitHubActions,
                "anthropic/claude-opus-5",
                id: sessionId));

            await db.SaveChangesAsync(Token);
            return true;
        });

        await instance.InScopeAsync(provider => provider.GetRequiredService<SessionCoordinator>()
            .CancelAsync(sessionId, "Cancelled by request.", Token));

        var jobs = await instance.JobsAsync(Token);

        Assert.All(jobs.Where(job => job.Type == JobType.Build), job =>
            Assert.Equal(JobStatus.Cancelled, job.Status));

        Assert.True(await instance.Dispatcher.TryBecomeLeaderAsync(Token));
        Assert.Equal(0, await instance.Dispatcher.DispatchOnceAsync(Token));
        Assert.Empty(instance.Runner.Dispatches);
    }

    [Fact]
    public async Task ABuildPayloadThatNamesNothingFailsRatherThanRetryingForever()
    {
        await using var database = await OrchestrationDatabase.CreateAsync(Token);
        if (database is null)
        {
            return;
        }

        await using var instance = ControlPlaneInstance.Create(database);

        await instance.InScopeAsync(provider => provider.GetRequiredService<JobQueue>().EnqueueAsync(
            JobType.Build,
            """{"nothing":"useful"}""",
            maxAttempts: 1,
            cancellationToken: Token));

        Assert.True(await instance.Dispatcher.TryBecomeLeaderAsync(Token));
        Assert.Equal(1, await instance.Dispatcher.DispatchOnceAsync(Token));

        var job = Assert.Single(await instance.JobsAsync(Token));

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Empty(await instance.SessionsAsync(Token));
    }

    [Fact]
    public void TheSessionIdDerivationSeparatesARebuildFromTheBuildItReplaces()
    {
        var spec = Guid.CreateVersion7();
        var feedback = Guid.CreateVersion7().ToString("D");

        // Section 11: "Not quite" becomes a new session on the same spec. If the derivation used the
        // specification alone, the rebuild would silently resume the session they had just rejected.
        Assert.NotEqual(
            SpecBuildPayload.SessionIdFor(spec),
            SpecBuildPayload.SessionIdFor(spec, feedback));

        Assert.Equal(SpecBuildPayload.SessionIdFor(spec), SpecBuildPayload.SessionIdFor(spec));
        Assert.Equal(
            SpecBuildPayload.SessionIdFor(spec, feedback),
            SpecBuildPayload.SessionIdFor(spec, feedback));
    }

    [Theory]
    [InlineData("""{"requestId":"11111111-1111-1111-1111-111111111111","specId":"22222222-2222-2222-2222-222222222222"}""")]
    [InlineData("""{"request_id":"11111111-1111-1111-1111-111111111111","spec_id":"22222222-2222-2222-2222-222222222222"}""")]
    public void BothSpellingsOfTheApprovalPayloadParse(string payload)
    {
        var parsed = SpecBuildPayload.TryParse(payload);

        Assert.NotNull(parsed);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), parsed.RequestId);
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), parsed.SpecId);
        Assert.False(parsed.IsRebuild);
    }

    [Fact]
    public void ASessionShapedPayloadIsNotMistakenForAnApprovalShapedOne()
    {
        var session = Guid.CreateVersion7();
        var payload = new BuildJobPayload { SessionId = session }.ToJson();

        Assert.Equal(session, BuildJobPayload.TryParse(payload)!.SessionId);
        Assert.Null(SpecBuildPayload.TryParse(payload));
    }
}
