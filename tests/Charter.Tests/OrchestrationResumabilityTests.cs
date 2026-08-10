using Charter.Domain;
using Charter.Orchestration;
using Charter.Runners;
using Microsoft.Extensions.DependencyInjection;

namespace Charter.Tests;

/// <summary>
/// Section 2.3, against a real Postgres: <strong>the container can restart mid-session, so there is
/// no in-memory orchestration state and every session is fully resumable from Postgres alone.</strong>
/// </summary>
/// <remarks>
/// <para>
/// Every test here builds a <see cref="ControlPlaneInstance"/>, does something to it, throws it away,
/// and builds a second one over the same database. The second instance shares nothing with the first
/// except rows — no dictionaries, no cancellation tokens, no dispatch log — which is the only way to
/// test the claim honestly. A restart that reuses the same objects proves nothing.
/// </para>
/// <para>
/// They are in one class deliberately: xUnit runs tests within a class serially, and the orchestrator
/// sweeps every live session in its database, so two of these running concurrently would reconcile
/// each other's rows.
/// </para>
/// </remarks>
public class OrchestrationResumabilityTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The lease used where the point is that it has already lapsed.</summary>
    private static readonly TimeSpan ExpiredLease = TimeSpan.FromMilliseconds(1);

    [Fact]
    public async Task ASessionSurvivesTheContainerRestartingMidRunWithNoDuplicatesAndNoSecondDispatch()
    {
        await using var database = await OrchestrationDatabase.CreateAsync(Token);
        if (database is null)
        {
            return;
        }

        // The backend outlives both control-plane instances, exactly as GitHub Actions or a Mac mini
        // would. It is the only thing that can count how many times the work was really started.
        var runner = new RecordingRunner();
        Guid session;
        long cursorAtCrash;

        // ---- Instance A: claims the job, dispatches, streams three events, then dies. -------------
        var first = ControlPlaneInstance.Create(database, runner, lease: ExpiredLease);
        try
        {
            session = await first.SeedSessionAsync(Token);
            await first.EnqueueBuildAsync(session, Token);

            Assert.True(await first.Dispatcher.TryBecomeLeaderAsync(Token));
            Assert.Equal(1, await first.Dispatcher.DispatchOnceAsync(Token));

            Assert.Single(runner.Dispatches, dispatch => dispatch.SessionId == session);
            Assert.Equal(SessionStatus.Running, (await first.LoadSessionAsync(session, Token))!.Status);

            for (var index = 1; index <= 3; index++)
            {
                var appended = await first.IngestRunnerEventAsync(
                    session,
                    index,
                    EventTypes.ToolUse,
                    $$"""{"step":{{index}}}""",
                    Token);

                Assert.True(appended.Appended);
            }

            cursorAtCrash = (await first.SummarizeAsync(session, Token)).LastSeq;
        }
        finally
        {
            // Killed, not stopped. No graceful shutdown, no lock release, no claim handed back.
            await first.KillAsync();
        }

        // ---- Instance B: a brand new process, sharing only the database. -------------------------
        await using var second = ControlPlaneInstance.Create(database, runner, lease: ExpiredLease);

        var reconciliations = await second.Orchestrator.ReconcileAsync(startup: true, Token);
        var plan = Assert.Single(reconciliations, entry => entry.SessionId == session).Plan;

        // It read the journal and worked out that a backend already has this session.
        Assert.Equal(SessionRecoveryAction.Adopt, plan.Action);
        Assert.Equal(cursorAtCrash, plan.ResumeFromSeq);

        // The job A never completed came back on lease expiry and was claimed here. It must not
        // produce a second dispatch.
        Assert.True(await second.Dispatcher.TryBecomeLeaderAsync(Token));
        await second.Dispatcher.ReclaimExpiredLeasesAsync(Token);
        await second.Dispatcher.DispatchOnceAsync(Token);

        Assert.Single(runner.Dispatches, dispatch => dispatch.SessionId == session);

        // The runner reconnects and replays what it already sent, then carries on.
        for (var index = 1; index <= 3; index++)
        {
            var replay = await second.IngestRunnerEventAsync(
                session,
                index,
                EventTypes.ToolUse,
                $$"""{"step":{{index}}}""",
                Token);

            Assert.False(replay.Appended);
        }

        for (var index = 4; index <= 5; index++)
        {
            Assert.True((await second.IngestRunnerEventAsync(
                session,
                index,
                EventTypes.ToolUse,
                $$"""{"step":{{index}}}""",
                Token)).Appended);
        }

        var events = await second.EventsAsync(session, Token);

        // No duplicates: the sequence is strictly increasing and every seq is distinct.
        Assert.Equal(events.Select(entry => entry.Seq).Distinct().Count(), events.Count);
        Assert.Equal([.. events.Select(entry => entry.Seq).Order()], [.. events.Select(entry => entry.Seq)]);

        // Exactly one dispatch record, three original tool_use events plus the two new ones, and the
        // resume marker that says where the restart happened.
        Assert.Single(events, entry => entry.Type == OrchestrationEventTypes.SessionDispatched);
        Assert.Equal(5, events.Count(entry => entry.Type == EventTypes.ToolUse));

        var resumed = Assert.Single(events, entry => entry.Type == OrchestrationEventTypes.SessionResumed);
        using var marker = System.Text.Json.JsonDocument.Parse(resumed.Payload);
        Assert.Equal(cursorAtCrash, marker.RootElement.GetProperty("resumed_from_seq").GetInt64());

        // And the summary a third instance would read still says the same thing.
        var summary = await second.SummarizeAsync(session, Token);
        Assert.True(summary.Dispatched);
        Assert.Equal(RunnerKind.GitHubActions, summary.Runner);
    }

    [Fact]
    public async Task RecoveryIsIdempotentSoRestartingRepeatedlyChangesNothing()
    {
        await using var database = await OrchestrationDatabase.CreateAsync(Token);
        if (database is null)
        {
            return;
        }

        var runner = new RecordingRunner();

        var first = ControlPlaneInstance.Create(database, runner);
        var session = await first.SeedSessionAsync(Token);
        await first.EnqueueBuildAsync(session, Token);
        await first.Dispatcher.TryBecomeLeaderAsync(Token);
        await first.Dispatcher.DispatchOnceAsync(Token);
        await first.KillAsync();

        // Crash-loop: three separate processes each run startup recovery over the same session.
        for (var restart = 0; restart < 3; restart++)
        {
            await using var instance = ControlPlaneInstance.Create(database, runner);
            await instance.Orchestrator.ReconcileAsync(startup: true, Token);
            await instance.Dispatcher.TryBecomeLeaderAsync(Token);
            await instance.Dispatcher.DispatchOnceAsync(Token);
        }

        await using var final = ControlPlaneInstance.Create(database, runner);
        var events = await final.EventsAsync(session, Token);

        Assert.Single(runner.Dispatches, dispatch => dispatch.SessionId == session);
        Assert.Single(events, entry => entry.Type == OrchestrationEventTypes.SessionDispatched);

        // The resume marker is keyed on the cursor, so a restart at an unchanged cursor writes one.
        Assert.Single(events, entry => entry.Type == OrchestrationEventTypes.SessionResumed);
    }

    [Fact]
    public async Task TwoReplicasDoNotDoubleDispatch()
    {
        await using var database = await OrchestrationDatabase.CreateAsync(Token);
        if (database is null)
        {
            return;
        }

        var runner = new RecordingRunner();
        var lockKey = Random.Shared.NextInt64();

        await using var alpha = ControlPlaneInstance.Create(database, runner, lockKey, "replica-alpha");
        await using var beta = ControlPlaneInstance.Create(database, runner, lockKey, "replica-beta");

        var session = await alpha.SeedSessionAsync(Token);
        await alpha.EnqueueBuildAsync(session, Token);

        // Section 2.3: pg_try_advisory_lock, so scaling to two replicas does not double-dispatch.
        Assert.True(await alpha.Dispatcher.TryBecomeLeaderAsync(Token));
        Assert.False(await beta.Dispatcher.TryBecomeLeaderAsync(Token));
        Assert.True(alpha.Dispatcher.IsLeader);
        Assert.False(beta.Dispatcher.IsLeader);

        // Belt and braces: even with both dispatching at once - which the lock is there to prevent -
        // SKIP LOCKED gives the row to one of them and the dispatch claim refuses the other.
        var claims = await Task.WhenAll(
            alpha.Dispatcher.DispatchOnceAsync(Token),
            beta.Dispatcher.DispatchOnceAsync(Token));

        Assert.Equal(1, claims.Sum());
        Assert.Single(runner.Dispatches, dispatch => dispatch.SessionId == session);
    }

    [Fact]
    public async Task TheAdvisoryLockPassesToTheSurvivingReplicaOnGracefulShutdown()
    {
        await using var database = await OrchestrationDatabase.CreateAsync(Token);
        if (database is null)
        {
            return;
        }

        var lockKey = Random.Shared.NextInt64();

        var leader = ControlPlaneInstance.Create(database, lockKey: lockKey, workerId: "replica-leader");
        await using var standby = ControlPlaneInstance.Create(database, lockKey: lockKey, workerId: "replica-standby");

        Assert.True(await leader.Dispatcher.TryBecomeLeaderAsync(Token));
        Assert.False(await standby.Dispatcher.TryBecomeLeaderAsync(Token));

        // Section 31: graceful shutdown releases the advisory lock rather than making the survivor
        // wait for a connection to time out.
        await leader.DisposeAsync();

        Assert.True(await standby.Dispatcher.TryBecomeLeaderAsync(Token));
    }

    [Fact]
    public async Task GracefulShutdownHandsBackClaimedJobsForRetry()
    {
        await using var database = await OrchestrationDatabase.CreateAsync(Token);
        if (database is null)
        {
            return;
        }

        // A backend that never answers, so the job stays claimed while the instance shuts down.
        var stuck = new RecordingRunner { ThrowWith = new OperationCanceledException() };
        var instance = ControlPlaneInstance.Create(database, stuck, workerId: "replica-draining");

        var session = await instance.SeedSessionAsync(Token);
        await instance.EnqueueBuildAsync(session, Token);

        await instance.InScopeAsync(async provider =>
        {
            var queue = provider.GetRequiredService<Charter.Data.JobQueue>();
            var claimed = await queue.ClaimAsync(
                instance.Options.WorkerId,
                instance.Options.Lease,
                cancellationToken: Token);

            Assert.Single(claimed);
            return claimed;
        });

        await instance.DisposeAsync();

        // The job is pending again, immediately, rather than after a five-minute lease.
        await using var successor = ControlPlaneInstance.Create(database);
        var jobs = await successor.JobsAsync(Token);

        Assert.Equal(JobStatus.Pending, Assert.Single(jobs).Status);
    }

    [Fact]
    public async Task AnExpiredLeaseReturnsWorkToTheQueueAndTheSuccessorPicksItUp()
    {
        await using var database = await OrchestrationDatabase.CreateAsync(Token);
        if (database is null)
        {
            return;
        }

        var runner = new RecordingRunner();
        await using var instance = ControlPlaneInstance.Create(database, runner, lease: ExpiredLease);

        var session = await instance.SeedSessionAsync(Token);
        await instance.EnqueueBuildAsync(session, Token);

        // A worker claims it and never comes back.
        await instance.InScopeAsync(async provider =>
        {
            var queue = provider.GetRequiredService<Charter.Data.JobQueue>();
            return await queue.ClaimAsync("worker-that-died", ExpiredLease, cancellationToken: Token);
        });

        Assert.Equal(1, await instance.Dispatcher.ReclaimExpiredLeasesAsync(Token));

        await instance.Dispatcher.TryBecomeLeaderAsync(Token);
        Assert.Equal(1, await instance.Dispatcher.DispatchOnceAsync(Token));

        Assert.Single(runner.Dispatches, dispatch => dispatch.SessionId == session);

        var job = Assert.Single(await instance.JobsAsync(Token));
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(2, job.Attempts);
    }

    [Fact]
    public async Task CancellingKillsTheRunnerSettlesTheCostAndTakesTheJobOutOfTheQueue()
    {
        await using var database = await OrchestrationDatabase.CreateAsync(Token);
        if (database is null)
        {
            return;
        }

        var runner = new RecordingRunner { ExternalReference = "https://github.com/acme/spectra/actions/runs/4242" };
        await using var instance = ControlPlaneInstance.Create(database, runner);

        var session = await instance.SeedSessionAsync(Token);
        await instance.EnqueueBuildAsync(session, Token);
        await instance.Dispatcher.TryBecomeLeaderAsync(Token);
        await instance.Dispatcher.DispatchOnceAsync(Token);

        // The run reports what it has spent so far, then the user presses cancel.
        await instance.IngestRunnerEventAsync(session, 1, EventTypes.Cost, """{"usd":0.42}""", Token);

        // A follow-up session was queued behind it, to prove the queue is cleaned up too.
        await instance.EnqueueBuildAsync(session, Token);

        var cancelled = await instance.InScopeAsync(provider => provider
            .GetRequiredService<SessionCoordinator>()
            .CancelAsync(session, "Cancelled from the status thread.", Token));

        Assert.True(cancelled);

        // 1. The runner was actually told to stop, with the handle it needs to do it.
        var cancellation = Assert.Single(runner.Cancellations);
        Assert.Equal(session, cancellation.SessionId);
        Assert.Equal("https://github.com/acme/spectra/actions/runs/4242", cancellation.ExternalReference);

        // 2. Cost settled from what the transcript recorded.
        var settled = await instance.LoadSessionAsync(session, Token);
        Assert.Equal(SessionStatus.Cancelled, settled!.Status);
        Assert.Equal(0.42m, settled.CostUsd);
        Assert.NotNull(settled.CancelRequestedAt);
        Assert.NotNull(settled.EndedAt);

        // 3. Nothing is left in the queue to start it again.
        var jobs = await instance.JobsAsync(Token);
        Assert.DoesNotContain(jobs, job => job.Status == JobStatus.Pending);
    }

    [Fact]
    public async Task ACancellationThatWasInterruptedIsFinishedByTheNextProcess()
    {
        await using var database = await OrchestrationDatabase.CreateAsync(Token);
        if (database is null)
        {
            return;
        }

        var runner = new RecordingRunner();

        var first = ControlPlaneInstance.Create(database, runner);
        var session = await first.SeedSessionAsync(Token);
        await first.EnqueueBuildAsync(session, Token);
        await first.Dispatcher.TryBecomeLeaderAsync(Token);
        await first.Dispatcher.DispatchOnceAsync(Token);

        // The cancel button set the column, and then the container went away before the runner was
        // stopped. This is the case a design with in-memory state loses entirely.
        await first.InScopeAsync(async provider =>
        {
            var db = provider.GetRequiredService<Charter.Data.CharterDbContext>();
            var row = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .FirstAsync(db.Sessions, candidate => candidate.Id == session, Token);

            row.RequestCancellation();
            await db.SaveChangesAsync(Token);
            return true;
        });

        await first.KillAsync();

        await using var second = ControlPlaneInstance.Create(database, runner);
        var reconciliations = await second.Orchestrator.ReconcileAsync(startup: true, Token);

        Assert.Equal(
            SessionRecoveryAction.Cancel,
            Assert.Single(reconciliations, entry => entry.SessionId == session).Plan.Action);

        Assert.Single(runner.Cancellations);
        Assert.Equal(SessionStatus.Cancelled, (await second.LoadSessionAsync(session, Token))!.Status);
    }

    [Fact]
    public async Task ASessionWithNoEligibleRunnerQueuesWithAnExplanationInsteadOfFailing()
    {
        await using var database = await OrchestrationDatabase.CreateAsync(Token);
        if (database is null)
        {
            return;
        }

        // Linux only, and the job needs a Mac.
        var runner = new RecordingRunner(RunnerKind.GitHubActions, ["linux", "dotnet:10"]);
        await using var instance = ControlPlaneInstance.Create(database, runner);

        var session = await instance.SeedSessionAsync(Token);

        var routing = await instance.InScopeAsync(async provider => await provider
            .GetRequiredService<IRunnerRegistry>()
            .RouteAsync(["macos", "xcode:16"], cancellationToken: Token));

        await instance.InScopeAsync(async provider =>
        {
            await provider.GetRequiredService<SessionCoordinator>()
                .ExplainQueuedAsync(session, ["macos", "xcode:16"], routing, Token);
            return true;
        });

        var summary = await instance.SummarizeAsync(session, Token);

        Assert.False(summary.Dispatched);
        Assert.Equal(
            "No runner available with macOS and Xcode 16. Register one in Settings → Runners.",
            summary.QueuedExplanation);

        // Queued, not failed: the session row is untouched and still waiting.
        Assert.Equal(SessionStatus.Queued, (await instance.LoadSessionAsync(session, Token))!.Status);

        // Writing the same explanation again does not add a second event to the thread.
        await instance.InScopeAsync(async provider =>
        {
            await provider.GetRequiredService<SessionCoordinator>()
                .ExplainQueuedAsync(session, ["macos", "xcode:16"], routing, Token);
            return true;
        });

        Assert.Single((await instance.EventsAsync(session, Token))
            , entry => entry.Type == OrchestrationEventTypes.SessionQueued);
    }

    [Fact]
    public async Task AJobRequiringACapabilityNoRunnerHasIsNeverClaimedAtAll()
    {
        await using var database = await OrchestrationDatabase.CreateAsync(Token);
        if (database is null)
        {
            return;
        }

        var runner = new RecordingRunner(RunnerKind.GitHubActions, ["linux"]);
        await using var instance = ControlPlaneInstance.Create(database, runner);

        var session = await instance.SeedSessionAsync(Token);

        await instance.InScopeAsync(provider => provider.GetRequiredService<SessionCoordinator>().EnqueueAsync(
            new BuildJobPayload { SessionId = session },
            ["macos", "xcode:16"],
            cancellationToken: Token));

        await instance.Dispatcher.TryBecomeLeaderAsync(Token);

        // The queue's own capability filter is what makes "queues rather than fails" the default:
        // the job is simply not claimable by a control plane with no macOS backend.
        Assert.Equal(0, await instance.Dispatcher.DispatchOnceAsync(Token));

        var job = Assert.Single(await instance.JobsAsync(Token));
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(0, job.Attempts);
        Assert.Empty(runner.Dispatches);
    }

    [Fact]
    public async Task ARefusedDispatchIsUndoneSoAGenuineRetryStillHappens()
    {
        await using var database = await OrchestrationDatabase.CreateAsync(Token);
        if (database is null)
        {
            return;
        }

        var runner = new RecordingRunner { RefuseWith = "The GitHub App installation was revoked." };
        await using var instance = ControlPlaneInstance.Create(database, runner);

        var session = await instance.SeedSessionAsync(Token);

        var refused = await instance.InScopeAsync(provider => provider
            .GetRequiredService<SessionCoordinator>()
            .DispatchAsync(new BuildJobPayload { SessionId = session }, Token));

        Assert.Equal(DispatchDecision.Failed, refused.Decision);

        // The claim was written before the backend was called, and the refusal undid it, so the
        // session is dispatchable again rather than stuck marked as in flight.
        var afterRefusal = await instance.SummarizeAsync(session, Token);
        Assert.False(afterRefusal.Dispatched);
        Assert.Equal(1, afterRefusal.DispatchGeneration);

        runner.RefuseWith = null;

        var retried = await instance.InScopeAsync(provider => provider
            .GetRequiredService<SessionCoordinator>()
            .DispatchAsync(new BuildJobPayload { SessionId = session }, Token));

        Assert.Equal(DispatchDecision.Dispatched, retried.Decision);
        Assert.True((await instance.SummarizeAsync(session, Token)).Dispatched);

        // The backend really was called twice - once refused, once accepted - which is the point:
        // the guard blocks a repeat of the *same* attempt, not an honest retry after a failure.
        Assert.Equal(2, runner.Dispatches.Count(dispatch => dispatch.SessionId == session));
    }

    [Fact]
    public async Task AQueuedSessionThatLostItsJobIsPutBackOnTheQueueByRecovery()
    {
        await using var database = await OrchestrationDatabase.CreateAsync(Token);
        if (database is null)
        {
            return;
        }

        await using var instance = ControlPlaneInstance.Create(database);

        // Approved and queued, but the job never made it - the process died between the two writes.
        var session = await instance.SeedSessionAsync(Token);

        Assert.Empty(await instance.JobsAsync(Token));

        var reconciliations = await instance.Orchestrator.ReconcileAsync(startup: true, Token);

        Assert.Equal(
            SessionRecoveryAction.Dispatch,
            Assert.Single(reconciliations, entry => entry.SessionId == session).Plan.Action);

        var job = Assert.Single(await instance.JobsAsync(Token));
        Assert.Equal(JobType.Build, job.Type);
        Assert.Equal(JobStatus.Pending, job.Status);

        // And a second sweep does not enqueue it twice, because the job is now open.
        await instance.Orchestrator.ReconcileAsync(cancellationToken: Token);
        Assert.Single(await instance.JobsAsync(Token));
    }

    [Fact]
    public async Task ATerminalResultReportedWhileTheProcessWasDownIsAppliedOnRestart()
    {
        await using var database = await OrchestrationDatabase.CreateAsync(Token);
        if (database is null)
        {
            return;
        }

        var runner = new RecordingRunner();

        var first = ControlPlaneInstance.Create(database, runner);
        var session = await first.SeedSessionAsync(Token);
        await first.EnqueueBuildAsync(session, Token);
        await first.Dispatcher.TryBecomeLeaderAsync(Token);
        await first.Dispatcher.DispatchOnceAsync(Token);

        // The webhook landed and was journalled; the process died before the session row was updated.
        await first.IngestRunnerEventAsync(session, 1, EventTypes.Cost, """{"usd":1.25}""", Token);
        await first.IngestRunnerEventAsync(
            session,
            2,
            EventTypes.SessionEnded,
            """{"state":"failed","message":"the checks did not pass"}""",
            Token);

        await first.KillAsync();

        await using var second = ControlPlaneInstance.Create(database, runner);
        var reconciliations = await second.Orchestrator.ReconcileAsync(startup: true, Token);

        Assert.Equal(
            SessionRecoveryAction.Settle,
            Assert.Single(reconciliations, entry => entry.SessionId == session).Plan.Action);

        var settled = await second.LoadSessionAsync(session, Token);
        Assert.Equal(SessionStatus.Failed, settled!.Status);
        Assert.Equal(1.25m, settled.CostUsd);
    }

    [Fact]
    public async Task TheSessionsPathScopeComesFromTheRepositorysCommittedConfig()
    {
        await using var database = await OrchestrationDatabase.CreateAsync(Token);
        if (database is null)
        {
            return;
        }

        var runner = new RecordingRunner();
        await using var instance = ControlPlaneInstance.Create(database, runner);

        var session = await instance.SeedSessionAsync(
            Token,
            configSnapshot: """
                {"runner_image":"ghcr.io/binn/charter-runner-dotnet:1",
                 "scopes":{"allow":["src/Features/**"],"deny":["src/Auth/**","**/Migrations/**"]}}
                """);

        await instance.EnqueueBuildAsync(session, Token);
        await instance.Dispatcher.TryBecomeLeaderAsync(Token);
        await instance.Dispatcher.DispatchOnceAsync(Token);

        var dispatch = Assert.Single(runner.Dispatches, candidate => candidate.SessionId == session);

        Assert.Equal(["src/Features/**"], dispatch.PathScope.Allow);
        Assert.Equal(["src/Auth/**", "**/Migrations/**"], dispatch.PathScope.Deny);
        Assert.Equal("ghcr.io/binn/charter-runner-dotnet:1", dispatch.RunnerImage);
        Assert.Equal("acme/spectra", dispatch.RepoFullName);

        // The callback base the shipped workflow appends /credentials, /events and /result to.
        Assert.Equal(
            $"https://charter.example.test/api/runners/sessions/{session:D}",
            dispatch.CallbackUrl.ToString());
    }
}
