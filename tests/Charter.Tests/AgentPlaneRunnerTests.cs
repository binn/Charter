using Charter.Auth;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Charter.Runners;
using Charter.Runners.Agent;
using Microsoft.Extensions.DependencyInjection;

namespace Charter.Tests;

/// <summary>
/// <see cref="AgentRunner"/> behind the <see cref="IAgentRunner"/> seam (sections 2.2, 33.4).
/// </summary>
/// <remarks>
/// The one property worth stating twice: dispatching does not push. The control plane cannot reach an
/// agent behind NAT, so what a dispatch does is make a row claimable and wait to be asked.
/// </remarks>
[Collection(AgentPlaneCollection.Name)]
public class AgentPlaneRunnerTests
{
    /// <summary>
    /// A dispatch, tagged so its queue row cannot be claimed by another test class's agent.
    /// </summary>
    /// <remarks>
    /// The <c>jobs</c> table is shared with every other integration test, so a required capability
    /// nobody else advertises is how a row stays private — the same trick <c>DataJobQueueTests</c>
    /// uses. It is also a faithful use of section 27.3 rather than a test-only escape hatch.
    /// </remarks>
    private static RunnerDispatch DispatchFor(AgentPlaneFixture fixture, Guid sessionId, params string[] required) =>
        DispatchFor(sessionId, [.. required, fixture.Tag]);

    private static RunnerDispatch DispatchFor(Guid sessionId, params string[] required) =>
        new(
            sessionId,
            "acme/widgets",
            "main",
            "a3f9c21",
            "claude-code",
            "openrouter/deepseek/deepseek-r1",
            null,
            new Uri($"https://charter.test/api/runners/sessions/{sessionId:D}"),
            new Uri($"https://charter.test/api/runners/sessions/{sessionId:D}/spec"),
            new RunnerPathScope(["src/**"], ["infra/**"]),
            required,
            60,
            $"dispatch:{sessionId:D}");

    [Fact]
    public void TheWorkerIdentityRoundTripsSoAClaimedByColumnNamesTheAgent()
    {
        var agentId = Guid.CreateVersion7();

        Assert.Equal($"agent:{agentId:D}", AgentRunner.WorkerIdFor(agentId));
        Assert.Equal(agentId, AgentRunner.AgentIdFrom(AgentRunner.WorkerIdFor(agentId)));

        // A control-plane replica's own worker id must not read as an agent.
        Assert.Null(AgentRunner.AgentIdFrom("charter-1234"));
        Assert.Null(AgentRunner.AgentIdFrom(null));
    }

    [Fact]
    public void TheExternalReferenceRoundTripsSoARestartedPlaneCanStillCancel()
    {
        var jobId = Guid.CreateVersion7();
        var reference = AgentRunner.ExternalReferenceFor(jobId);

        Assert.Equal(jobId, AgentRunner.TryParseReference(reference));
        Assert.Null(AgentRunner.TryParseReference("https://github.com/acme/widgets/actions/runs/1"));
        Assert.Null(AgentRunner.TryParseReference(null));
    }

    [Fact]
    public void TheQueueRowIdIsAPureFunctionOfTheDispatchKey()
    {
        // Which is what makes a second dispatch a primary key violation rather than a second unit of
        // work - across a restart, and across two replicas (section 2.3).
        var key = $"dispatch:{Guid.CreateVersion7():D}";

        Assert.Equal(AgentRunner.SessionJournalId(key), AgentRunner.SessionJournalId(key));
        Assert.NotEqual(AgentRunner.SessionJournalId(key), AgentRunner.SessionJournalId(key + "x"));
    }

    [Fact]
    public async Task DispatchMarksTheWorkClaimableRatherThanPushingIt()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var sessionId = Guid.CreateVersion7();
        var dispatch = DispatchFor(fixture, sessionId, "linux", "dotnet:10");

        var result = await fixture.Runner.DispatchAsync(dispatch, TestContext.Current.CancellationToken);

        Assert.True(result.Accepted);
        var jobId = AgentRunner.TryParseReference(result.ExternalReference);
        Assert.NotNull(jobId);

        var job = await fixture.JobAsync(jobId.Value);
        Assert.NotNull(job);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Null(job.ClaimedBy);

        // The marker plus the session's own requirements, expanded.
        Assert.Contains(AgentRunner.ClaimCapability, job.RequiredCapabilities);
        Assert.Contains("linux", job.RequiredCapabilities);
        Assert.Contains("dotnet:10", job.RequiredCapabilities);

        var payload = AgentJobPayload.TryParse(job.Payload);
        Assert.NotNull(payload);
        Assert.Equal(sessionId, payload.SessionId);
        Assert.Equal("acme/widgets", payload.RepoFullName);
        Assert.Equal(["src/**"], payload.AllowPaths);
        Assert.Equal(["infra/**"], payload.DenyPaths);
        Assert.Equal(60, payload.TimeoutMinutes);
    }

    [Fact]
    public async Task DispatchingTwiceUnderOneKeyProducesOneUnitOfWork()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var dispatch = DispatchFor(fixture, Guid.CreateVersion7(), "linux");

        var first = await fixture.Runner.DispatchAsync(dispatch, TestContext.Current.CancellationToken);
        var second = await fixture.Runner.DispatchAsync(dispatch, TestContext.Current.CancellationToken);

        Assert.True(first.Accepted);
        Assert.True(second.Accepted);
        Assert.Equal(first.ExternalReference, second.ExternalReference);

        var count = await fixture.InScopeAsync(async provider =>
            await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(
                provider.GetRequiredService<Charter.Data.CharterDbContext>().Jobs,
                job => job.Id == AgentRunner.TryParseReference(first.ExternalReference),
                TestContext.Current.CancellationToken));

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task DescribeAdvertisesOnlineAgentsOnlyAndExplainsAnOfflineOne()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        // Assertions are keyed on this fixture's own capability tag rather than on the descriptor's
        // Online flag, because AgentRunner advertises every online agent on the instance - the
        // IAgentRunner seam carries no organisation - so another test class's agent would legitimately
        // show up here.
        var empty = await fixture.Runner.DescribeAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(fixture.Tag, empty.Capabilities);

        var (agentId, _) = await fixture.PairAsync("mac-mini", ["macos", "xcode:16.2", fixture.Tag]);

        // Paired but never connected: registered, and still not routable.
        var paired = await fixture.Runner.DescribeAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(fixture.Tag, paired.Capabilities);

        var (channel, run) = fixture.Connect(agentId);
        await fixture.HandshakeAsync(channel, ["macos", "xcode:16.2", fixture.Tag]);

        var online = await fixture.Runner.DescribeAsync(TestContext.Current.CancellationToken);
        Assert.True(online.Online);
        Assert.Contains("mac-mini", online.Name, StringComparison.Ordinal);
        Assert.Contains(fixture.Tag, online.Capabilities);
        Assert.Contains("xcode:16", online.Capabilities);
        Assert.True(online.CanRun([fixture.Tag, "macos", "xcode:16"]));
        Assert.False(online.CanRun([fixture.Tag, "macos", "xcode:17"]));

        channel.Disconnect();
        await run;

        // Section 33.3: a runner that stops heartbeating stops being routed to. The descriptor still
        // names the registered agents, because "your Mac mini is offline" beats "no runner has macOS".
        var afterwards = await fixture.Runner.DescribeAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(fixture.Tag, afterwards.Capabilities);
    }

    [Fact]
    public async Task AnAgentThatStoppedHeartbeatingStopsAdvertising()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync("mac-mini", ["macos", fixture.Tag]);
        var (channel, run) = fixture.Connect(agentId);
        await fixture.HandshakeAsync(channel, ["macos", fixture.Tag]);

        Assert.Contains(
            fixture.Tag,
            (await fixture.Runner.DescribeAsync(TestContext.Current.CancellationToken)).Capabilities);

        // The status column still says online - the control plane was killed before it could write
        // the disconnect. The heartbeat window is what covers exactly that case.
        fixture.Clock.Advance(RunnerAgent.HeartbeatGrace + TimeSpan.FromMinutes(1));

        Assert.DoesNotContain(
            fixture.Tag,
            (await fixture.Runner.DescribeAsync(TestContext.Current.CancellationToken)).Capabilities);

        channel.Disconnect();
        await run;
    }

    [Fact]
    public async Task CancellingQueuedWorkTakesItOutOfTheQueue()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var sessionId = Guid.CreateVersion7();
        var dispatched = await fixture.Runner.DispatchAsync(
            DispatchFor(fixture, sessionId, "linux"),
            TestContext.Current.CancellationToken);

        var cancelled = await fixture.Runner.CancelAsync(
            new RunnerCancellation(sessionId, dispatched.ExternalReference, "Cancelled by request."),
            TestContext.Current.CancellationToken);

        Assert.True(cancelled.Stopped);

        var job = await fixture.JobAsync(AgentRunner.TryParseReference(dispatched.ExternalReference)!.Value);
        Assert.Equal(JobStatus.Cancelled, job!.Status);
    }

    [Fact]
    public async Task CancellingInFlightWorkPushesTheCancelDownTheAgentsOwnSocket()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync();
        var sessionId = Guid.CreateVersion7();
        var jobId = await fixture.EnqueueClaimableAsync(sessionId);

        var (channel, run) = fixture.Connect(agentId);
        await fixture.HandshakeAsync(channel);
        await AgentPlaneConnectionTests.ClaimOneAsync(fixture, channel);

        // The session id is the job's own. A reference naming a job that belongs to a different
        // session is not acted on — the reference is folded from the session's event stream and
        // session_started arrives from the execution plane (see RunnerRunReferenceTests).
        var result = await fixture.Runner.CancelAsync(
            new RunnerCancellation(sessionId, AgentRunner.ExternalReferenceFor(jobId), "Cancelled by request."),
            TestContext.Current.CancellationToken);

        Assert.True(result.Stopped);

        // Section 11: the cancel button has to reach the runner, and outbound-only means it can only
        // travel on a socket the agent opened.
        var cancel = (await channel.ExpectAsync(MessageTypes.JobCancel)).ReadPayload<JobCancelPayload>();
        Assert.Equal(jobId.ToString("D"), cancel!.JobId);
        Assert.Equal("Cancelled by request.", cancel.Reason);

        channel.Disconnect();
        await run;
    }

    [Fact]
    public async Task CancellingWithoutAReferenceFindsTheAgentsRowAndNotTheControlPlanes()
    {
        // Its own schema: the control plane row below requires no capabilities, and a row requiring
        // nothing is claimable by every worker in the suite.
        await using var fixture = await AgentPlaneFixture.CreateAsync(isolated: true);
        if (fixture is null)
        {
            return;
        }

        var sessionId = Guid.CreateVersion7();

        // Both rows exist at once, which is ordinary rather than exotic: the dispatcher re-enqueues a
        // pending control-plane build every time a dispatch defers, and its payload carries the same
        // session id. Written first, so a lookup that does not filter by plane finds it first.
        var controlPlaneJob = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<JobQueue>().EnqueueAsync(
                JobType.Build,
                $$"""{"session_id":"{{sessionId:D}}"}""",
                now: fixture.Clock.GetUtcNow(),
                cancellationToken: TestContext.Current.CancellationToken));

        var dispatched = await fixture.Runner.DispatchAsync(
            DispatchFor(fixture, sessionId, "linux"),
            TestContext.Current.CancellationToken);

        var agentJobId = AgentRunner.TryParseReference(dispatched.ExternalReference)!.Value;

        // No external reference, so the fallback runs. Section 11: cancelling the wrong row and
        // reporting success is worse than failing, because the session settles as cancelled while the
        // agent holding the real claim was never told.
        var cancelled = await fixture.Runner.CancelAsync(
            new RunnerCancellation(sessionId, null, "Cancelled by request."),
            TestContext.Current.CancellationToken);

        Assert.True(cancelled.Stopped);

        Assert.Equal(JobStatus.Cancelled, (await fixture.JobAsync(agentJobId))!.Status);
        Assert.Equal(JobStatus.Pending, (await fixture.JobAsync(controlPlaneJob.Id))!.Status);
    }

    [Fact]
    public async Task CancellingWillNotStopAJobThatBelongsToAnotherSession()
    {
        // Its own schema: two agent jobs exist at once here, and a row requiring no capabilities is
        // claimable by every worker in the suite.
        await using var fixture = await AgentPlaneFixture.CreateAsync(isolated: true);
        if (fixture is null)
        {
            return;
        }

        var victim = Guid.CreateVersion7();
        var victimJob = await fixture.EnqueueClaimableAsync(victim);

        // The attacker's session, with a reference naming somebody else's job. The reference reaches
        // the runner from the session's event stream, and `session_started` arrives from the execution
        // plane, so `charter-agent:job:<victim>` is a string a sandbox can put there (section 16).
        var attacker = Guid.CreateVersion7();
        await fixture.EnqueueClaimableAsync(attacker);

        var result = await fixture.Runner.CancelAsync(
            new RunnerCancellation(
                attacker,
                AgentRunner.ExternalReferenceFor(victimJob),
                "Cancelled by request."),
            TestContext.Current.CancellationToken);

        // The victim's work is untouched, and what was cancelled is the attacker's own row — which is
        // the only honest reading of "cancel this session".
        Assert.Equal(JobStatus.Pending, (await fixture.JobAsync(victimJob))!.Status);
        Assert.True(result.Stopped);
    }

    [Fact]
    public async Task CancellingSomethingThatWasNeverDispatchedSaysSoRatherThanFailing()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var result = await fixture.Runner.CancelAsync(
            new RunnerCancellation(Guid.CreateVersion7(), null, "Cancelled by request."),
            TestContext.Current.CancellationToken);

        Assert.False(result.Stopped);
        Assert.NotNull(result.Explanation);
    }

    [Fact]
    public async Task TheRegistryRoutesToTheAgentByCapabilityMatch()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync("mac-mini", ["macos", "xcode:16.2", fixture.Tag]);
        var (channel, run) = fixture.Connect(agentId);
        await fixture.HandshakeAsync(channel, ["macos", "xcode:16.2", fixture.Tag]);

        var registry = new RunnerRegistry([fixture.Runner]);

        var routed = await registry.RouteAsync(
            [fixture.Tag, "macos", "xcode:16"],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(routed.IsRoutable);
        Assert.Equal(RunnerKind.Agent, routed.Runner!.Kind);

        // Section 27.3: no eligible runner queues with an explanation naming the capability in human
        // words, rather than failing.
        var unroutable = await registry.RouteAsync(
            [fixture.Tag, "windows"],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(unroutable.IsRoutable);
        Assert.Contains("Windows", unroutable.Explanation!, StringComparison.Ordinal);
        Assert.Contains(RunnerRegistry.RegisterHint, unroutable.Explanation!, StringComparison.Ordinal);
        Assert.Equal(["windows"], unroutable.Missing);

        // And the marker never reaches the dispatcher's claim filter.
        var advertised = await registry.AdvertisedCapabilitiesAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(AgentRunner.ClaimCapability, advertised);
        Assert.Contains("macos", advertised);

        channel.Disconnect();
        await run;
    }

    [Fact]
    public async Task TheMappedRoutesAreTheOnesTheDaemonDials()
    {
        var config = ConfigTestEnvironment.Valid();

        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.Services.AddLogging();
        builder.Services.AddCharterConfig(config);
        builder.Services.AddCharterData(config.Database.ConnectionString.Reveal());

        // The admin routes resolve the acting member through the same authorisation service the rest
        // of the API uses, so the graph has to include it or minimal APIs infer it as a request body.
        builder.Services.AddCharterAuth();
        builder.Services.AddSingleton(new RunnerSessionTokens("charter-test-secret-key-0123456789"));
        builder.Services.AddCharterAgentPlane();

        await using var app = builder.Build();
        app.MapCharterAgentPlane();

        // WebApplication's own data source, not the composite one in the container: routes only join
        // the composite when the host starts, and starting a host to read a route table is a waste.
        var routes = ((Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Select(endpoint => "/" + endpoint.RoutePattern.RawText!.TrimStart('/'))
            .ToArray();

        // charter-agent builds these from its own constants and dials them literally. A trailing
        // slash or a renamed segment here is an agent that cannot pair, discovered by an operator
        // rather than by the build.
        Assert.Contains(Charter.Agent.Protocol.AgentProtocol.PairPath, routes);
        Assert.Contains(Charter.Agent.Protocol.AgentProtocol.ConnectPath, routes);
        Assert.Contains("/api/agent/agents", routes);
        Assert.Contains("/api/agent/agents/{agentId:guid}/revoke", routes);
    }

    [Fact]
    public async Task TheAgentBackendIsRegisteredBehindTheSeamWhenCharterRunnerAsksForIt()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCharterData("Host=localhost;Database=charter;Username=charter;Password=charter");
        services.AddSingleton(new RunnerSessionTokens("charter-test-secret-key-0123456789"));
        services.AddCharterAgentPlane();

        await using var provider = services.BuildServiceProvider();

        var runner = Assert.Single(provider.GetServices<IAgentRunner>());
        Assert.Equal(RunnerKind.Agent, runner.Kind);

        // The pairing endpoint's dependencies have to resolve, or the failure shows up on the first
        // request after a deploy rather than at the build.
        await using var scope = provider.CreateAsyncScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AgentPlaneService>());
        Assert.NotNull(provider.GetRequiredService<AgentConnectionRegistry>());
        Assert.NotNull(provider.GetRequiredService<AgentCredentialMint>());
    }
}
