using System.Text.Json;
using Charter.Agent;
using Charter.Agent.Capabilities;
using Charter.Agent.Jobs;
using Charter.Agent.Logging;
using Charter.Agent.Protocol;
using Charter.Agent.Session;

namespace Charter.Tests;

/// <summary>
/// The agent's half of the protocol: registration, claiming, leases and version negotiation
/// (sections 33.3, 33.4, 33.6). Nothing here touches a socket or a control plane.
/// </summary>
public class AgentSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheFirstFrameAdvertisesTheProtocolAndTheProbedCapabilities()
    {
        var session = NewSession(out _);

        var step = session.Open(Now);

        var hello = Assert.Single(step.Send);
        Assert.Equal(MessageTypes.Hello, hello.Type);
        Assert.Equal(AgentProtocol.Version, hello.ProtocolVersion);

        var payload = hello.ReadPayload<HelloPayload>()!;
        Assert.Equal(AgentProtocol.Version, payload.ProtocolVersion);
        Assert.Contains("linux", payload.Capabilities);
        Assert.Contains("dotnet:10.0.100", payload.Capabilities);
        Assert.Equal("docker", payload.Mode);
        Assert.Equal(2, payload.Concurrency);
    }

    [Fact]
    public void RegistrationIsFollowedByAClaimForEveryFreeSlot()
    {
        var session = NewSession(out _);
        session.Open(Now);

        var step = session.Receive(Welcome(), Now);

        Assert.Equal(SessionPhase.Ready, session.Phase);
        var claim = Assert.Single(step.Send, e => e.Type == MessageTypes.JobClaim);
        var payload = claim.ReadPayload<JobClaimPayload>()!;
        Assert.Equal(2, payload.MaxJobs);
        Assert.Contains("dotnet:10.0.100", payload.Capabilities);
    }

    [Fact]
    public void AGrantedJobThisHostCanRunIsStartedAndLeased()
    {
        var session = Ready();

        var step = session.Receive(Grant(Job("job-1", ["linux", "dotnet:10"])), Now);

        var started = Assert.Single(step.Start);
        Assert.Equal("job-1", started.JobId);
        Assert.Equal(["job-1"], session.HeldJobIds);
        Assert.Equal(1, session.AvailableSlots);
        Assert.DoesNotContain(step.Send, e => e.Type == MessageTypes.JobResult);
    }

    [Fact]
    public void AGrantedJobThisHostCannotRunIsHandedStraightBack()
    {
        // Claims are filtered by capability at the plane, but the agent's set can change between
        // the claim and the grant. Running it anyway burns a lease and fails as if the agent broke.
        var session = Ready();

        var step = session.Receive(Grant(Job("job-1", ["macos", "xcode:16"])), Now);

        Assert.Empty(step.Start);
        Assert.Empty(session.HeldJobIds);

        var result = Assert.Single(step.Send, e => e.Type == MessageTypes.JobResult);
        var payload = result.ReadPayload<JobResultPayload>()!;
        Assert.Equal(JobOutcomes.Abandoned, payload.Outcome);
        Assert.Contains("macos", payload.Error, StringComparison.Ordinal);
        Assert.Contains("xcode:16", payload.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ConcurrencyIsARealLimit()
    {
        var session = Ready(concurrency: 1);

        var step = session.Receive(
            Grant(Job("job-1", ["linux"]), Job("job-2", ["linux"])),
            Now);

        Assert.Single(step.Start);
        var handedBack = Assert.Single(step.Send, e => e.Type == MessageTypes.JobResult);
        Assert.Equal("job-2", handedBack.ReadPayload<JobResultPayload>()!.JobId);
        Assert.Equal(0, session.AvailableSlots);
    }

    [Fact]
    public void AnIdleAgentDoesNotClaimFasterThanTheClaimInterval()
    {
        var session = Ready();
        session.Receive(EmptyGrant(), Now);

        var immediately = session.Advance(Now.AddSeconds(1));
        Assert.DoesNotContain(immediately.Send, e => e.Type == MessageTypes.JobClaim);

        var later = session.Advance(Now.AddSeconds(6));
        Assert.Contains(later.Send, e => e.Type == MessageTypes.JobClaim);
    }

    [Fact]
    public void HeartbeatsCarryEveryHeldLeaseAndTheAckRenewsThem()
    {
        var session = Ready();
        session.Receive(Grant(Job("job-1", ["linux"], leaseSeconds: 60)), Now);

        var beat = session.Advance(Now.AddSeconds(31));
        var heartbeat = Assert.Single(beat.Send, e => e.Type == MessageTypes.Heartbeat);
        var payload = heartbeat.ReadPayload<HeartbeatPayload>()!;
        Assert.Equal(["job-1"], payload.HeldJobIds);
        Assert.Equal("busy", payload.Status);
        Assert.Equal(1, payload.AvailableSlots);

        session.Receive(
            Envelope.Create(
                MessageTypes.HeartbeatAck,
                new HeartbeatAckPayload
                {
                    Leases = [new LeaseGrant { JobId = "job-1", LeaseExpiresAt = Now.AddSeconds(180) }],
                },
                Now.AddSeconds(31)),
            Now.AddSeconds(31));

        // The original lease would have lapsed by now; the renewal carried it past that.
        var afterOriginalExpiry = session.Advance(Now.AddSeconds(90));
        Assert.Empty(afterOriginalExpiry.Stop);
        Assert.Equal(["job-1"], session.HeldJobIds);
    }

    [Fact]
    public void ALapsedLeaseStopsTheLocalWorkAndReportsNothing()
    {
        // The control plane re-queues an unrenewed job. If the agent kept running it, two runners
        // would be pushing to the same branch.
        var session = Ready();
        session.Receive(Grant(Job("job-1", ["linux"], leaseSeconds: 60)), Now);

        var step = session.Advance(Now.AddSeconds(61));

        var stop = Assert.Single(step.Stop);
        Assert.Equal("job-1", stop.JobId);
        Assert.False(stop.Report);
        Assert.Empty(session.HeldJobIds);
        Assert.DoesNotContain(step.Send, e => e.Type == MessageTypes.JobResult);
        Assert.Contains(step.Notes, n => n.Level == LogLevel.Warning && n.Message.Contains("lease expired", StringComparison.Ordinal));
    }

    [Fact]
    public void FinishingAJobReportsItAndFreesTheSlotImmediately()
    {
        var session = Ready(concurrency: 1);
        session.Receive(Grant(Job("job-1", ["linux"])), Now);

        var step = session.JobFinished(
            new JobCompletion("job-1", JobOutcomes.Succeeded, 0, null, 1234), Now.AddMinutes(1));

        var result = Assert.Single(step.Send, e => e.Type == MessageTypes.JobResult);
        var payload = result.ReadPayload<JobResultPayload>()!;
        Assert.Equal(JobOutcomes.Succeeded, payload.Outcome);
        Assert.Equal(0, payload.ExitCode);
        Assert.Equal(1234, payload.DurationMs);
        Assert.Contains(step.Send, e => e.Type == MessageTypes.JobClaim);
        Assert.Equal(1, session.AvailableSlots);
    }

    [Fact]
    public void AJobWhoseLeaseAlreadyLapsedIsNotReportedWhenItFinallyStops()
    {
        var session = Ready();
        session.Receive(Grant(Job("job-1", ["linux"], leaseSeconds: 60)), Now);
        session.Advance(Now.AddSeconds(61));

        var step = session.JobFinished(new JobCompletion("job-1", JobOutcomes.Cancelled), Now.AddSeconds(62));

        Assert.DoesNotContain(step.Send, e => e.Type == MessageTypes.JobResult);
    }

    [Fact]
    public void AProtocolMismatchRefusesWorkWithAMessageThatSaysWhichSideToUpgrade()
    {
        var session = NewSession(out _);
        session.Open(Now);

        var step = session.Receive(Welcome(protocolVersion: AgentProtocol.Version + 7), Now);

        Assert.Equal(SessionPhase.Refusing, session.Phase);
        Assert.DoesNotContain(step.Send, e => e.Type == MessageTypes.JobClaim);
        Assert.NotNull(session.RefusalReason);
        Assert.Contains("Protocol mismatch", session.RefusalReason, StringComparison.Ordinal);
        Assert.Contains("Upgrade charter-agent", session.RefusalReason, StringComparison.Ordinal);
        Assert.Contains(step.Notes, n => n.Level == LogLevel.Error);
    }

    [Fact]
    public void AnOlderControlPlaneIsNamedAsTheSideToUpgrade()
    {
        var negotiation = ProtocolNegotiation.Evaluate(AgentProtocol.MinimumSupportedVersion - 1, [0]);

        Assert.False(negotiation.Ok);
        Assert.Contains("Upgrade the control plane", negotiation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusingAgentHandsBackWorkGrantedAnyway()
    {
        var session = NewSession(out _);
        session.Open(Now);
        session.Receive(Welcome(protocolVersion: 99), Now);

        var step = session.Receive(Grant(Job("job-1", ["linux"])), Now);

        Assert.Empty(step.Start);
        var result = Assert.Single(step.Send, e => e.Type == MessageTypes.JobResult);
        Assert.Equal(JobOutcomes.Abandoned, result.ReadPayload<JobResultPayload>()!.Outcome);
    }

    [Fact]
    public void ARefusingAgentStillHeartbeatsSoTheOperatorCanSeeWhy()
    {
        var session = NewSession(out _);
        session.Open(Now);
        session.Receive(Welcome(protocolVersion: 99), Now);

        var step = session.Advance(Now.AddSeconds(31));

        var heartbeat = Assert.Single(step.Send, e => e.Type == MessageTypes.Heartbeat);
        Assert.Equal("refusing", heartbeat.ReadPayload<HeartbeatPayload>()!.Status);
    }

    [Fact]
    public void AnExplicitMismatchFrameIsAlsoRefused()
    {
        var session = NewSession(out _);
        session.Open(Now);

        var step = session.Receive(
            Envelope.Create(
                MessageTypes.ProtocolMismatch,
                new ProtocolMismatchPayload { ServerProtocolVersion = 42, SupportedProtocolVersions = [42, 41] },
                Now),
            Now);

        Assert.Equal(SessionPhase.Refusing, session.Phase);
        Assert.NotNull(step.Close);
        Assert.False(step.Close!.CredentialRevoked);
    }

    [Fact]
    public void RevocationStopsInFlightWorkAndEndsTheConnectionForGood()
    {
        var session = Ready();
        session.Receive(Grant(Job("job-1", ["linux"])), Now);

        var step = session.Receive(
            Envelope.Create(MessageTypes.Revoked, new RevokedPayload { Reason = "revoked in the UI" }, Now),
            Now);

        Assert.NotNull(step.Close);
        Assert.True(step.Close!.CredentialRevoked);
        Assert.Single(step.Stop);
        Assert.Empty(session.HeldJobIds);
    }

    [Fact]
    public void ADailyReprobeIsRequestedAndTheNewSetIsReportedOnlyWhenItChanged()
    {
        var session = Ready();

        var due = session.Advance(Now.AddHours(25));
        Assert.True(due.ReprobeRequested);

        var unchanged = session.CapabilitiesRefreshed(Probed(), Now.AddHours(25));
        Assert.DoesNotContain(unchanged.Send, e => e.Type == MessageTypes.CapabilitiesReport);

        var changed = session.CapabilitiesRefreshed(
            new CapabilitySet(["linux", "runner:docker", "dotnet:10.0.200"], Now.AddHours(25), []),
            Now.AddHours(25));

        var report = Assert.Single(changed.Send, e => e.Type == MessageTypes.CapabilitiesReport);
        Assert.Contains("dotnet:10.0.200", report.ReadPayload<CapabilitiesReportPayload>()!.Capabilities);
        Assert.Contains(changed.Notes, n => n.Message.Contains("capabilities lost", StringComparison.Ordinal));
    }

    [Fact]
    public void ShuttingDownHandsBackEveryLeaseRatherThanWaitingOutTheTtl()
    {
        var session = Ready();
        session.Receive(Grant(Job("job-1", ["linux"]), Job("job-2", ["linux"])), Now);

        var step = session.Drain("the agent is shutting down", Now.AddMinutes(1));

        var goodbye = Assert.Single(step.Send);
        Assert.Equal(MessageTypes.Goodbye, goodbye.Type);
        Assert.Equal(["job-1", "job-2"], goodbye.ReadPayload<GoodbyePayload>()!.ReleasedJobIds);
        Assert.Equal(2, step.Stop.Count);
        Assert.All(step.Stop, s => Assert.False(s.Report));
    }

    [Fact]
    public void AnUnknownMessageTypeIsIgnoredRatherThanFatal()
    {
        var session = Ready();

        var step = session.Receive(Envelope.Create("something.newer", new { }, Now), Now);

        Assert.Null(step.Close);
        Assert.Empty(step.Send);
    }

    [Fact]
    public void AnUpdateOfferOnlyWarnsUnlessTheOperatorOptedIn()
    {
        var warned = NewSession(out _);
        warned.Open(Now);
        var warning = warned.Receive(Welcome(update: new AgentUpdateOffer { LatestVersion = "9.9.9" }), Now);
        Assert.Contains(warning.Notes, n => n.Level == LogLevel.Warning && n.Message.Contains("--auto-update", StringComparison.Ordinal));

        var opted = new AgentSession(Options(autoUpdate: true), Probed(), Now);
        opted.Open(Now);
        var accepted = opted.Receive(Welcome(update: new AgentUpdateOffer { LatestVersion = "9.9.9" }), Now);
        Assert.Contains(accepted.Notes, n => n.Level == LogLevel.Info && n.Message.Contains("9.9.9", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------------------------

    internal static CapabilitySet Probed() =>
        new(["dotnet:10.0.100", "linux", "node:22.11.0", "runner:docker"], Now, []);

    internal static AgentOptions Options(int concurrency = 2, bool autoUpdate = false) => new()
    {
        Server = new Uri("https://charter.example.com"),
        Token = null,
        Mode = AgentExecutionMode.Docker,
        Concurrency = concurrency,
        Name = "test-agent",
        StateDirectory = Path.Combine(Path.GetTempPath(), "charter-agent-tests"),
        WorkDirectory = Path.Combine(Path.GetTempPath(), "charter-agent-tests", "work"),
        AutoUpdate = autoUpdate,
    };

    private static AgentSession NewSession(out AgentOptions options)
    {
        options = Options();
        return new AgentSession(options, Probed(), Now);
    }

    private static AgentSession Ready(int concurrency = 2)
    {
        var session = new AgentSession(Options(concurrency), Probed(), Now);
        session.Open(Now);
        session.Receive(Welcome(), Now);
        return session;
    }

    internal static Envelope Welcome(
        int? protocolVersion = null,
        AgentUpdateOffer? update = null) =>
        Envelope.Create(
            MessageTypes.Welcome,
            new WelcomePayload
            {
                AgentId = "agt_1",
                ProtocolVersion = protocolVersion ?? AgentProtocol.Version,
                SupportedProtocolVersions = [protocolVersion ?? AgentProtocol.Version],
                HeartbeatSeconds = 30,
                LeaseSeconds = 300,
                ClaimIntervalSeconds = 5,
                ReprobeSeconds = 86_400,
                Update = update,
            },
            Now);

    internal static JobAssignment Job(
        string id,
        IReadOnlyList<string> required,
        int leaseSeconds = 300,
        JobSecrets? secrets = null) =>
        new()
        {
            JobId = id,
            Type = "build",
            LeaseExpiresAt = Now.AddSeconds(leaseSeconds),
            RequiredCapabilities = required,
            RunnerImage = "ghcr.io/binn/charter-runner-fullstack:1",
            Command = new JobCommand { Executable = "charter-run", Arguments = ["--session", id] },
            Secrets = secrets,
        };

    internal static Envelope Grant(params JobAssignment[] jobs) =>
        Envelope.Create(MessageTypes.JobGrant, new JobGrantPayload { Jobs = jobs }, Now);

    private static Envelope EmptyGrant() =>
        Envelope.Create(MessageTypes.JobGrant, new JobGrantPayload(), Now);

    [Fact]
    public void EveryFrameRoundTripsThroughItsJsonForm()
    {
        var original = Grant(Job("job-1", ["linux"]));

        var json = original.ToJson();
        var parsed = Envelope.FromJson(json)!;

        Assert.Equal(original.Type, parsed.Type);
        Assert.Equal(original.Id, parsed.Id);
        Assert.Equal(AgentProtocol.Version, parsed.ProtocolVersion);
        Assert.Equal("job-1", parsed.ReadPayload<JobGrantPayload>()!.Jobs[0].JobId);
        Assert.Equal(JsonValueKind.Object, parsed.Payload!.Value.ValueKind);
    }
}
