using System.Globalization;
using System.Threading.Channels;
using Charter.Agent.Capabilities;
using Charter.Agent.Execution;
using Charter.Agent.Jobs;
using Charter.Agent.Logging;
using Charter.Agent.Protocol;
using Charter.Agent.Session;
using Charter.Agent.Transport;

namespace Charter.Agent;

/// <summary>Why the daemon stopped, and what an operator should do about it.</summary>
public enum AgentExitReason
{
    /// <summary>Ctrl-C, SIGTERM, or the service manager stopping it. Exit code 0.</summary>
    Stopped,

    /// <summary>The credential was revoked from the UI. Re-pair to come back.</summary>
    Revoked,
}

/// <summary>
/// The daemon: dial out, register, claim, run, repeat (section 33).
/// </summary>
/// <remarks>
/// Everything time-dependent is decided by <see cref="AgentSession"/> from an injected clock, and
/// everything network-dependent goes through <see cref="IAgentTransport"/>. What is left here is
/// plumbing: connect, pump frames, start and stop jobs, reconnect with backoff.
/// </remarks>
public sealed class AgentDaemon
{
    private readonly AgentOptions _options;
    private readonly IAgentTransportFactory _transportFactory;
    private readonly IJobExecutor _executor;
    private readonly CapabilityProber _prober;
    private readonly IAgentLog _log;
    private readonly SecretScrubber _scrubber;
    private readonly ReconnectPolicy _reconnect;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private readonly Channel<AgentSignal> _signals = Channel.CreateUnbounded<AgentSignal>();
    private readonly Dictionary<string, RunningJobHandle> _running = new(StringComparer.Ordinal);

    private string _agentToken;
    private CapabilitySet _capabilities;
    private AgentSession _session;

    public AgentDaemon(
        AgentOptions options,
        string agentToken,
        CapabilitySet capabilities,
        IAgentTransportFactory transportFactory,
        IJobExecutor executor,
        CapabilityProber prober,
        IAgentLog log,
        SecretScrubber scrubber,
        ReconnectPolicy? reconnect = null,
        Func<DateTimeOffset>? now = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentToken);
        ArgumentNullException.ThrowIfNull(capabilities);

        _options = options;
        _agentToken = agentToken;
        _capabilities = capabilities;
        _transportFactory = transportFactory;
        _executor = executor;
        _prober = prober;
        _log = log;
        _scrubber = scrubber;
        _reconnect = reconnect ?? new ReconnectPolicy();
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;

        _scrubber.Register(agentToken);
        _session = new AgentSession(options, capabilities, _now());
    }

    /// <summary>The session state, for tests and for the startup banner.</summary>
    public AgentSession Current => _session;

    /// <summary>Connect, work, reconnect. Returns when cancelled or when the credential is revoked.</summary>
    public async Task<AgentExitReason> RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            IAgentTransport? transport = null;
            try
            {
                transport = await _transportFactory.ConnectAsync(_agentToken, cancellationToken);
                _log.Info($"connected to {_options.Server} (outbound; no inbound port is open on this host)");

                var close = await PumpAsync(transport, cancellationToken);
                if (close?.CredentialRevoked == true)
                {
                    return AgentExitReason.Revoked;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    await SayGoodbyeAsync(transport);
                }
            }
            catch (TransportClosedException exception)
            {
                _log.Warn(exception.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Hand the leases back rather than making the queue wait out every TTL.
                if (transport is not null)
                {
                    await SayGoodbyeAsync(transport);
                }

                break;
            }
            finally
            {
                if (transport is not null)
                {
                    await transport.DisposeAsync();
                }
            }

            _session.Disconnected();

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var wait = _reconnect.NextDelay();
            _log.Info(string.Create(
                CultureInfo.InvariantCulture,
                $"reconnecting in {wait.TotalSeconds:0.0}s (attempt {_reconnect.Attempt})"));

            try
            {
                await _delay(wait, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await StopAllJobsAsync("the agent is shutting down");
        return AgentExitReason.Stopped;
    }

    /// <summary>One connection: handshake, then frames and timers until it ends.</summary>
    private async Task<SessionClose?> PumpAsync(IAgentTransport transport, CancellationToken cancellationToken)
    {
        var step = _session.Open(_now());
        var close = await ApplyAsync(step, transport, cancellationToken);
        if (close is not null)
        {
            return close;
        }

        Task<Envelope?>? receive = null;
        Task<bool>? signalled = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            receive ??= transport.ReceiveAsync(cancellationToken);
            signalled ??= _signals.Reader.WaitToReadAsync(cancellationToken).AsTask();

            // The idle wait is cancelled the moment something else wins the race, so a long-lived
            // connection does not accumulate one pending timer per frame it receives.
            using var idleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var wait = step.NextWakeAt - _now();
            var idle = _delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, idleCancellation.Token);

            var finished = await Task.WhenAny(receive, signalled, idle);
            if (finished != idle)
            {
                await idleCancellation.CancelAsync();
            }

            if (finished == receive)
            {
                var envelope = await receive;
                receive = null;
                if (envelope is null)
                {
                    _log.Warn(
                        "the control plane closed the connection" +
                        (transport.CloseDescription is { Length: > 0 } why ? $": {why}" : "."));
                    return null;
                }

                if (_reconnect.Attempt > 0 && envelope.Type == MessageTypes.Welcome)
                {
                    _reconnect.Reset();
                }

                step = _session.Receive(envelope, _now());
            }
            else if (finished == signalled)
            {
                await signalled;
                signalled = null;
                step = await DrainSignalsAsync(transport, cancellationToken);
            }
            else
            {
                step = _session.Advance(_now());
            }

            close = await ApplyAsync(step, transport, cancellationToken);
            if (close is not null)
            {
                return close;
            }
        }

        return null;
    }

    /// <summary>
    /// Job output, job completions and finished probes, folded back into the session.
    /// </summary>
    /// <remarks>
    /// Each signal's step is applied here and only here; the step handed back to the pump is a fresh
    /// one, so nothing is sent twice. A duplicated <c>job.result</c> would look to the control plane
    /// like a second report for a lease it had already released.
    /// </remarks>
    private async Task<SessionStep> DrainSignalsAsync(IAgentTransport transport, CancellationToken cancellationToken)
    {
        while (_signals.Reader.TryRead(out var signal))
        {
            if (signal.Event is { } jobEvent)
            {
                await transport.SendAsync(jobEvent, cancellationToken);
                continue;
            }

            if (signal.Completion is { } completion)
            {
                Forget(completion.JobId);
                await ApplyAsync(_session.JobFinished(completion, _now()), transport, cancellationToken);
                continue;
            }

            if (signal.Capabilities is { } capabilities)
            {
                _capabilities = capabilities;
                await ApplyAsync(_session.CapabilitiesRefreshed(capabilities, _now()), transport, cancellationToken);
            }
        }

        return _session.Advance(_now());
    }

    private async Task<SessionClose?> ApplyAsync(
        SessionStep step,
        IAgentTransport transport,
        CancellationToken cancellationToken)
    {
        foreach (var note in step.Notes)
        {
            _log.Write(note.Level, note.Message);
        }

        foreach (var envelope in step.Send)
        {
            await transport.SendAsync(envelope, cancellationToken);
        }

        foreach (var stop in step.Stop)
        {
            StopJob(stop);
        }

        foreach (var job in step.Start)
        {
            StartJob(job, cancellationToken);
        }

        if (step.ReprobeRequested)
        {
            _ = ReprobeAsync(cancellationToken);
        }

        if (step.Close is { } close)
        {
            _log.Warn("closing the connection: " + close.Reason);
        }

        return step.Close;
    }

    /// <summary>
    /// Releases every held lease on the way out, so a planned restart does not leave its jobs
    /// stranded for the length of their TTL.
    /// </summary>
    private async Task SayGoodbyeAsync(IAgentTransport transport)
    {
        var step = _session.Drain("the agent is shutting down", _now());

        foreach (var note in step.Notes)
        {
            _log.Write(note.Level, note.Message);
        }

        foreach (var stop in step.Stop)
        {
            StopJob(stop);
        }

        foreach (var envelope in step.Send)
        {
            try
            {
                await transport.SendAsync(envelope, CancellationToken.None);
            }
            catch (TransportClosedException)
            {
                // The link is already gone; the leases will lapse on their own.
                return;
            }
        }
    }

    /// <summary>
    /// Starts one granted job, unless one of that id is already running here.
    /// </summary>
    /// <remarks>
    /// The guard is not redundant with <see cref="AgentSession"/>'s. Overwriting the handle would drop
    /// the first job's <see cref="CancellationTokenSource"/> on the floor: the shim would keep running
    /// with nothing holding its token, so cancel, revoke and lease expiry would all reach the second
    /// copy and none of them the first — and the first would go on to push a branch. A daemon that
    /// cannot stop what it started is worse than one that refuses to start twice.
    /// </remarks>
    private void StartJob(JobAssignment job, CancellationToken cancellationToken)
    {
        if (_running.ContainsKey(job.JobId))
        {
            _log.Warn($"job {job.JobId}: already running on this host; ignoring the duplicate grant");
            return;
        }

        // Register every secret before anything can print it, and forget it when the job ends.
        _scrubber.Register(job.Secrets?.Values());

        var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sink = new ChannelJobEventSink(_signals.Writer, _scrubber, _now);

        var task = Task.Run(
            async () =>
            {
                JobCompletion completion;
                try
                {
                    completion = await _executor.ExecuteAsync(job, sink, jobCancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    completion = new JobCompletion(job.JobId, JobOutcomes.Cancelled, null, "stopped");
                }
#pragma warning disable CA1031 // A crashing executor must still report, or the lease hangs until it lapses.
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    completion = new JobCompletion(
                        job.JobId, JobOutcomes.Failed, null, _scrubber.Scrub(exception.Message));
                }

                await _signals.Writer.WriteAsync(new AgentSignal(Completion: completion), CancellationToken.None);
            },
            CancellationToken.None);

        _running[job.JobId] = new RunningJobHandle(job, jobCancellation, task);
    }

    private void StopJob(JobStop stop)
    {
        if (!_running.TryGetValue(stop.JobId, out var handle))
        {
            return;
        }

        _log.Info($"job {stop.JobId}: stopping - {stop.Reason}");
        handle.Cancellation.Cancel();
    }

    private async Task StopAllJobsAsync(string reason)
    {
        foreach (var handle in _running.Values)
        {
            _log.Info($"job {handle.Job.JobId}: stopping - {reason}");
            await handle.Cancellation.CancelAsync();
        }

        foreach (var handle in _running.Values.ToArray())
        {
            try
            {
                await handle.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }
            catch (TimeoutException)
            {
                _log.Warn($"job {handle.Job.JobId}: did not stop in time");
            }

            Forget(handle.Job.JobId);
        }

        _running.Clear();
    }

    private void Forget(string jobId)
    {
        if (_running.Remove(jobId, out var handle))
        {
            _scrubber.Forget(handle.Job.Secrets?.Values());
            handle.Cancellation.Dispose();
        }
    }

    private async Task ReprobeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var probed = await _prober.ProbeAsync(_options.Mode, _now(), cancellationToken);
            await _signals.Writer.WriteAsync(new AgentSignal(Capabilities: probed), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Shutting down mid-probe is not worth a line.
        }
    }

    /// <summary>Replaces the credential in memory, after a re-pair.</summary>
    public void UseCredential(string agentToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentToken);
        _scrubber.Register(agentToken);
        _agentToken = agentToken;
    }

    private sealed record AgentSignal(
        JobCompletion? Completion = null,
        Envelope? Event = null,
        CapabilitySet? Capabilities = null);

    private sealed record RunningJobHandle(
        JobAssignment Job,
        CancellationTokenSource Cancellation,
        Task Task);

    /// <summary>Streams a job's output back as <c>job.event</c> frames, scrubbed and length-capped.</summary>
    private sealed class ChannelJobEventSink(
        ChannelWriter<AgentSignal> writer,
        SecretScrubber scrubber,
        Func<DateTimeOffset> now) : IJobEventSink
    {
        private const int MaxLineLength = 4000;

        private readonly Dictionary<string, long> _sequences = new(StringComparer.Ordinal);
        private readonly Lock _gate = new();

        public void Publish(string jobId, string kind, string message)
        {
            long sequence;
            lock (_gate)
            {
                sequence = _sequences.TryGetValue(jobId, out var last) ? last + 1 : 1;
                _sequences[jobId] = sequence;
            }

            var scrubbed = scrubber.Scrub(message);
            if (scrubbed.Length > MaxLineLength)
            {
                scrubbed = scrubbed[..MaxLineLength] + "… (truncated)";
            }

            var envelope = Envelope.Create(
                MessageTypes.JobEvent,
                new JobEventPayload
                {
                    JobId = jobId,
                    Sequence = sequence,
                    Kind = kind,
                    Message = scrubbed,
                    At = now(),
                },
                now());

            writer.TryWrite(new AgentSignal(Event: envelope));
        }
    }
}
