using Charter.Data;
using Charter.Domain;
using Charter.Runners;
using Charter.Runners.Agent;
using Microsoft.Extensions.DependencyInjection;

namespace Charter.Tests;

/// <summary>
/// The liveness gate on the agent plane's mint (sections 6, 16, 33.5).
/// </summary>
/// <remarks>
/// <para>
/// Section 16 is explicit that a session's credential is its blast radius, and section 33.5 says the
/// agent receives a short-TTL repository token <em>per job</em>. Both only mean anything if the
/// window in which one can be minted is the window in which a session is genuinely running.
/// </para>
/// <para>
/// A job row outliving its session is an ordinary path rather than a contrived one. Cancelling a
/// claimed session calls <c>JobQueue.FailAsync</c>, and a failed job with an attempt left goes back
/// to <c>Pending</c> for the next agent to claim — so without this gate, pressing cancel could be
/// followed by a fresh contribute-scoped GitHub token and a twelve-hour event token being handed to
/// a runner for work that had already been called off.
/// </para>
/// </remarks>
[Collection(AgentPlaneCollection.Name)]
public class AgentPlaneCredentialTests
{
    [Fact]
    public async Task ALiveDispatchedSessionIsGrantedItsCredentials()
    {
        // The control. Without it every assertion below could pass because nothing is ever granted.
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

        var granted = await ClaimAsync(fixture, channel);

        Assert.NotNull(granted);
        Assert.Equal(jobId.ToString("D"), granted.JobId);
        Assert.Equal(StubRunnerCredentialBroker.GitHubToken, granted.Secrets!.GitHub!.Token);
        Assert.Equal(["acme/widgets"], fixture.Broker.Issued);

        channel.Disconnect();
        await run;
    }

    [Theory]
    [InlineData(SessionStatus.Failed)]
    [InlineData(SessionStatus.Cancelled)]
    [InlineData(SessionStatus.Merged)]
    [InlineData(SessionStatus.HandedOff)]
    public async Task ATerminalSessionIsNeverGivenCredentials(SessionStatus status)
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync();
        var sessionId = Guid.CreateVersion7();
        var jobId = await fixture.EnqueueClaimableAsync(sessionId);

        await fixture.MoveSessionAsync(sessionId, status);

        var (channel, run) = fixture.Connect(agentId);
        await fixture.HandshakeAsync(channel);

        Assert.Null(await ClaimAsync(fixture, channel));

        // Nothing was minted, and the row is settled rather than handed back: returning it would
        // re-offer the same dead work on the agent's next claim, forever.
        Assert.Empty(fixture.Broker.Issued);
        Assert.Equal(JobStatus.Completed, (await fixture.JobAsync(jobId))!.Status);

        channel.Disconnect();
        await run;
    }

    [Fact]
    public async Task ASessionWithACancelInFlightIsNeverGivenCredentials()
    {
        // The status has not moved yet — cancellation is a request first and a terminal state after
        // the runner has been stopped and cost settled (section 11). A twelve-hour token minted in
        // that window outlives the thing that justified it by most of a day.
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync();
        var sessionId = Guid.CreateVersion7();
        var jobId = await fixture.EnqueueClaimableAsync(sessionId);

        await fixture.MoveSessionAsync(sessionId, cancelRequested: true);

        var (channel, run) = fixture.Connect(agentId);
        await fixture.HandshakeAsync(channel);

        Assert.Null(await ClaimAsync(fixture, channel));
        Assert.Empty(fixture.Broker.Issued);
        Assert.Equal(JobStatus.Completed, (await fixture.JobAsync(jobId))!.Status);

        channel.Disconnect();
        await run;
    }

    [Fact]
    public async Task ASessionNoBackendHoldsIsNeverGivenCredentials()
    {
        // No dispatch claim in the journal means nothing legitimate is running this session. The HTTP
        // exchange has always refused that case; the agent plane did not.
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync();
        var sessionId = await fixture.SeedSessionAsync(dispatched: false);
        var jobId = await fixture.EnqueueClaimableAsync(sessionId, seedSession: false);

        var (channel, run) = fixture.Connect(agentId);
        await fixture.HandshakeAsync(channel);

        Assert.Null(await ClaimAsync(fixture, channel));
        Assert.Empty(fixture.Broker.Issued);
        Assert.Equal(JobStatus.Completed, (await fixture.JobAsync(jobId))!.Status);

        channel.Disconnect();
        await run;
    }

    [Fact]
    public async Task AJobForASessionThatDoesNotExistIsNeverGivenCredentials()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync();
        var jobId = await fixture.EnqueueClaimableAsync(Guid.CreateVersion7(), seedSession: false);

        var (channel, run) = fixture.Connect(agentId);
        await fixture.HandshakeAsync(channel);

        Assert.Null(await ClaimAsync(fixture, channel));
        Assert.Empty(fixture.Broker.Issued);

        channel.Disconnect();
        await run;
    }

    [Fact]
    public async Task TheTokenFollowsTheSessionsRepositoryRatherThanTheRowsClaimAboutIt()
    {
        // Section 7.4: one repository, and it is the one the session's own aggregate names. A row
        // whose payload disagrees is refused rather than trusted, so a token for a repository the
        // session has nothing to do with cannot be minted by writing one queue row.
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync();
        var sessionId = await fixture.SeedSessionAsync("acme/widgets", dispatched: true);
        var jobId = await fixture.EnqueueClaimableAsync(sessionId, repo: "acme/payroll", seedSession: false);

        var (channel, run) = fixture.Connect(agentId);
        await fixture.HandshakeAsync(channel);

        Assert.Null(await ClaimAsync(fixture, channel));
        Assert.Empty(fixture.Broker.Issued);
        Assert.Equal(JobStatus.Completed, (await fixture.JobAsync(jobId))!.Status);

        channel.Disconnect();
        await run;
    }

    [Fact]
    public async Task TheGuardReadsTheSessionRatherThanTheCallersWord()
    {
        // The gate itself, over every state, so the two callers are testing one decision.
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var live = await fixture.SeedSessionAsync("acme/widgets", dispatched: true);
        var undispatched = await fixture.SeedSessionAsync("acme/widgets");
        var ended = await fixture.SeedSessionAsync("acme/widgets", dispatched: true);
        var cancelling = await fixture.SeedSessionAsync("acme/widgets", dispatched: true);

        await fixture.MoveSessionAsync(ended, SessionStatus.Failed);
        await fixture.MoveSessionAsync(cancelling, cancelRequested: true);

        await fixture.InScopeAsync(async provider =>
        {
            var db = provider.GetRequiredService<CharterDbContext>();
            var journal = provider.GetRequiredService<Charter.Orchestration.SessionJournal>();
            var token = TestContext.Current.CancellationToken;

            var allowed = await SessionCredentialGuard.EvaluateAsync(db, journal, live, token);
            Assert.True(allowed.Allowed);
            Assert.Equal("acme/widgets", allowed.RepoFullName);

            Assert.Equal(
                SessionCredentialRefusal.NotDispatched,
                (await SessionCredentialGuard.EvaluateAsync(db, journal, undispatched, token)).Refusal);

            Assert.Equal(
                SessionCredentialRefusal.Ended,
                (await SessionCredentialGuard.EvaluateAsync(db, journal, ended, token)).Refusal);

            Assert.Equal(
                SessionCredentialRefusal.Cancelled,
                (await SessionCredentialGuard.EvaluateAsync(db, journal, cancelling, token)).Refusal);

            Assert.Equal(
                SessionCredentialRefusal.UnknownSession,
                (await SessionCredentialGuard.EvaluateAsync(db, journal, Guid.CreateVersion7(), token)).Refusal);
        });
    }

    /// <summary>Claims once and returns the single granted job, or null when nothing was granted.</summary>
    private static async Task<JobAssignment?> ClaimAsync(AgentPlaneFixture fixture, LoopbackAgentChannel channel)
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

        var frame = await channel.NextAsync();

        // A refusal to mint sends no error frame of its own: the job is settled and simply not
        // granted, so the agent sees an ordinary empty grant and asks again later.
        Assert.Equal(MessageTypes.JobGrant, frame.Type);

        var grant = frame.ReadPayload<JobGrantPayload>();
        Assert.NotNull(grant);

        return grant.Jobs.Count == 0 ? null : Assert.Single(grant.Jobs);
    }
}
