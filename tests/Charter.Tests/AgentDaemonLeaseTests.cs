using Charter.Agent;
using Charter.Agent.Execution;
using Charter.Agent.Jobs;
using Charter.Agent.Protocol;
using Charter.Agent.Session;

namespace Charter.Tests;

/// <summary>
/// Two ways one host ends up running work it cannot stop (sections 33.3, 33.4).
/// </summary>
/// <remarks>
/// <para>
/// Both come back to the same promise. A claim carries a lease, the lease is renewed by heartbeat,
/// and a job whose lease is not renewed belongs to whoever claims it next. The agent's half of that
/// is stopping local work the moment it loses the claim — <see cref="LeaseBook"/> says so in as many
/// words: if only the plane re-queued, a partitioned agent would keep running a job another agent had
/// already picked up, and two runners would push to the same branch.
/// </para>
/// <para>
/// The first failure is a duplicate grant: the same job id started twice on one host, the second
/// handle overwriting the first, so cancel, revoke and lease expiry all reach the copy and none of
/// them the original. The second is the ack: the wire contract says a job absent from a heartbeat ack
/// has lost its lease, and the control plane leaves one out precisely to say stop — but the agent
/// read only the renewals, so the stop signal reached nothing.
/// </para>
/// </remarks>
public class AgentDaemonLeaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AJobGrantedTwiceIsStartedOnceAndKeepsTheRunningCopy()
    {
        var session = Ready();

        var first = session.Receive(AgentSessionTests.Grant(AgentSessionTests.Job("job-1", ["linux"])), Now);
        Assert.Equal(["job-1"], first.Start.Select(job => job.JobId));

        // The same job again — clock skew between a lease the plane thought had lapsed and one this
        // agent still holds is enough to produce it.
        var again = session.Receive(
            AgentSessionTests.Grant(AgentSessionTests.Job("job-1", ["linux"], leaseSeconds: 600)),
            Now.AddSeconds(1));

        Assert.Empty(again.Start);
        Assert.Empty(again.Stop);
        Assert.DoesNotContain(again.Send, envelope => envelope.Type == MessageTypes.JobResult);
        Assert.Contains(
            again.Notes,
            note => note.Message.Contains("already running here", StringComparison.Ordinal));

        // One lease, at the newer expiry, and the slot is still counted once.
        Assert.Equal(["job-1"], session.HeldJobIds);
        Assert.Equal(1, session.AvailableSlots);

        // The renewal really took: the original expiry passes without the work being stopped.
        Assert.Empty(session.Advance(Now.AddSeconds(400)).Stop);
    }

    [Fact]
    public async Task ADuplicateGrantNeverLeavesTwoShimsRunningOnOneHost()
    {
        // The same thing through the daemon, because the consequence lives there: `_running[jobId]`
        // is what holds a job's CancellationTokenSource, and overwriting it drops the first job's on
        // the floor while its shim carries on pushing a branch.
        var transport = new FakeTransport();
        transport.OnSend(MessageTypes.Hello, _ => [AgentSessionTests.Welcome()]);

        var grants = 0;
        transport.OnSend(
            MessageTypes.JobClaim,
            _ => Interlocked.Increment(ref grants) <= 2
                ? [AgentSessionTests.Grant(AgentSessionTests.Job("job-1", ["linux"]))]
                : []);

        var executor = new CountingExecutor();
        var harness = new DaemonHarness(transport, executor);

        await harness.RunUntilAsync(() => grants >= 2 && executor.Started.Task.IsCompleted);

        Assert.Equal(1, executor.Starts);

        // And the one that is running is the one the daemon can stop. Shutting down cancels it; a
        // leaked handle would leave the token nobody holds and the count would not come back to zero.
        Assert.Equal(0, executor.Running);
        Assert.True(executor.Cancelled);
    }

    [Fact]
    public void AnAckThatLeavesOutALeaseStopsTheWork()
    {
        var session = Ready();
        session.Receive(AgentSessionTests.Grant(AgentSessionTests.Job("job-1", ["linux"], leaseSeconds: 600)), Now);

        var beat = session.Advance(Now.AddSeconds(31));
        var heartbeat = Assert.Single(beat.Send, envelope => envelope.Type == MessageTypes.Heartbeat);
        Assert.Equal(["job-1"], heartbeat.ReadPayload<HeartbeatPayload>()!.HeldJobIds);

        // What AgentConnection.RenewAsync sends when the claim is gone: the job is simply absent.
        var step = session.Receive(Ack(heartbeat), Now.AddSeconds(32));

        var stop = Assert.Single(step.Stop);
        Assert.Equal("job-1", stop.JobId);

        // Reports nothing, for the same reason a lapsed lease does: the claim is already somebody
        // else's, and a result from here would contradict whoever holds it.
        Assert.False(stop.Report);
        Assert.DoesNotContain(step.Send, envelope => envelope.Type == MessageTypes.JobResult);

        // The lease and the slot are both released, so the agent can claim again straight away.
        Assert.Empty(session.HeldJobIds);
        Assert.Equal(2, session.AvailableSlots);
    }

    [Fact]
    public void AnAckDoesNotStopAJobGrantedAfterTheHeartbeatWasSent()
    {
        // The ack answers a heartbeat that named one job. A job granted in the gap was never up for
        // renewal, and stopping it would kill work the plane had just handed over.
        var session = Ready();
        session.Receive(AgentSessionTests.Grant(AgentSessionTests.Job("job-1", ["linux"], leaseSeconds: 600)), Now);

        var beat = session.Advance(Now.AddSeconds(31));
        var heartbeat = Assert.Single(beat.Send, envelope => envelope.Type == MessageTypes.Heartbeat);

        session.Receive(
            AgentSessionTests.Grant(AgentSessionTests.Job("job-2", ["linux"], leaseSeconds: 600)),
            Now.AddSeconds(31));

        var step = session.Receive(
            Ack(heartbeat, new LeaseGrant { JobId = "job-1", LeaseExpiresAt = Now.AddSeconds(900) }),
            Now.AddSeconds(32));

        Assert.Empty(step.Stop);
        Assert.Equal(["job-1", "job-2"], session.HeldJobIds);
    }

    [Fact]
    public void AnAckForSomeOtherHeartbeatStopsNothing()
    {
        var session = Ready();
        session.Receive(AgentSessionTests.Grant(AgentSessionTests.Job("job-1", ["linux"], leaseSeconds: 600)), Now);
        session.Advance(Now.AddSeconds(31));

        var stray = Envelope.Create(
            MessageTypes.HeartbeatAck,
            new HeartbeatAckPayload(),
            Now.AddSeconds(32),
            "a-frame-this-agent-never-sent");

        Assert.Empty(session.Receive(stray, Now.AddSeconds(32)).Stop);
        Assert.Equal(["job-1"], session.HeldJobIds);
    }

    [Fact]
    public async Task TheDaemonStopsAJobTheControlPlaneStoppedRenewing()
    {
        var transport = new FakeTransport();
        transport.OnSend(MessageTypes.Hello, _ => [AgentSessionTests.Welcome()]);

        var granted = 0;
        transport.OnSend(
            MessageTypes.JobClaim,
            _ => Interlocked.Increment(ref granted) == 1
                ? [AgentSessionTests.Grant(AgentSessionTests.Job("job-1", ["linux"], leaseSeconds: 3600))]
                : []);

        // Every heartbeat is acknowledged with no leases at all: the claim is gone (swept, cancelled,
        // or claimed by another worker) and the agent is being told to stop.
        transport.OnSend(
            MessageTypes.Heartbeat,
            beat =>
            [
                Envelope.Create(MessageTypes.HeartbeatAck, new HeartbeatAckPayload(), Now, beat.Id),
            ]);

        var executor = new CountingExecutor();
        var harness = new DaemonHarness(transport, executor);

        await harness.RunUntilAsync(() => executor.Cancelled);

        Assert.True(executor.Cancelled);
        Assert.Contains(
            harness.Log.Lines,
            line => line.Contains("did not renew the lease", StringComparison.Ordinal));

        // Nothing is reported for it. The lease is somebody else's now, and a result would contradict
        // whoever picked the work up.
        Assert.DoesNotContain(transport.Sent, envelope => envelope.Type == MessageTypes.JobResult);
    }

    private static Envelope Ack(Envelope heartbeat, params LeaseGrant[] leases) =>
        Envelope.Create(
            MessageTypes.HeartbeatAck,
            new HeartbeatAckPayload { Leases = leases },
            heartbeat.SentAt.AddMilliseconds(20),
            heartbeat.Id);

    private static AgentSession Ready()
    {
        var session = new AgentSession(AgentSessionTests.Options(), AgentSessionTests.Probed(), Now);
        session.Open(Now);
        session.Receive(AgentSessionTests.Welcome(), Now);
        return session;
    }
}

/// <summary>
/// An executor that blocks until it is cancelled, and counts how many times it was entered.
/// </summary>
/// <remarks>
/// A shim is a long-running process, so a stub that returns immediately cannot show the failure these
/// tests are about: two of them running at once, and one of them holding a cancellation token nobody
/// has a handle to any more.
/// </remarks>
internal sealed class CountingExecutor : IJobExecutor
{
    private int _starts;
    private int _running;

    /// <summary>Completes the first time a job is entered.</summary>
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Starts => Volatile.Read(ref _starts);

    /// <summary>How many are inside <see cref="ExecuteAsync"/> right now.</summary>
    public int Running => Volatile.Read(ref _running);

    public bool Cancelled { get; private set; }

    public string Describe() => "counting";

    public Task<IReadOnlyList<string>> PreflightAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>([]);

    public async Task<JobCompletion> ExecuteAsync(
        JobAssignment job,
        IJobEventSink events,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _starts);
        Interlocked.Increment(ref _running);
        Started.TrySetResult();

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new JobCompletion(job.JobId, JobOutcomes.Succeeded, 0);
        }
        catch (OperationCanceledException)
        {
            Cancelled = true;
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }
}
