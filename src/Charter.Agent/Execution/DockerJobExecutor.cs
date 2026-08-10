using System.Diagnostics;
using System.Globalization;
using Charter.Agent.Jobs;
using Charter.Agent.Logging;
using Charter.Agent.Protocol;

namespace Charter.Agent.Execution;

/// <summary>
/// Runs each job in an ephemeral container spawned through the local Docker socket (section 33.2).
/// </summary>
/// <remarks>
/// The default mode, and the one to use wherever it is possible. The container is created, started,
/// followed, waited on, and removed - nothing survives it but the named cache volumes, which are
/// scoped per repository because a cache shared across repositories is a cross-repo contamination
/// path (section 32.3).
/// <para>
/// Per-job secrets are passed as container environment. They are never written to the image, never
/// baked into a layer, and never put on a command line where <c>ps</c> would show them.
/// </para>
/// </remarks>
public sealed class DockerJobExecutor(
    AgentOptions options,
    DockerSocketClient docker,
    IAgentLog log) : IJobExecutor
{
    private readonly AgentOptions _options = options;
    private readonly DockerSocketClient _docker = docker;
    private readonly IAgentLog _log = log;

    public string Describe() => $"docker, via the local socket at {_docker.SocketPath}";

    public async Task<IReadOnlyList<string>> PreflightAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_docker.SocketPath) && !Directory.Exists(_docker.SocketPath))
        {
            return
            [
                $"the Docker socket {_docker.SocketPath} does not exist. Start Docker, point " +
                "--docker-socket at the right path, or run with --mode native.",
            ];
        }

        return await _docker.PingAsync(cancellationToken)
            ? []
            :
            [
                $"the Docker daemon at {_docker.SocketPath} did not answer. Check it is running and " +
                $"that {Environment.UserName} may use it (on Linux, membership of the docker group).",
            ];
    }

    public async Task<JobCompletion> ExecuteAsync(
        JobAssignment job,
        IJobEventSink events,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(events);

        if (string.IsNullOrWhiteSpace(job.RunnerImage))
        {
            return new JobCompletion(
                job.JobId,
                JobOutcomes.Failed,
                null,
                "the job carried no runner image, and docker mode has nothing to run it in. Set " +
                "runner_image in .charter/config.yml.");
        }

        var started = Stopwatch.GetTimestamp();
        var containerId = string.Empty;

        try
        {
            var request = new DockerCreateContainer
            {
                Image = job.RunnerImage,
                Cmd = [job.Command.Executable, .. job.Command.Arguments],
                Env = [.. JobEnvironment.Build(job).Select(pair => $"{pair.Key}={pair.Value}")],
                WorkingDir = "/workspace",
                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["com.charter.job"] = job.JobId,
                    ["com.charter.agent"] = _options.Name,
                },
                HostConfig = new DockerHostConfig
                {
                    AutoRemove = false,
                    Binds = CacheBinds(job),
                },
            };

            containerId = await _docker.CreateContainerAsync($"charter-job-{job.JobId}", request, cancellationToken);
            events.Publish(job.JobId, "started", $"container from {job.RunnerImage}");

            await _docker.StartContainerAsync(containerId, cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (job.TimeoutSeconds is { } seconds and > 0)
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
            }

            var logs = FollowAsync(containerId, job.JobId, events, timeout.Token);

            int exitCode;
            try
            {
                exitCode = await _docker.WaitAsync(containerId, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                await StopAsync(containerId);
                await logs;
                var cancelled = cancellationToken.IsCancellationRequested;
                return new JobCompletion(
                    job.JobId,
                    cancelled ? JobOutcomes.Cancelled : JobOutcomes.Failed,
                    null,
                    cancelled ? "stopped by the control plane" : "exceeded its wall-clock limit",
                    Elapsed(started));
            }

            await logs;

            return new JobCompletion(
                job.JobId,
                exitCode == 0 ? JobOutcomes.Succeeded : JobOutcomes.Failed,
                exitCode,
                exitCode == 0
                    ? null
                    : string.Create(CultureInfo.InvariantCulture, $"the container exited {exitCode}"),
                Elapsed(started));
        }
        catch (DockerApiException exception)
        {
            return new JobCompletion(job.JobId, JobOutcomes.Failed, null, exception.Message, Elapsed(started));
        }
        catch (HttpRequestException exception)
        {
            return new JobCompletion(
                job.JobId, JobOutcomes.Failed, null, "the Docker daemon went away: " + exception.Message, Elapsed(started));
        }
        finally
        {
            if (containerId.Length > 0)
            {
                await RemoveAsync(containerId);
            }
        }
    }

    /// <summary>
    /// Package caches and the git mirror, in named volumes keyed by repository. Scoping is a
    /// security requirement, not an optimisation (section 32.3).
    /// </summary>
    private static IReadOnlyList<string> CacheBinds(JobAssignment job)
    {
        var scope = job.Repo?.CacheScope ?? job.Repo?.FullName;
        if (string.IsNullOrWhiteSpace(scope))
        {
            return [];
        }

        var slug = Capabilities.CapabilityParsers.Slug(scope);
        return
        [
            $"charter-cache-{slug}-nuget:/root/.nuget/packages",
            $"charter-cache-{slug}-npm:/root/.npm",
            $"charter-mirror-{slug}:/mirrors",
        ];
    }

    private async Task FollowAsync(string containerId, string jobId, IJobEventSink events, CancellationToken cancellationToken)
    {
        try
        {
            await _docker.FollowLogsAsync(
                containerId, (kind, line) => events.Publish(jobId, kind, line), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The job was stopped; its output ends there.
        }
        catch (DockerApiException exception)
        {
            _log.Warn($"job {jobId}: log stream ended early: {exception.Message}");
        }
        catch (HttpRequestException exception)
        {
            _log.Warn($"job {jobId}: log stream ended early: {exception.Message}");
        }
    }

    private async Task StopAsync(string containerId)
    {
        try
        {
            await _docker.StopContainerAsync(containerId, timeoutSeconds: 10, CancellationToken.None);
        }
        catch (DockerApiException exception)
        {
            _log.Warn($"could not stop container {containerId}: {exception.Message}");
        }
        catch (HttpRequestException exception)
        {
            _log.Warn($"could not stop container {containerId}: {exception.Message}");
        }
    }

    private async Task RemoveAsync(string containerId)
    {
        try
        {
            await _docker.RemoveContainerAsync(containerId, CancellationToken.None);
        }
        catch (DockerApiException exception)
        {
            _log.Warn($"could not remove container {containerId}: {exception.Message}");
        }
        catch (HttpRequestException exception)
        {
            _log.Warn($"could not remove container {containerId}: {exception.Message}");
        }
    }

    private static long Elapsed(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
