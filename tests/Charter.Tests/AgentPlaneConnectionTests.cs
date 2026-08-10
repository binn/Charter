using Charter.Data;
using Charter.Domain;
using Charter.Runners.Agent;
using Microsoft.Extensions.DependencyInjection;

namespace Charter.Tests;

/// <summary>
/// The frame exchange: handshake, heartbeat, leases, revocation and close codes (sections 33.3,
/// 33.4, 33.6).
/// </summary>
[Collection(AgentPlaneCollection.Name)]
public class AgentPlaneConnectionTests
{
    [Fact]
    public async Task HelloIsAnsweredWithTheTimingContractAndTheAgentGoesOnline()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync();
        var (channel, run) = fixture.Connect(agentId);

        var welcome = await fixture.HandshakeAsync(channel, ["linux", "docker", "dotnet:10.0.100", fixture.Tag]);

        Assert.Equal(agentId.ToString("D"), welcome.AgentId);
        Assert.Equal(AgentProtocol.Version, welcome.ProtocolVersion);
        Assert.Equal(fixture.Options.HeartbeatSeconds, welcome.HeartbeatSeconds);
        Assert.Equal(fixture.Options.LeaseSeconds, welcome.LeaseSeconds);
        Assert.Equal(fixture.Options.ReprobeSeconds, welcome.ReprobeSeconds);
        Assert.Contains(AgentProtocol.Version, welcome.SupportedProtocolVersions);

        var agent = await fixture.AgentAsync(agentId);
        Assert.NotNull(agent);
        Assert.Equal(RunnerAgentStatus.Online, agent.Status);
        Assert.True(agent.IsOnlineAt(fixture.Clock.GetUtcNow()));

        // Section 32.2: probed capabilities, expanded on the way in.
        Assert.Contains("dotnet:10", agent.Capabilities);
        Assert.Contains("dotnet:10.0.100", agent.Capabilities);

        channel.Disconnect();
        await run;

        // The socket closing marks it offline; the leases it held survive until they lapse.
        var afterwards = await fixture.AgentAsync(agentId);
        Assert.Equal(RunnerAgentStatus.Offline, afterwards!.Status);
    }

    [Fact]
    public async Task AProtocolMismatchProducesTheMismatchFrameAndCloseCode4001()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync();
        var (channel, run) = fixture.Connect(agentId);

        // An agent from the future: it speaks only a version this plane has never heard of.
        channel.Send(fixture.Hello(protocolVersion: 99, supported: [99, 98]));

        var frame = await channel.ExpectAsync(MessageTypes.ProtocolMismatch);
        var payload = frame.ReadPayload<ProtocolMismatchPayload>();

        Assert.NotNull(payload);
        Assert.Equal(AgentProtocol.Version, payload.ServerProtocolVersion);
        Assert.Equal(AgentProtocol.SupportedVersions, payload.SupportedProtocolVersions);

        // Section 33.6: it names both versions and says which side to upgrade.
        Assert.Contains("99", payload.Message!, StringComparison.Ordinal);
        Assert.Contains("Upgrade the control plane", payload.Message!, StringComparison.Ordinal);

        await run;

        Assert.Equal(AgentProtocol.CloseProtocolMismatch, channel.CloseCode);
        Assert.Equal(4001, channel.CloseCode);

        // Refusing means refusing: nothing about the agent was written as if it had connected.
        var agent = await fixture.AgentAsync(agentId);
        Assert.Equal(RunnerAgentStatus.Offline, agent!.Status);
    }

    [Fact]
    public async Task AnAgentOneVersionAheadStillConnectsOnAVersionBothSidesSpeak()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync();
        var (channel, run) = fixture.Connect(agentId);

        // supportedProtocolVersions exists precisely so this is not a refusal.
        channel.Send(fixture.Hello(protocolVersion: 2, supported: [2, 1]));

        var welcome = (await channel.ExpectAsync(MessageTypes.Welcome)).ReadPayload<WelcomePayload>();
        Assert.Equal(AgentProtocol.Version, welcome!.ProtocolVersion);

        channel.Disconnect();
        await run;
    }

    [Fact]
    public async Task NothingIsGrantedBeforeTheHandshake()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync();
        await fixture.EnqueueClaimableAsync(Guid.CreateVersion7());

        var (channel, run) = fixture.Connect(agentId);

        channel.Send(Envelope.Create(
            MessageTypes.JobClaim,
            new JobClaimPayload { MaxJobs = 4, Capabilities = ["linux", fixture.Tag], Mode = "docker" },
            fixture.Clock.GetUtcNow()));

        var error = (await channel.ExpectAsync(MessageTypes.Error)).ReadPayload<ErrorPayload>();
        Assert.Equal(AgentErrorCodes.HandshakeRequired, error!.Code);

        channel.Disconnect();
        await run;
    }

    [Fact]
    public async Task AHeartbeatRenewsTheLeaseOnEveryJobTheAgentStillHolds()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync();
        var jobId = await fixture.EnqueueClaimableAsync(Guid.CreateVersion7());

        var (channel, run) = fixture.Connect(agentId);
        await fixture.HandshakeAsync(channel);

        var granted = await ClaimOneAsync(fixture, channel);
        Assert.Equal(jobId.ToString("D"), granted.JobId);

        var before = (await fixture.JobAsync(jobId))!.LeaseExpiresAt;

        // Most of the way through the lease and not past it: the job is still legitimately held, and
        // without this heartbeat the sweep would take it back shortly.
        fixture.Clock.Advance(fixture.Options.Lease - TimeSpan.FromSeconds(5));

        channel.Send(Envelope.Create(
            MessageTypes.Heartbeat,
            new HeartbeatPayload
            {
                Status = "busy",
                HeldJobIds = [jobId.ToString("D")],
                AvailableSlots = 1,
                CapabilitiesHash = "hash-1",
            },
            fixture.Clock.GetUtcNow()));

        var ack = (await channel.ExpectAsync(MessageTypes.HeartbeatAck)).ReadPayload<HeartbeatAckPayload>();

        Assert.NotNull(ack);
        var lease = Assert.Single(ack.Leases);
        Assert.Equal(jobId.ToString("D"), lease.JobId);
        Assert.Equal(fixture.Clock.GetUtcNow() + fixture.Options.Lease, lease.LeaseExpiresAt);

        var after = (await fixture.JobAsync(jobId))!.LeaseExpiresAt;
        Assert.True(after > before, "The heartbeat must have pushed the lease out.");
        Assert.Equal(JobStatus.Claimed, (await fixture.JobAsync(jobId))!.Status);

        channel.Disconnect();
        await run;
    }

    [Fact]
    public async Task AHeartbeatDoesNotRenewAJobTheAgentHasAlreadyLost()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync();
        var jobId = await fixture.EnqueueClaimableAsync(Guid.CreateVersion7());

        var (channel, run) = fixture.Connect(agentId);
        await fixture.HandshakeAsync(channel);
        await ClaimOneAsync(fixture, channel);

        // The lease lapses and the sweep hands the work back to the queue (section 33.4).
        fixture.Clock.Advance(fixture.Options.Lease + TimeSpan.FromSeconds(1));

        var released = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<JobQueue>().ReleaseExpiredLeasesAsync(
                fixture.Clock.GetUtcNow(),
                TestContext.Current.CancellationToken));

        Assert.True(released >= 1);
        Assert.Equal(JobStatus.Pending, (await fixture.JobAsync(jobId))!.Status);

        channel.Send(Envelope.Create(
            MessageTypes.Heartbeat,
            new HeartbeatPayload
            {
                Status = "busy",
                HeldJobIds = [jobId.ToString("D")],
                AvailableSlots = 1,
                CapabilitiesHash = "hash-1",
            },
            fixture.Clock.GetUtcNow()));

        var ack = (await channel.ExpectAsync(MessageTypes.HeartbeatAck)).ReadPayload<HeartbeatAckPayload>();

        // Absent from the ack is how the agent is told to stop. Renewing it would leave two runners
        // pushing to one branch.
        Assert.NotNull(ack);
        Assert.Empty(ack.Leases);

        channel.Disconnect();
        await run;
    }

    [Fact]
    public async Task AChangedCapabilityHashAsksForAFreshProbe()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync();
        var (channel, run) = fixture.Connect(agentId);
        await fixture.HandshakeAsync(channel);

        var first = await HeartbeatAsync(fixture, channel, "hash-1");
        Assert.False(first.ReprobeRequested);

        var second = await HeartbeatAsync(fixture, channel, "hash-1");
        Assert.False(second.ReprobeRequested);

        // Section 32.2: a Mac mini that got an Xcode update must not keep advertising the old one.
        var third = await HeartbeatAsync(fixture, channel, "hash-2");
        Assert.True(third.ReprobeRequested);

        channel.Send(Envelope.Create(
            MessageTypes.CapabilitiesReport,
            new CapabilitiesReportPayload
            {
                Capabilities = ["linux", "docker", "xcode:16.2", fixture.Tag],
                ProbedAt = fixture.Clock.GetUtcNow(),
                CapabilitiesHash = "hash-2",
            },
            fixture.Clock.GetUtcNow()));

        var fourth = await HeartbeatAsync(fixture, channel, "hash-2");
        Assert.False(fourth.ReprobeRequested);

        var agent = await fixture.AgentAsync(agentId);
        Assert.Contains("xcode:16.2", agent!.Capabilities);
        Assert.Equal("hash-2", agent.CapabilitiesHash);

        channel.Disconnect();
        await run;
    }

    [Fact]
    public async Task RevokingKillsInFlightWorkSendsTheFrameAndClosesWith4003()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync();
        var jobId = await fixture.EnqueueClaimableAsync(Guid.CreateVersion7());

        var (channel, run) = fixture.Connect(agentId);
        await fixture.HandshakeAsync(channel);
        await ClaimOneAsync(fixture, channel);

        Assert.Equal(JobStatus.Claimed, (await fixture.JobAsync(jobId))!.Status);

        await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<AgentPlaneService>()
                .RevokeAsync(agentId, "Revoked from the UI.", TestContext.Current.CancellationToken));

        var revoked = (await channel.ExpectAsync(MessageTypes.Revoked)).ReadPayload<RevokedPayload>();
        Assert.Equal("Revoked from the UI.", revoked!.Reason);

        await run;

        Assert.Equal(AgentProtocol.CloseCredentialRevoked, channel.CloseCode);
        Assert.Equal(4003, channel.CloseCode);

        // All three halves of section 33.3: the frame, the in-flight work, and the credential.
        Assert.Equal(JobStatus.Pending, (await fixture.JobAsync(jobId))!.Status);

        var agent = await fixture.AgentAsync(agentId);
        Assert.Equal(RunnerAgentStatus.Revoked, agent!.Status);
        Assert.Null(agent.CredentialHash);
    }

    [Fact]
    public async Task ASecondConnectionForOneAgentDisplacesTheFirstWith4008()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync();

        var (first, firstRun) = fixture.Connect(agentId);
        await fixture.HandshakeAsync(first);

        var (second, secondRun) = fixture.Connect(agentId);
        await fixture.HandshakeAsync(second);

        await firstRun;

        // Two sockets for one agent would both claim under the same worker identity and each would
        // renew the other's leases. The older one loses.
        Assert.Equal(AgentProtocol.CloseReplaced, first.CloseCode);
        Assert.Equal(4008, first.CloseCode);
        Assert.True(fixture.Connections.IsConnected(agentId));

        second.Disconnect();
        await secondRun;

        Assert.False(fixture.Connections.IsConnected(agentId));
    }

    [Fact]
    public async Task GoodbyeHandsEveryLeaseBackRatherThanMakingTheQueueWaitItOut()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync();
        var jobId = await fixture.EnqueueClaimableAsync(Guid.CreateVersion7());

        var (channel, run) = fixture.Connect(agentId);
        await fixture.HandshakeAsync(channel);
        await ClaimOneAsync(fixture, channel);

        channel.Send(Envelope.Create(
            MessageTypes.Goodbye,
            new GoodbyePayload { Reason = "charter-agent shutting down", ReleasedJobIds = [jobId.ToString("D")] },
            fixture.Clock.GetUtcNow()));

        await run;

        var job = await fixture.JobAsync(jobId);
        Assert.Equal(JobStatus.Pending, job!.Status);
        Assert.Null(job.ClaimedBy);
    }

    [Fact]
    public async Task LeasesSurviveAReconnectSoAHalfFinishedBuildIsNotLost()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync();
        var jobId = await fixture.EnqueueClaimableAsync(Guid.CreateVersion7());

        var (first, firstRun) = fixture.Connect(agentId);
        await fixture.HandshakeAsync(first);
        await ClaimOneAsync(fixture, first);

        // The socket drops. The lease does not: the plane holds it until the TTL lapses.
        first.Disconnect();
        await firstRun;

        fixture.Clock.Advance(fixture.Options.Lease - TimeSpan.FromSeconds(5));
        var beforeReconnect = (await fixture.JobAsync(jobId))!.LeaseExpiresAt;

        var (second, secondRun) = fixture.Connect(agentId);
        await fixture.HandshakeAsync(second, heldJobIds: [jobId.ToString("D")]);

        var job = await fixture.JobAsync(jobId);
        Assert.Equal(JobStatus.Claimed, job!.Status);
        Assert.Equal(AgentRunner.WorkerIdFor(agentId), job.ClaimedBy);
        Assert.True(job.LeaseExpiresAt > beforeReconnect, "Reconnecting must renew what the agent still holds.");

        second.Disconnect();
        await secondRun;
    }

    [Fact]
    public async Task AnUnknownFrameIsIgnoredRatherThanFatal()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync();
        var (channel, run) = fixture.Connect(agentId);
        await fixture.HandshakeAsync(channel);

        // A control plane one minor version behind an agent must not drop the socket over a frame it
        // has no record for.
        channel.Send(Envelope.Create(
            "something.from.the.future",
            new { anything = true },
            fixture.Clock.GetUtcNow()));

        var ack = await HeartbeatAsync(fixture, channel, "hash-1");
        Assert.NotNull(ack);

        channel.Disconnect();
        await run;
    }

    internal static async Task<JobAssignment> ClaimOneAsync(AgentPlaneFixture fixture, LoopbackAgentChannel channel)
    {
        channel.Send(Envelope.Create(
            MessageTypes.JobClaim,
            new JobClaimPayload
            {
                MaxJobs = 4,
                Capabilities = ["linux", "docker", fixture.Tag],
                Mode = "docker",
            },
            fixture.Clock.GetUtcNow()));

        var grant = (await channel.ExpectAsync(MessageTypes.JobGrant)).ReadPayload<JobGrantPayload>();
        Assert.NotNull(grant);

        return Assert.Single(grant.Jobs);
    }

    private static async Task<HeartbeatAckPayload> HeartbeatAsync(
        AgentPlaneFixture fixture,
        LoopbackAgentChannel channel,
        string capabilitiesHash)
    {
        channel.Send(Envelope.Create(
            MessageTypes.Heartbeat,
            new HeartbeatPayload
            {
                Status = "ready",
                HeldJobIds = [],
                AvailableSlots = 2,
                CapabilitiesHash = capabilitiesHash,
            },
            fixture.Clock.GetUtcNow()));

        return (await channel.ExpectAsync(MessageTypes.HeartbeatAck)).ReadPayload<HeartbeatAckPayload>()!;
    }
}
