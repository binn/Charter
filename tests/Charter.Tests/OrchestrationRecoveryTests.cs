using Charter.Domain;
using Charter.Orchestration;
using Charter.Runners;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Charter.Tests;

/// <summary>
/// The recovery rules of section 2.3, with no database in the way.
/// </summary>
/// <remarks>
/// <see cref="SessionRecovery.Decide"/> is a pure function precisely so that the decision table can
/// be enumerated rather than discovered while debugging a half-restarted instance. Every row of that
/// table is here.
/// </remarks>
public class OrchestrationRecoveryTests
{
    private static SessionJournalSummary Journal(
        long lastSeq = 0,
        int attempts = 0,
        int failures = 0,
        string? terminal = null,
        RunnerKind? runner = null)
        => new(Guid.Empty, lastSeq, attempts, failures, runner, null, terminal, 0m, null);

    private static SessionRecoveryInput Input(
        SessionStatus status = SessionStatus.Queued,
        bool cancelRequested = false,
        SessionJournalSummary? journal = null,
        bool hasOpenJob = false)
        => new(Guid.NewGuid(), status, cancelRequested, journal ?? Journal(), hasOpenJob);

    [Fact]
    public void ADispatchedSessionIsAdoptedAndNeverDispatchedAgain()
    {
        var plan = SessionRecovery.Decide(Input(
            SessionStatus.Running,
            journal: Journal(lastSeq: 27, attempts: 1, runner: RunnerKind.GitHubActions)));

        Assert.Equal(SessionRecoveryAction.Adopt, plan.Action);
        Assert.Equal(27, plan.ResumeFromSeq);
        Assert.Contains("GitHub Actions", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ADispatchThatWasRefusedIsNotAnInFlightRun()
    {
        // One claim, one recorded failure: nothing is holding this session.
        var plan = SessionRecovery.Decide(Input(journal: Journal(attempts: 1, failures: 1)));

        Assert.Equal(SessionRecoveryAction.Dispatch, plan.Action);
    }

    [Fact]
    public void AQueuedSessionWithAJobStillInTheQueueIsLeftAlone()
    {
        var plan = SessionRecovery.Decide(Input(hasOpenJob: true));

        Assert.Equal(SessionRecoveryAction.None, plan.Action);
    }

    [Fact]
    public void AQueuedSessionWithNoJobIsPutBackOnTheQueue()
    {
        var plan = SessionRecovery.Decide(Input());

        Assert.Equal(SessionRecoveryAction.Dispatch, plan.Action);
    }

    [Fact]
    public void CancellationOutranksEverythingElse()
    {
        // A user asked for this to stop and the process died before it did. Nothing else matters.
        var plan = SessionRecovery.Decide(Input(
            SessionStatus.Running,
            cancelRequested: true,
            journal: Journal(attempts: 1, terminal: "failed")));

        Assert.Equal(SessionRecoveryAction.Cancel, plan.Action);
    }

    [Theory]
    [InlineData("failed", SessionStatus.Failed)]
    [InlineData("cancelled", SessionStatus.Cancelled)]
    [InlineData("stale", SessionStatus.Stale)]
    public void AReportedTerminalResultIsSettled(string state, SessionStatus expected)
    {
        var plan = SessionRecovery.Decide(Input(
            SessionStatus.Running,
            journal: Journal(attempts: 1, terminal: state)));

        Assert.Equal(SessionRecoveryAction.Settle, plan.Action);
        Assert.Equal(expected, plan.SettleAs);
    }

    [Fact]
    public void ACompletedRunIsNotSettledBecauseOpeningThePullRequestIsPhaseThree()
    {
        // Section 6 puts PROpen after Running. Claiming a pull request exists because the agent
        // process exited zero would be a claim Charter cannot back up.
        var plan = SessionRecovery.Decide(Input(
            SessionStatus.Running,
            journal: Journal(attempts: 1, terminal: "completed")));

        Assert.Equal(SessionRecoveryAction.Adopt, plan.Action);
        Assert.Null(plan.SettleAs);
        Assert.Null(SessionRecovery.MapTerminal("completed"));
    }

    [Theory]
    [InlineData(SessionStatus.Merged)]
    [InlineData(SessionStatus.Failed)]
    [InlineData(SessionStatus.Cancelled)]
    [InlineData(SessionStatus.Stale)]
    [InlineData(SessionStatus.HandedOff)]
    public void AFinishedSessionIsLeftAloneEvenWithACancelRequestOnIt(SessionStatus status)
    {
        var plan = SessionRecovery.Decide(Input(status, cancelRequested: true));

        Assert.Equal(SessionRecoveryAction.None, plan.Action);
    }

    [Fact]
    public void TheDispatchKeyIsAPureFunctionOfTheSessionAndGeneration()
    {
        var session = Guid.NewGuid();

        // The same session, in two processes, derives the same key - which is what makes the second
        // dispatch lose a primary-key race rather than start a second run.
        Assert.Equal(
            SessionCoordinator.DispatchClaimKey(session, 0),
            SessionCoordinator.DispatchClaimKey(session, 0));

        Assert.NotEqual(
            SessionCoordinator.DispatchClaimKey(session, 0),
            SessionCoordinator.DispatchClaimKey(session, 1));

        Assert.NotEqual(
            SessionCoordinator.DispatchClaimKey(session, 0),
            SessionCoordinator.DispatchClaimKey(Guid.NewGuid(), 0));
    }

    [Fact]
    public void DeterministicEventIdsAreStableAcrossProcessesAndDistinctPerKey()
    {
        var session = Guid.Parse("11111111-2222-3333-4444-555555555555");

        Assert.Equal(
            SessionJournal.DeterministicEventId(session, "runner:1"),
            SessionJournal.DeterministicEventId(session, "runner:1"));

        Assert.NotEqual(
            SessionJournal.DeterministicEventId(session, "runner:1"),
            SessionJournal.DeterministicEventId(session, "runner:2"));

        Assert.NotEqual(
            SessionJournal.DeterministicEventId(session, "runner:1"),
            SessionJournal.DeterministicEventId(Guid.NewGuid(), "runner:1"));
    }

    [Fact]
    public void TheJournalSummaryFoldsDispatchesAgainstFailures()
    {
        Assert.True(Journal(attempts: 1).Dispatched);
        Assert.False(Journal(attempts: 1, failures: 1).Dispatched);
        Assert.True(Journal(attempts: 2, failures: 1).Dispatched);
        Assert.Equal(1, Journal(attempts: 1, failures: 1).DispatchGeneration);
    }
}

/// <summary>The callback and spec URLs the dispatch payload is built from.</summary>
public class OrchestrationOptionsTests
{
    [Fact]
    public void CallbackUrlsHangOffTheRouteTheEndpointsAreMappedAt()
    {
        var session = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var options = new OrchestrationOptions { BaseUrl = new Uri("https://charter.example.com/") };

        Assert.Equal(
            "https://charter.example.com/api/runners/sessions/11111111-2222-3333-4444-555555555555",
            options.CallbackUrlFor(session).ToString());

        Assert.Equal(
            "https://charter.example.com/api/runners/sessions/11111111-2222-3333-4444-555555555555/spec",
            options.SpecUrlFor(session).ToString());

        // And that prefix is the one the endpoints actually claim.
        Assert.Equal("/api/runners/sessions/{sessionId:guid}", RunnerCallbackEndpoints.RoutePrefix);
    }

    [Fact]
    public void EveryReplicaOfAnInstanceCompetesForTheSameAdvisoryLock()
    {
        Assert.Equal(new OrchestrationOptions().DispatcherLockKey, new OrchestrationOptions().DispatcherLockKey);
        Assert.NotEqual(
            OrchestrationOptions.AdvisoryKey("charter.dispatcher"),
            OrchestrationOptions.AdvisoryKey("charter.something-else"));
    }

    [Fact]
    public void EachProcessClaimsUnderItsOwnWorkerIdentity()
    {
        // Section 31: shutdown releases what *this* worker holds, so two replicas must never share
        // an identity or one would hand back the other's in-flight work.
        Assert.NotEqual(
            new OrchestrationOptions { WorkerId = "a" }.WorkerId,
            new OrchestrationOptions { WorkerId = "b" }.WorkerId);

        Assert.False(string.IsNullOrWhiteSpace(new OrchestrationOptions().WorkerId));
    }
}

/// <summary>Job payloads survive a Charter that has been upgraded under them.</summary>
public class OrchestrationJobPayloadTests
{
    [Fact]
    public void OnlyTheSessionIdIsRequiredSoAnOldPayloadStillDispatches()
    {
        var payload = BuildJobPayload.TryParse("""{"session_id":"11111111-2222-3333-4444-555555555555"}""");

        Assert.NotNull(payload);
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), payload.SessionId);
        Assert.Null(payload.RepoFullName);
    }

    [Fact]
    public void AFieldFromANewerCharterIsIgnoredRatherThanFatal()
    {
        var payload = BuildJobPayload.TryParse(
            """{"session_id":"11111111-2222-3333-4444-555555555555","something_new":42}""");

        Assert.NotNull(payload);
    }

    [Fact]
    public void AnUnparseablePayloadIsNull()
    {
        Assert.Null(BuildJobPayload.TryParse("not json"));
    }

    [Fact]
    public void RoundTrippingKeepsEveryField()
    {
        var payload = new BuildJobPayload
        {
            SessionId = Guid.NewGuid(),
            RepoFullName = "acme/spectra",
            BaseBranch = "main",
            BaseCommitSha = "a3f9c21",
            AdapterId = "claude-code",
            RunnerImage = "ghcr.io/binn/charter-runner-dotnet:1",
            AllowPaths = ["src/Features/**"],
            DenyPaths = ["src/Auth/**"],
            TimeoutMinutes = 45,
        };

        var round = BuildJobPayload.TryParse(payload.ToJson());

        Assert.NotNull(round);
        Assert.Equal(payload.SessionId, round.SessionId);
        Assert.Equal(payload.RepoFullName, round.RepoFullName);
        Assert.Equal(payload.BaseBranch, round.BaseBranch);
        Assert.Equal(payload.BaseCommitSha, round.BaseCommitSha);
        Assert.Equal(payload.AdapterId, round.AdapterId);
        Assert.Equal(payload.RunnerImage, round.RunnerImage);
        Assert.Equal(payload.AllowPaths, round.AllowPaths);
        Assert.Equal(payload.DenyPaths, round.DenyPaths);
        Assert.Equal(payload.TimeoutMinutes, round.TimeoutMinutes);
    }

    [Fact]
    public void AnUnreadableRepositoryConfigDeniesEverythingRatherThanWideningTheScope()
    {
        var scope = SessionDispatchPlanner.ReadScope(
            "{ this is not json",
            new BuildJobPayload { SessionId = Guid.NewGuid() });

        Assert.Contains("**", scope.Deny);
    }

    [Fact]
    public void TheRepositorysDenyEntriesAlwaysSurvive()
    {
        // Section 7.5: a repository may only tighten. A job payload cannot drop a committed deny.
        var scope = SessionDispatchPlanner.ReadScope(
            """{"scopes":{"allow":["src/Features/**"],"deny":["src/Auth/**"]}}""",
            new BuildJobPayload { SessionId = Guid.NewGuid(), DenyPaths = ["infra/**"] });

        Assert.Contains("src/Auth/**", scope.Deny);
        Assert.Contains("infra/**", scope.Deny);
        Assert.Contains("src/Features/**", scope.Allow);
    }
}

/// <summary>What <c>AddCharterOrchestration()</c> puts in the container.</summary>
public class OrchestrationWiringTests
{
    private static IServiceCollection Registered()
    {
        var services = new ServiceCollection();
        services.AddCharterOrchestration();
        return services;
    }

    [Fact]
    public void BothHostedServicesOfSection2Point1AreRegistered()
    {
        var hosted = Registered()
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(descriptor => descriptor.ImplementationType)
            .ToArray();

        Assert.Contains(typeof(SessionOrchestrator), hosted);
        Assert.Contains(typeof(QueueDispatcher), hosted);
    }

    [Fact]
    public void TheSeamsAreRegisteredWithRefusingDefaultsRatherThanNulls()
    {
        var services = Registered();

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IGitHubRepositoryDispatcher));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IRunnerCredentialBroker));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IRunnerRegistry));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IQueuedJobHandler));
    }

    [Fact]
    public void PerSessionServicesAreScopedSoTheyNeverOutliveTheirDbContext()
    {
        var services = Registered();

        foreach (var type in new[]
                 {
                     typeof(SessionJournal),
                     typeof(SessionCoordinator),
                     typeof(ISessionDispatchPlanner),
                     typeof(IQueuedJobHandler),
                 })
        {
            var descriptor = Assert.Single(services, candidate => candidate.ServiceType == type);
            Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        }
    }

    [Fact]
    public void ARegisteredGitHubClientIsNotReplacedByTheRefusingDefault()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGitHubRepositoryDispatcher, RecordingGitHubDispatcher>();
        services.AddCharterOrchestration();

        var descriptor = Assert.Single(
            services, candidate => candidate.ServiceType == typeof(IGitHubRepositoryDispatcher));

        Assert.Equal(typeof(RecordingGitHubDispatcher), descriptor.ImplementationType);
    }

    [Fact]
    public void OptionsAreOverridable()
    {
        var services = new ServiceCollection();
        services.AddCharterOrchestration(options => options.WorkerId = "explicit");

        using var provider = services.BuildServiceProvider();

        Assert.Equal("explicit", provider.GetRequiredService<OrchestrationOptions>().WorkerId);
    }
}
