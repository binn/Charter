using Charter.Data;
using Charter.Domain;
using Charter.Runners;
using Charter.Runners.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Charter.Tests;

/// <summary>
/// Claiming: capability filtering, leases, and the per-job secrets (sections 33.4, 33.5, 27.3).
/// </summary>
/// <remarks>
/// The claim is where the two hardest promises of section 33 are kept. The agent only ever sees jobs
/// it can actually run — which has to be a property of the queue, not a check somebody remembered to
/// write — and the credentials it receives are minted in the moment rather than parked in a row for
/// as long as the backlog lasts.
/// </remarks>
[Collection(AgentPlaneCollection.Name)]
public class AgentPlaneClaimTests
{
    [Fact]
    public async Task AnAgentIsNeverGrantedAJobItCannotRun()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync();

        var runnable = await fixture.EnqueueClaimableAsync(Guid.CreateVersion7(), ["linux", "dotnet:10"]);
        var impossible = await fixture.EnqueueClaimableAsync(Guid.CreateVersion7(), ["macos", "xcode:16"]);

        var (channel, run) = fixture.Connect(agentId);
        await fixture.HandshakeAsync(channel, ["linux", "docker", "dotnet:10.0.100", fixture.Tag]);

        channel.Send(Envelope.Create(
            MessageTypes.JobClaim,
            new JobClaimPayload
            {
                MaxJobs = 8,
                Capabilities = ["linux", "docker", "dotnet:10.0.100", fixture.Tag],
                Mode = "docker",
            },
            fixture.Clock.GetUtcNow()));

        var grant = (await channel.ExpectAsync(MessageTypes.JobGrant)).ReadPayload<JobGrantPayload>();
        Assert.NotNull(grant);

        var granted = Assert.Single(grant.Jobs);
        Assert.Equal(runnable.ToString("D"), granted.JobId);

        // Section 27.3: the macOS job was never locked, never returned, and never had to be handed
        // back. It simply waits, which is what "queues rather than fails" means on the queue.
        Assert.Equal(JobStatus.Pending, (await fixture.JobAsync(impossible))!.Status);

        // A coarse requirement is satisfied by a precise advertisement, because both were expanded.
        Assert.Contains("dotnet:10", granted.RequiredCapabilities);

        // And the routing marker is stripped: it is not something an operator can install.
        Assert.DoesNotContain(AgentRunner.ClaimCapability, granted.RequiredCapabilities);

        channel.Disconnect();
        await run;
    }

    [Fact]
    public async Task TheGrantCarriesTheTwoCredentialsAndTheRowCarriesNeither()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync();
        var sessionId = Guid.CreateVersion7();
        var jobId = await fixture.EnqueueClaimableAsync(sessionId, repo: "acme/widgets");

        var (channel, run) = fixture.Connect(agentId);
        await fixture.HandshakeAsync(channel);

        var granted = await AgentPlaneConnectionTests.ClaimOneAsync(fixture, channel);

        // Section 33.5: exactly two slots. A single-repo version-control token and a scoped model
        // credential, and nothing else.
        Assert.NotNull(granted.Secrets);
        Assert.Equal(StubRunnerCredentialBroker.GitHubToken, granted.Secrets.GitHub!.Token);
        Assert.Equal("acme/widgets", granted.Secrets.GitHub.Repository);
        Assert.Equal(StubRunnerCredentialBroker.ModelApiKey, granted.Secrets.Model!.ApiKey);
        Assert.Equal("openrouter", granted.Secrets.Model.Provider);
        Assert.True(granted.Secrets.GitHub.ExpiresAt > fixture.Clock.GetUtcNow());

        // Never a credential for another repository (section 7.4).
        Assert.Equal(["acme/widgets"], fixture.Broker.Issued);

        // Minted at claim time. A queued row is a live credential sitting in the database for as long
        // as the backlog lasts, which is why the payload has no slot for one.
        var row = await fixture.JobAsync(jobId);
        Assert.NotNull(row);
        Assert.DoesNotContain(StubRunnerCredentialBroker.GitHubToken, row.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain(StubRunnerCredentialBroker.ModelApiKey, row.Payload, StringComparison.Ordinal);

        Assert.NotNull(AgentJobPayload.TryParse(row.Payload));
        Assert.Equal(sessionId, AgentJobPayload.TryParse(row.Payload)!.SessionId);

        channel.Disconnect();
        await run;
    }

    [Fact]
    public async Task NoSecretAppearsInAnyPersistedRowAnywhere()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, token) = await fixture.PairAsync();
        var jobId = await fixture.EnqueueClaimableAsync(Guid.CreateVersion7());

        var (channel, run) = fixture.Connect(agentId);
        await fixture.HandshakeAsync(channel);
        await AgentPlaneConnectionTests.ClaimOneAsync(fixture, channel);

        var secrets = new[]
        {
            StubRunnerCredentialBroker.GitHubToken,
            StubRunnerCredentialBroker.ModelApiKey,
            token,
        };

        await fixture.InScopeAsync(async provider =>
        {
            var db = provider.GetRequiredService<CharterDbContext>();

            var job = await db.Jobs.AsNoTracking()
                .FirstAsync(candidate => candidate.Id == jobId, TestContext.Current.CancellationToken);

            var agent = await db.RunnerAgents.AsNoTracking()
                .FirstAsync(candidate => candidate.Id == agentId, TestContext.Current.CancellationToken);

            var written = string.Join(
                "\n",
                job.Payload,
                job.ClaimedBy,
                job.LastError,
                string.Join(",", job.RequiredCapabilities),
                agent.Name,
                agent.CredentialHash,
                agent.PairingTokenHash,
                agent.CapabilitiesHash,
                string.Join(",", agent.Capabilities));

            foreach (var secret in secrets)
            {
                Assert.DoesNotContain(secret, written, StringComparison.Ordinal);
            }
        });

        channel.Disconnect();
        await run;
    }

    [Fact]
    public async Task AnExpiredLeaseReturnsTheJobToTheQueue()
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
        await AgentPlaneConnectionTests.ClaimOneAsync(fixture, channel);

        Assert.Equal(JobStatus.Claimed, (await fixture.JobAsync(jobId))!.Status);

        // The agent crashed. Nothing tells the control plane; the lease simply stops being renewed.
        // Section 33.4 is what makes outbound-only safe: the plane never needs to reach the worker to
        // discover that it is gone.
        fixture.Clock.Advance(fixture.Options.Lease + TimeSpan.FromSeconds(1));

        await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<JobQueue>().ReleaseExpiredLeasesAsync(
                fixture.Clock.GetUtcNow(),
                TestContext.Current.CancellationToken));

        var job = await fixture.JobAsync(jobId);
        Assert.Equal(JobStatus.Pending, job!.Status);
        Assert.Null(job.ClaimedBy);
        Assert.Null(job.LeaseExpiresAt);

        // And it is claimable again, by this agent or another one.
        var reclaimed = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<JobQueue>().ClaimAsync(
                "another-worker",
                fixture.Options.Lease,
                4,
                [AgentRunner.ClaimCapability, fixture.Tag],
                AgentRunner.ClaimCapability,
                fixture.Clock.GetUtcNow(),
                TestContext.Current.CancellationToken));

        Assert.Equal(jobId, Assert.Single(reclaimed).Id);

        channel.Disconnect();
        await run;
    }

    [Fact]
    public async Task TheControlPlanesOwnDispatcherNeverClaimsAnAgentJob()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var jobId = await fixture.EnqueueClaimableAsync(Guid.CreateVersion7());

        // The dispatcher claims with the union of what the registered runners advertise. Without the
        // routing marker it would claim this row straight back and dispatch the session a second
        // time - so the marker has to be absent from every runner's advertisement.
        var advertised = new List<string> { "linux", "docker", "dotnet:10", "macos", "xcode:16", fixture.Tag };

        var claimed = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<JobQueue>().ClaimAsync(
                "control-plane-replica-1",
                fixture.Options.Lease,
                8,
                advertised,
                now: fixture.Clock.GetUtcNow(),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.DoesNotContain(claimed, job => job.Id == jobId);
        Assert.Equal(JobStatus.Pending, (await fixture.JobAsync(jobId))!.Status);

        var descriptor = await fixture.Runner.DescribeAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(AgentRunner.ClaimCapability, descriptor.Capabilities);
    }

    [Fact]
    public async Task AnAgentNeverHoldsMoreThanItsConcurrency()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync(concurrency: 2);

        for (var index = 0; index < 4; index++)
        {
            await fixture.EnqueueClaimableAsync(Guid.CreateVersion7());
        }

        var (channel, run) = fixture.Connect(agentId);
        await fixture.HandshakeAsync(channel, concurrency: 2);

        // The agent asks for what it has room for; the plane never pushes more (section 33.4).
        channel.Send(Envelope.Create(
            MessageTypes.JobClaim,
            new JobClaimPayload { MaxJobs = 2, Capabilities = ["linux", "docker", fixture.Tag], Mode = "docker" },
            fixture.Clock.GetUtcNow()));

        var grant = (await channel.ExpectAsync(MessageTypes.JobGrant)).ReadPayload<JobGrantPayload>();
        Assert.Equal(2, grant!.Jobs.Count);

        channel.Disconnect();
        await run;
    }

    [Fact]
    public async Task AnEmptyQueueAnswersWithARetryHintRatherThanSilence()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync();
        var (channel, run) = fixture.Connect(agentId);
        await fixture.HandshakeAsync(channel);

        channel.Send(Envelope.Create(
            MessageTypes.JobClaim,
            new JobClaimPayload { MaxJobs = 2, Capabilities = ["linux", fixture.Tag], Mode = "docker" },
            fixture.Clock.GetUtcNow()));

        var grant = (await channel.ExpectAsync(MessageTypes.JobGrant)).ReadPayload<JobGrantPayload>();

        Assert.NotNull(grant);
        Assert.Empty(grant.Jobs);
        Assert.Equal(fixture.Options.EmptyClaimRetryAfterSeconds, grant.RetryAfterSeconds);

        channel.Disconnect();
        await run;
    }

    [Fact]
    public async Task AJobThatCannotBeGivenCredentialsGoesBackRatherThanRunningWithout()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync();
        var jobId = await fixture.EnqueueClaimableAsync(Guid.CreateVersion7());

        fixture.Broker.Throw = true;

        var (channel, run) = fixture.Connect(agentId);
        await fixture.HandshakeAsync(channel);

        channel.Send(Envelope.Create(
            MessageTypes.JobClaim,
            new JobClaimPayload { MaxJobs = 2, Capabilities = ["linux", "docker", fixture.Tag], Mode = "docker" },
            fixture.Clock.GetUtcNow()));

        // The error explains; the grant is empty. Running the job without credentials would burn an
        // attempt on a failure nobody could read without a transcript.
        var error = (await channel.ExpectAsync(MessageTypes.Error)).ReadPayload<ErrorPayload>();
        Assert.NotNull(error);

        var grant = (await channel.ExpectAsync(MessageTypes.JobGrant)).ReadPayload<JobGrantPayload>();
        Assert.Empty(grant!.Jobs);

        // The original claim is completed and an identical row takes its place, so no attempt is lost.
        Assert.Equal(JobStatus.Completed, (await fixture.JobAsync(jobId))!.Status);

        var replacement = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<CharterDbContext>().Jobs
                .AsNoTracking()
                .CountAsync(
                    job => job.Status == JobStatus.Pending && job.RequiredCapabilities.Contains(fixture.Tag),
                    TestContext.Current.CancellationToken));

        Assert.Equal(1, replacement);

        channel.Disconnect();
        await run;
    }

    [Fact]
    public async Task AResultCompletesFailsOrReturnsTheJobAsTheAgentSaid()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync();

        var succeeded = await RunOneAsync(fixture, agentId, AgentJobOutcomes.Succeeded);
        Assert.Equal(JobStatus.Completed, succeeded.Status);

        var failed = await RunOneAsync(fixture, agentId, AgentJobOutcomes.Failed, "the build did not compile");
        Assert.Equal(JobStatus.Pending, failed.Status);
        Assert.Equal(1, failed.Attempts);
        Assert.Equal("the build did not compile", failed.LastError);

        // Abandoned is not a failure: the agent gave the work back. Burning an attempt for it would
        // make three lapsed leases a terminal failure with an unhelpful message.
        var abandoned = await RunOneAsync(fixture, agentId, AgentJobOutcomes.Abandoned, "the lease expired");
        Assert.Equal(JobStatus.Completed, abandoned.Status);
    }

    [Fact]
    public async Task AnAgentCannotStreamEventsIntoASessionItDoesNotHold()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync();

        // Somebody else's session, real enough for the append to have succeeded — so the assertion
        // below is about the guard and not about a foreign key.
        var sessionId = await fixture.SeedSessionAsync();
        var jobId = await fixture.EnqueueClaimableAsync(sessionId);

        var (channel, run) = fixture.Connect(agentId);
        await fixture.HandshakeAsync(channel);

        channel.Send(Envelope.Create(
            MessageTypes.JobEvent,
            new JobEventPayload
            {
                JobId = jobId.ToString("D"),
                Sequence = 1,
                Kind = "log",
                Message = "everything is fine",
                At = fixture.Clock.GetUtcNow(),
            },
            fixture.Clock.GetUtcNow()));

        channel.Send(Envelope.Create(
            MessageTypes.Heartbeat,
            new HeartbeatPayload
            {
                Status = "ready",
                HeldJobIds = [],
                AvailableSlots = 2,
                CapabilitiesHash = "hash",
            },
            fixture.Clock.GetUtcNow()));

        await channel.ExpectAsync(MessageTypes.HeartbeatAck);

        // The job id came out of a frame the agent composed, so it is a request and not a fact. A
        // transcript is what a requester and an engineer both read as the account of what happened,
        // and only the worker actually holding the claim gets to write it (sections 11, 16).
        var events = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<CharterDbContext>().Events
                .AsNoTracking()
                .CountAsync(
                    candidate => candidate.SessionId == sessionId,
                    TestContext.Current.CancellationToken));

        Assert.Equal(0, events);

        channel.Disconnect();
        await run;
    }

    [Fact]
    public async Task AbandoningAJobTheAgentDoesNotHoldDoesNotDuplicateIt()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync();

        // Queued and never claimed by anybody. A lease that lapsed under a slow agent leaves the same
        // shape, and that is the case this guards: the frame names a job the agent no longer holds.
        var jobId = await fixture.EnqueueClaimableAsync(Guid.CreateVersion7());

        var (channel, run) = fixture.Connect(agentId);
        await fixture.HandshakeAsync(channel);

        channel.Send(Envelope.Create(
            MessageTypes.JobResult,
            new JobResultPayload
            {
                JobId = jobId.ToString("D"),
                Outcome = AgentJobOutcomes.Abandoned,
                Error = "the lease expired",
                FinishedAt = fixture.Clock.GetUtcNow(),
                DurationMs = 10,
            },
            fixture.Clock.GetUtcNow()));

        channel.Send(Envelope.Create(
            MessageTypes.Heartbeat,
            new HeartbeatPayload
            {
                Status = "ready",
                HeldJobIds = [],
                AvailableSlots = 2,
                CapabilitiesHash = "hash",
            },
            fixture.Clock.GetUtcNow()));

        await channel.ExpectAsync(MessageTypes.HeartbeatAck);

        // Returning work to the queue means enqueueing an equivalent row and completing the claim. The
        // completion is guarded by claimed_by and would have no-opped here, so acting on the frame's
        // word would have left a second copy of a session somebody else may already be running.
        Assert.Equal(JobStatus.Pending, (await fixture.JobAsync(jobId))!.Status);

        var rows = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<CharterDbContext>().Jobs
                .AsNoTracking()
                .CountAsync(
                    job => job.RequiredCapabilities.Contains(fixture.Tag),
                    TestContext.Current.CancellationToken));

        Assert.Equal(1, rows);

        channel.Disconnect();
        await run;
    }

    [Fact]
    public void TheAssignmentBuilderNeverLeaksTheRoutingMarkerOrInventsATimeout()
    {
        var job = new ClaimedJob(
            Guid.CreateVersion7(),
            JobType.Build,
            "{}",
            2,
            3,
            "agent:x",
            DateTimeOffset.UtcNow.AddMinutes(5),
            [AgentRunner.ClaimCapability, "linux", "dotnet:10"]);

        var payload = new AgentJobPayload
        {
            SessionId = Guid.CreateVersion7(),
            RepoFullName = "acme/widgets",
            CloneUrl = "https://github.com/acme/widgets.git",
            BaseBranch = "main",
            AdapterId = "claude-code",
            Model = "anthropic/claude-opus-5",
            SpecUrl = "https://charter.test/spec",
            CallbackUrl = "https://charter.test/callback",
            AllowPaths = ["src/**"],
            DenyPaths = ["infra/**"],
            TimeoutMinutes = 0,
        };

        var options = new AgentPlaneOptions { DefaultTimeoutMinutes = 45 };
        var assignment = AgentJobAssignmentBuilder.Build(job, payload, null, "event-token", options);

        Assert.Equal("build", assignment.Type);
        Assert.Equal(2, assignment.Attempt);
        Assert.Equal(3, assignment.MaxAttempts);
        Assert.DoesNotContain(AgentRunner.ClaimCapability, assignment.RequiredCapabilities);
        Assert.Equal(["linux", "dotnet:10"], assignment.RequiredCapabilities);

        // Section 27.5: a wall-clock cap alongside the token cap, always.
        Assert.Equal(45 * 60, assignment.TimeoutSeconds);

        // Section 32.3: the cache scope is the repository, never something shared.
        Assert.Equal("acme/widgets", assignment.Repo!.CacheScope);

        // Section 7.3: the scope travels for the shim to enforce, in the variable it already reads.
        var scope = assignment.Command.Environment[AgentJobAssignmentBuilder.PathScopeVariable];
        Assert.Contains("src/**", scope, StringComparison.Ordinal);
        Assert.Contains("infra/**", scope, StringComparison.Ordinal);

        // The shim's own flag surface, spelled as ShimCommandLine.Parse reads it.
        var problems = new List<string>();
        var parsed = Charter.Runners.Shim.ShimCommandLine.Parse([.. assignment.Command.Arguments], problems);

        Assert.Empty(problems);
        Assert.Equal(payload.SessionId.ToString("D"), parsed.SessionId);
        Assert.Equal("claude-code", parsed.Adapter);
        Assert.Equal("anthropic/claude-opus-5", parsed.Model);
        Assert.Equal(["linux", "dotnet:10"], parsed.Require);
    }

    private static async Task<Job> RunOneAsync(
        AgentPlaneFixture fixture,
        Guid agentId,
        string outcome,
        string? error = null)
    {
        var jobId = await fixture.EnqueueClaimableAsync(Guid.CreateVersion7());

        var (channel, run) = fixture.Connect(agentId);
        await fixture.HandshakeAsync(channel);
        await AgentPlaneConnectionTests.ClaimOneAsync(fixture, channel);

        channel.Send(Envelope.Create(
            MessageTypes.JobResult,
            new JobResultPayload
            {
                JobId = jobId.ToString("D"),
                Outcome = outcome,
                ExitCode = outcome == AgentJobOutcomes.Succeeded ? 0 : 1,
                Error = error,
                FinishedAt = fixture.Clock.GetUtcNow(),
                DurationMs = 1234,
            },
            fixture.Clock.GetUtcNow()));

        // Give the handler its turn before the socket closes under it.
        channel.Send(Envelope.Create(
            MessageTypes.Heartbeat,
            new HeartbeatPayload
            {
                Status = "ready",
                HeldJobIds = [],
                AvailableSlots = 2,
                CapabilitiesHash = "hash",
            },
            fixture.Clock.GetUtcNow()));

        await channel.ExpectAsync(MessageTypes.HeartbeatAck);

        channel.Disconnect();
        await run;

        return (await fixture.JobAsync(jobId))!;
    }
}
