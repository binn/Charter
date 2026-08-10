using System.Collections.Concurrent;
using System.Threading.Channels;
using Charter.Agent;
using Charter.Agent.Capabilities;
using Charter.Agent.Execution;
using Charter.Agent.Jobs;
using Charter.Agent.Logging;
using Charter.Agent.Protocol;
using Charter.Agent.Session;
using Charter.Agent.Transport;

namespace Charter.Tests;

/// <summary>
/// The daemon loop end to end (section 33), against a stubbed transport. No socket is opened, no
/// Docker daemon is contacted, and no control plane exists.
/// </summary>
public class AgentDaemonTests
{
    [Fact]
    public async Task DialsOutRegistersAndClaimsWithoutAnythingListeningOnThisHost()
    {
        var transport = new FakeTransport();
        transport.OnSend(MessageTypes.Hello, _ => [AgentSessionTests.Welcome()]);

        var harness = new DaemonHarness(transport);
        await harness.RunUntilAsync(() => transport.Sent.Any(e => e.Type == MessageTypes.JobClaim));

        Assert.Equal(MessageTypes.Hello, transport.Sent[0].Type);
        Assert.Contains(transport.Sent, e => e.Type == MessageTypes.JobClaim);
        Assert.Equal(SessionPhase.Ready, harness.ObservedPhase);
        Assert.Contains(harness.Log.Lines, l => l.Contains("registered as agt_1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunsAGrantedJobAndReportsTheResult()
    {
        var transport = new FakeTransport();
        transport.OnSend(MessageTypes.Hello, _ => [AgentSessionTests.Welcome()]);
        var granted = 0;
        transport.OnSend(
            MessageTypes.JobClaim,
            claim => claim.ReadPayload<JobClaimPayload>()!.MaxJobs > 0 && Interlocked.Increment(ref granted) == 1
                ? [AgentSessionTests.Grant(AgentSessionTests.Job("job-1", ["linux"]))]
                : []);

        var executor = new StubExecutor
        {
            Run = (job, events) =>
            {
                events.Publish(job.JobId, "stdout", "building");
                return new JobCompletion(job.JobId, JobOutcomes.Succeeded, 0);
            },
        };

        var harness = new DaemonHarness(transport, executor);
        await harness.RunUntilAsync(() => transport.Sent.Any(e => e.Type == MessageTypes.JobResult));

        var jobEvent = Assert.Single(transport.Sent, e => e.Type == MessageTypes.JobEvent);
        Assert.Equal("building", jobEvent.ReadPayload<JobEventPayload>()!.Message);

        var result = Assert.Single(transport.Sent, e => e.Type == MessageTypes.JobResult);
        Assert.Equal(JobOutcomes.Succeeded, result.ReadPayload<JobResultPayload>()!.Outcome);
    }

    [Fact]
    public async Task AnUnansweredClaimAgesOutSoTheAgentAsksAgain()
    {
        // Section 33.4: the agent claims and the control plane never pushes, so a claim that is
        // never answered - a dropped frame, a plane that restarted - must not park the agent.
        var transport = new FakeTransport();
        transport.OnSend(MessageTypes.Hello, _ => [AgentSessionTests.Welcome()]);

        var harness = new DaemonHarness(transport);
        await harness.RunUntilAsync(() => transport.Sent.Count(e => e.Type == MessageTypes.JobClaim) >= 2);

        Assert.All(
            transport.Sent,
            e => Assert.Contains(
                e.Type,
                new[] { MessageTypes.Hello, MessageTypes.JobClaim, MessageTypes.Heartbeat, MessageTypes.Goodbye }));
    }

    [Fact]
    public async Task AFlakyLinkReconnectsOnItsOwnWithGrowingBackoff()
    {
        // Section 33.1: a home connection that drops must not need babysitting.
        var factory = new FailingTransportFactory(failures: 4, then: () =>
        {
            var transport = new FakeTransport();
            transport.OnSend(MessageTypes.Hello, _ => [AgentSessionTests.Welcome()]);
            return transport;
        });

        var harness = new DaemonHarness(factory);
        await harness.RunUntilAsync(() => harness.Daemon.Current.Phase == SessionPhase.Ready);

        Assert.Equal(5, factory.Attempts);
        Assert.Equal(SessionPhase.Ready, harness.ObservedPhase);

        // Jitter is pinned to its ceiling here, so the growth is exactly the doubling.
        Assert.Equal(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)],
            harness.Delays.Take(4));
        Assert.Contains(harness.Log.Lines, l => l.Contains("reconnecting in", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ShuttingDownHandsTheLeasesBackInsteadOfLettingThemLapse()
    {
        var transport = new FakeTransport();
        transport.OnSend(MessageTypes.Hello, _ => [AgentSessionTests.Welcome()]);

        var granted = 0;
        transport.OnSend(
            MessageTypes.JobClaim,
            _ => Interlocked.Increment(ref granted) == 1
                ? [AgentSessionTests.Grant(AgentSessionTests.Job("job-1", ["linux"]))]
                : []);

        var blocked = new TaskCompletionSource();
        var executor = new StubExecutor
        {
            Run = (job, _) =>
            {
                blocked.TrySetResult();
                Thread.Sleep(50);
                return new JobCompletion(job.JobId, JobOutcomes.Cancelled);
            },
        };

        var harness = new DaemonHarness(transport, executor);
        await harness.RunUntilAsync(() => blocked.Task.IsCompleted);

        var goodbye = Assert.Single(transport.Sent, e => e.Type == MessageTypes.Goodbye);
        Assert.Equal(["job-1"], goodbye.ReadPayload<GoodbyePayload>()!.ReleasedJobIds);
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task RevocationStopsTheDaemonRatherThanReconnectingForever()
    {
        var transport = new FakeTransport();
        transport.OnSend(MessageTypes.Hello, _ =>
        [
            AgentSessionTests.Welcome(),
            Envelope.Create(
                MessageTypes.Revoked,
                new RevokedPayload { Reason = "revoked in Settings -> Runners" },
                DateTimeOffset.UnixEpoch),
        ]);

        var harness = new DaemonHarness(transport);
        var reason = await harness.RunToCompletionAsync();

        Assert.Equal(AgentExitReason.Revoked, reason);
        Assert.Contains(harness.Log.Lines, l => l.Contains("revoked", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AProtocolMismatchClaimsNothingAtAll()
    {
        // Section 33.6: a clear message and a refusal to claim, not subtle failures three sessions on.
        var transport = new FakeTransport();
        transport.OnSend(
            MessageTypes.Hello,
            _ => [AgentSessionTests.Welcome(protocolVersion: AgentProtocol.Version + 5)]);

        var harness = new DaemonHarness(transport);
        await harness.RunUntilAsync(() => transport.Sent.Any(e => e.Type == MessageTypes.Heartbeat));

        Assert.DoesNotContain(transport.Sent, e => e.Type == MessageTypes.JobClaim);
        Assert.Equal(SessionPhase.Refusing, harness.ObservedPhase);
        Assert.Contains(harness.Log.Lines, l => l.Contains("Protocol mismatch", StringComparison.Ordinal));
    }
}

// -------------------------------------------------------------------------------------------------
// Doubles shared by the Agent* test files. Everything the daemon touches - transport, processes,
// executor, clock, log - is stubbed, so no test opens a socket, starts a process, or waits on time.
// -------------------------------------------------------------------------------------------------

/// <summary>A transport with scripted replies. Records everything the agent sent.</summary>
internal sealed class FakeTransport : IAgentTransport
{
    private readonly Channel<Envelope> _inbound = Channel.CreateUnbounded<Envelope>();
    private readonly ConcurrentQueue<Envelope> _sent = new();
    private readonly List<(string Type, Func<Envelope, IReadOnlyList<Envelope>> Reply)> _scripts = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<Envelope> Sent => [.. _sent];

    public int? CloseStatus { get; private set; }

    public string? CloseDescription { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>Answers a frame of <paramref name="type"/> with whatever the script returns.</summary>
    public void OnSend(string type, Func<Envelope, IReadOnlyList<Envelope>> reply)
    {
        lock (_gate)
        {
            _scripts.Add((type, reply));
        }
    }

    public void Push(Envelope envelope) => _inbound.Writer.TryWrite(envelope);

    public void Close(int status = 1000, string? description = null)
    {
        CloseStatus = status;
        CloseDescription = description;
        _inbound.Writer.TryComplete();
    }

    public Task SendAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        _sent.Enqueue(envelope);

        List<(string Type, Func<Envelope, IReadOnlyList<Envelope>> Reply)> scripts;
        lock (_gate)
        {
            scripts = [.. _scripts];
        }

        foreach (var (type, reply) in scripts.Where(s => s.Type == envelope.Type))
        {
            foreach (var response in reply(envelope))
            {
                _inbound.Writer.TryWrite(response);
            }
        }

        return Task.CompletedTask;
    }

    public async Task<Envelope?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _inbound.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeTransportFactory(IAgentTransport transport) : IAgentTransportFactory
{
    public string? LastToken { get; private set; }

    public Task<IAgentTransport> ConnectAsync(string agentToken, CancellationToken cancellationToken = default)
    {
        LastToken = agentToken;
        return Task.FromResult(transport);
    }
}

/// <summary>Fails to connect a fixed number of times, then hands over a working transport.</summary>
internal sealed class FailingTransportFactory(int failures, Func<IAgentTransport> then) : IAgentTransportFactory
{
    public int Attempts { get; private set; }

    public IAgentTransport? Connected { get; private set; }

    public Task<IAgentTransport> ConnectAsync(string agentToken, CancellationToken cancellationToken = default)
    {
        Attempts++;
        if (Attempts <= failures)
        {
            throw new TransportClosedException("the network is down");
        }

        Connected ??= then();
        return Task.FromResult(Connected);
    }
}

/// <summary>Canned command output, keyed by executable. Nothing is ever started.</summary>
internal sealed class StubProcessRunner : IProcessRunner
{
    public Dictionary<string, ProcessResult> Results { get; } = new(StringComparer.Ordinal);

    public List<string> Invocations { get; } = [];

    public Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Invocations.Add($"{executable} {string.Join(' ', arguments)}".Trim());
        return Task.FromResult(Results.TryGetValue(executable, out var result) ? result : ProcessResult.NotFound);
    }
}

internal sealed class StubExecutor : IJobExecutor
{
    public Func<JobAssignment, IJobEventSink, JobCompletion> Run { get; set; } =
        (job, _) => new JobCompletion(job.JobId, JobOutcomes.Succeeded, 0);

    public IReadOnlyList<string> Preflight { get; set; } = [];

    public string Describe() => "stub";

    public Task<IReadOnlyList<string>> PreflightAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Preflight);

    public Task<JobCompletion> ExecuteAsync(
        JobAssignment job,
        IJobEventSink events,
        CancellationToken cancellationToken) =>
        Task.FromResult(Run(job, events));
}

internal sealed class RecordingLog : IAgentLog
{
    private readonly List<string> _lines = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    public void Write(LogLevel level, string message)
    {
        lock (_gate)
        {
            _lines.Add(message);
        }
    }
}

/// <summary>
/// Drives an <see cref="AgentDaemon"/> on a virtual clock: every wait the daemon asks for advances
/// the clock rather than the wall, so heartbeats, claim intervals and backoff are deterministic.
/// </summary>
internal sealed class DaemonHarness
{
    private readonly CancellationTokenSource _cancellation = new();
    private DateTimeOffset _clock = new(2026, 8, 10, 9, 0, 0, TimeSpan.Zero);

    public DaemonHarness(IAgentTransport transport, IJobExecutor? executor = null)
        : this(new FakeTransportFactory(transport), executor)
    {
    }

    public DaemonHarness(IAgentTransportFactory factory, IJobExecutor? executor = null)
    {
        Log = new RecordingLog();
        Scrubber = new Charter.Agent.Logging.SecretScrubber();
        Daemon = new AgentDaemon(
            AgentSessionTests.Options(),
            "agent-token-value-that-is-long",
            AgentSessionTests.Probed(),
            factory,
            executor ?? new StubExecutor(),
            new CapabilityProber(new StubProcessRunner()),
            Log,
            Scrubber,
            new ReconnectPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), jitter: () => 1.0),
            now: () => _clock,
            delay: Delay);
    }

    public AgentDaemon Daemon { get; }

    public RecordingLog Log { get; }

    public Charter.Agent.Logging.SecretScrubber Scrubber { get; }

    public List<TimeSpan> Delays { get; } = [];

    /// <summary>
    /// The session phase at the moment the test's condition held. Sampled there because stopping
    /// the daemon disconnects it, and a disconnected session is back to handshaking by definition.
    /// </summary>
    public SessionPhase ObservedPhase { get; private set; }

    private async Task Delay(TimeSpan span, CancellationToken cancellationToken)
    {
        lock (Delays)
        {
            Delays.Add(span);
        }

        _clock += span;

        // A short real yield keeps the loop cooperative without making the test wait on wall time.
        await Task.Delay(1, cancellationToken);
    }

    /// <summary>Runs the daemon until <paramref name="until"/> holds, then stops it.</summary>
    public async Task RunUntilAsync(Func<bool> until, int timeoutMs = 10_000)
    {
        var run = Daemon.RunAsync(_cancellation.Token);

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!until() && DateTime.UtcNow < deadline && !run.IsCompleted)
        {
            await Task.Delay(2);
        }

        var satisfied = until();
        ObservedPhase = Daemon.Current.Phase;
        await _cancellation.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(satisfied, "the daemon never reached the expected state");
    }

    public async Task<AgentExitReason> RunToCompletionAsync(int timeoutMs = 10_000)
    {
        var run = Daemon.RunAsync(_cancellation.Token);
        try
        {
            return await run.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }
        finally
        {
            await _cancellation.CancelAsync();
        }
    }
}
