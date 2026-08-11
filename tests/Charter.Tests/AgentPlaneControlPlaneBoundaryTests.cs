using System.Text.Json;
using Charter.Data;
using Charter.Domain;
using Charter.Runners.Agent;
using Microsoft.Extensions.DependencyInjection;

namespace Charter.Tests;

/// <summary>
/// The section 2.2 boundary, expressed on the queue: a Charter Agent claims execution plane work and
/// nothing else, and the control plane still claims all of its own.
/// </summary>
/// <remarks>
/// <para>
/// Charter enqueues <c>refine</c>, <c>recap</c>, <c>update_check</c>, <c>recon</c>, <c>smoke_test</c>
/// and the control plane's own <c>build</c> rows requiring <em>no</em> capabilities, because they run
/// in-process and need nothing from a runner. The claim filter of section 27.3 is a containment test,
/// and the empty set is contained in every set — so on capability alone every one of those rows is
/// claimable by any daemon that happens to be online.
/// </para>
/// <para>
/// What follows is not a wasted poll. The daemon's payload parse fails, because the row was never
/// written for it, and the job is failed rather than handed back. Refinement is the front door of the
/// product (section 10): a request whose refine job was swallowed by somebody's Mac mini stalls with
/// nothing on screen to explain why.
/// </para>
/// <para>
/// Against a real Postgres queue, because the fix is a predicate in the claim statement and a fake
/// would prove nothing about it — and in a schema of this fixture's own, because the subject here is
/// precisely what an unfiltered claim sweeps up. A job requiring no capabilities is claimable by every
/// worker in the suite, so writing four of them into the shared queue would make these tests both
/// flaky and a cause of flakiness elsewhere.
/// </para>
/// </remarks>
[Collection(AgentPlaneCollection.Name)]
public class AgentPlaneControlPlaneBoundaryTests
{
    /// <summary>What a daemon on a real host advertises, rather than a token capability set.</summary>
    private static readonly string[] DaemonCapabilities =
        ["linux", "docker", "dotnet:10.0.100", "node:22.11.0", "git:2.45.2"];

    [Fact]
    public async Task AnOnlineDaemonNeverClaimsControlPlaneWorkAndTheControlPlaneStillDoes()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync(isolated: true);
        if (fixture is null)
        {
            return;
        }

        var capabilities = new List<string>(DaemonCapabilities) { fixture.Tag };
        var (agentId, _) = await fixture.PairAsync(capabilities: capabilities);

        // Exactly the shapes the control plane writes, capabilities included — which is to say, none.
        var refine = await EnqueueControlPlaneAsync(
            fixture,
            JobType.Refine,
            JsonSerializer.Serialize(new { requestId = Guid.CreateVersion7() }));

        var recap = await EnqueueControlPlaneAsync(
            fixture,
            JobType.Recap,
            JsonSerializer.Serialize(new { sessionId = Guid.CreateVersion7() }));

        var updateCheck = await EnqueueControlPlaneAsync(
            fixture,
            JobType.UpdateCheck,
            JsonSerializer.Serialize(new { channel = "stable", currentVersion = "0.1.0" }));

        // And the fourth one, which is the same defect wearing the job type the daemon does run: a
        // build row the control plane enqueued for its own dispatcher, carrying no routing marker.
        var controlPlaneBuild = await EnqueueControlPlaneAsync(
            fixture,
            JobType.Build,
            JsonSerializer.Serialize(new { sessionId = Guid.CreateVersion7() }));

        var controlPlaneJobs = new[] { refine, recap, updateCheck, controlPlaneBuild };

        // The one row that really is the daemon's, so a starved queue cannot pass for a fixed one.
        var agentBuild = await fixture.EnqueueClaimableAsync(Guid.CreateVersion7());

        var (channel, run) = fixture.Connect(agentId);
        await fixture.HandshakeAsync(channel, capabilities);

        channel.Send(Envelope.Create(
            MessageTypes.JobClaim,
            new JobClaimPayload { MaxJobs = 8, Capabilities = capabilities, Mode = "docker" },
            fixture.Clock.GetUtcNow()));

        var grant = (await channel.ExpectAsync(MessageTypes.JobGrant)).ReadPayload<JobGrantPayload>();
        Assert.NotNull(grant);

        // The build job routed. The fix must not starve the runner it is protecting.
        var granted = Assert.Single(grant.Jobs);
        Assert.Equal(agentBuild.ToString("D"), granted.JobId);

        // Untouched: still pending, still on attempt zero, no error recorded. "Claimed and then handed
        // back" would also leave a pending row, so the attempt count is the assertion that matters.
        foreach (var jobId in controlPlaneJobs)
        {
            var row = await fixture.JobAsync(jobId);

            Assert.NotNull(row);
            Assert.Equal(JobStatus.Pending, row.Status);
            Assert.Equal(0, row.Attempts);
            Assert.Null(row.ClaimedBy);
            Assert.Null(row.LastError);
        }

        // What the control plane's dispatcher would claim with: the union the registry advertises,
        // taken from the real runner while this daemon is online and heartbeating.
        var descriptor = await fixture.Runner.DescribeAsync(TestContext.Current.CancellationToken);
        Assert.True(descriptor.Online);
        Assert.Contains(fixture.Tag, descriptor.Capabilities);

        const string controlPlaneWorker = "control-plane-replica-1";

        var claimed = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<JobQueue>().ClaimAsync(
                controlPlaneWorker,
                fixture.Options.Lease,
                16,
                descriptor.Capabilities,
                now: fixture.Clock.GetUtcNow(),
                cancellationToken: TestContext.Current.CancellationToken));

        // Not merely "a claim happened": these four rows, all of them, held by the control plane.
        Assert.Equal(
            controlPlaneJobs.Order().ToArray(),
            claimed.Select(job => job.Id).Order().ToArray());

        Assert.All(claimed, job => Assert.Equal(controlPlaneWorker, job.ClaimedBy));
        Assert.Equal(controlPlaneWorker, (await fixture.JobAsync(refine))!.ClaimedBy);

        // And the boundary holds in the other direction too: the daemon's row is not the dispatcher's,
        // which is what keeps a session from being dispatched a second time.
        Assert.DoesNotContain(agentBuild, claimed.Select(job => job.Id));

        channel.Disconnect();
        await run;
    }

    [Fact]
    public async Task TheClaimFilterIsWhatStopsIt_NotTheHandshakeOrTheHandler()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync(isolated: true);
        if (fixture is null)
        {
            return;
        }

        var refine = await EnqueueControlPlaneAsync(
            fixture,
            JobType.Refine,
            JsonSerializer.Serialize(new { requestId = Guid.CreateVersion7() }));

        // The daemon's identity and its full capability set, claiming directly against Postgres. No
        // connection, no handshake, no frame handler: if the guard lived anywhere above the SQL this
        // would still take the row.
        var agentCapabilities = new List<string>(DaemonCapabilities)
        {
            fixture.Tag,
            AgentRunner.ClaimCapability,
        };

        var claimed = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<JobQueue>().ClaimAsync(
                AgentRunner.WorkerIdFor(Guid.CreateVersion7()),
                fixture.Options.Lease,
                16,
                agentCapabilities,
                AgentRunner.ClaimCapability,
                fixture.Clock.GetUtcNow(),
                TestContext.Current.CancellationToken));

        Assert.Empty(claimed);

        var row = await fixture.JobAsync(refine);
        Assert.Equal(JobStatus.Pending, row!.Status);
        Assert.Equal(0, row.Attempts);

        // Why claiming it was never survivable: the payload is not an agent job and never becomes one,
        // so the daemon's only move is to fail a row the requester is waiting on.
        Assert.Null(AgentJobPayload.TryParse(row.Payload));
    }

    /// <summary>Enqueues a job the way the control plane does: a payload, and no capabilities at all.</summary>
    private static Task<Guid> EnqueueControlPlaneAsync(AgentPlaneFixture fixture, JobType type, string payload) =>
        fixture.InScopeAsync(async provider =>
        {
            var job = await provider.GetRequiredService<JobQueue>().EnqueueAsync(
                type,
                payload,
                now: fixture.Clock.GetUtcNow(),
                cancellationToken: TestContext.Current.CancellationToken);

            return job.Id;
        });
}
