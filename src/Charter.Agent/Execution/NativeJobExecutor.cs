using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Charter.Agent.Capabilities;
using Charter.Agent.Jobs;
using Charter.Agent.Logging;
using Charter.Agent.Protocol;

namespace Charter.Agent.Execution;

/// <summary>
/// Runs jobs directly on the host under a dedicated unprivileged account (section 33.2).
/// </summary>
/// <remarks>
/// This mode exists because containers are not universally possible: macOS with Xcode cannot be
/// containerised, and a USB-attached embedded target is awkward to pass through. It is not the mode
/// to reach for otherwise.
/// <para>
/// <b>Isolation here is weaker than container mode and the daemon says so at startup.</b> It is
/// process-level: the job gets its own scoped working directory and, where the platform allows,
/// runs as a different user - but it shares the host's filesystem outside that directory, its
/// installed software, and its network position. A dedicated machine or VM is the right host for
/// this; an engineer's daily driver is not.
/// </para>
/// </remarks>
public sealed class NativeJobExecutor(
    AgentOptions options,
    IAgentLog log,
    IProcessRunner processRunner) : IJobExecutor
{
    private readonly AgentOptions _options = options;
    private readonly IAgentLog _log = log;
    private readonly IProcessRunner _processRunner = processRunner;

    public string Describe() =>
        _options.RunsJobsAsAgentUser
            ? $"native, as the agent's own user ({Environment.UserName}), work dir {_options.WorkDirectory}"
            : $"native, as user '{_options.NativeUser}', work dir {_options.WorkDirectory}";

    /// <summary>The warning the operator gets at startup rather than discovering later.</summary>
    public IReadOnlyList<string> IsolationWarnings()
    {
        var warnings = new List<string>
        {
            "native mode: isolation is process-level, not container-level. A job shares this host's " +
            "filesystem outside its working directory, its installed software and its network position.",
            "native mode: run this on a dedicated machine or VM, not on a daily driver with SSH keys, " +
            "browser sessions and cloud CLI credentials on it.",
        };

        if (_options.RunsJobsAsAgentUser)
        {
            warnings.Add(
                $"native mode: --native-user self was given, so jobs run as {Environment.UserName} with " +
                "no account boundary at all. A dedicated unprivileged account is the supported setup.");
        }

        return warnings;
    }

    public async Task<IReadOnlyList<string>> PreflightAsync(CancellationToken cancellationToken = default)
    {
        var problems = new List<string>();

        try
        {
            Directory.CreateDirectory(_options.WorkDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            problems.Add($"cannot create the work directory {_options.WorkDirectory}: {exception.Message}");
        }

        if (_options.RunsJobsAsAgentUser)
        {
            return problems;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            problems.Add(
                "native mode on Windows cannot switch to a dedicated account without that account's " +
                "credentials. Run the agent itself as the dedicated user and pass --native-user self, " +
                "on a machine dedicated to Charter.");
            return problems;
        }

        var lookup = await _processRunner.RunAsync("id", ["-u", _options.NativeUser], TimeSpan.FromSeconds(10), cancellationToken);
        if (!lookup.Succeeded)
        {
            problems.Add(
                $"the dedicated unprivileged account '{_options.NativeUser}' does not exist on this host. " +
                $"Create it (for example: sudo useradd --system --create-home {_options.NativeUser}), or " +
                "pass --native-user self to accept weaker isolation.");
            return problems;
        }

        var elevation = await _processRunner.RunAsync(
            "sudo", ["-n", "-u", _options.NativeUser, "--", "true"], TimeSpan.FromSeconds(10), cancellationToken);
        if (!elevation.Succeeded)
        {
            problems.Add(
                $"this agent cannot run commands as '{_options.NativeUser}'. Grant it a password-less " +
                $"sudo rule for that account, or pass --native-user self.");
        }

        return problems;
    }

    public async Task<JobCompletion> ExecuteAsync(
        JobAssignment job,
        IJobEventSink events,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(events);

        var started = Stopwatch.GetTimestamp();
        var jobDirectory = Path.Combine(_options.WorkDirectory, job.JobId);

        try
        {
            Directory.CreateDirectory(jobDirectory);
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                File.SetUnixFileMode(
                    jobDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);
            }

            var workingDirectory = job.Command.WorkingSubdirectory is { Length: > 0 } sub
                ? Path.Combine(jobDirectory, sub)
                : jobDirectory;

            // A working subdirectory that escapes the scoped directory is a control-plane bug, and
            // running it anyway would put agent-authored code outside the only boundary this mode has.
            if (!Path.GetFullPath(workingDirectory).StartsWith(Path.GetFullPath(jobDirectory), StringComparison.Ordinal))
            {
                return new JobCompletion(
                    job.JobId, JobOutcomes.Failed, null, "the job's working directory escaped its scope");
            }

            Directory.CreateDirectory(workingDirectory);

            var environment = JobEnvironment.Build(job);
            var startInfo = BuildStartInfo(job, workingDirectory, environment);

            events.Publish(job.JobId, "started", $"native: {job.Command.Executable}");

            return await RunAsync(job, startInfo, events, started, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new JobCompletion(
                job.JobId, JobOutcomes.Failed, null, exception.Message, Elapsed(started));
        }
        finally
        {
            TryDelete(jobDirectory);
        }
    }

    private ProcessStartInfo BuildStartInfo(
        JobAssignment job,
        string workingDirectory,
        Dictionary<string, string> environment)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (_options.RunsJobsAsAgentUser)
        {
            startInfo.FileName = job.Command.Executable;
            foreach (var argument in job.Command.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }
        else
        {
            // sudo, not `env VAR=value cmd`: an environment variable passed on a command line is
            // visible to every process on the host through ps. --preserve-env names the variables to
            // carry across, so the values stay in the process environment where they belong.
            startInfo.FileName = "sudo";
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add("-u");
            startInfo.ArgumentList.Add(_options.NativeUser);
            startInfo.ArgumentList.Add("--preserve-env=" + string.Join(',', environment.Keys.Order(StringComparer.Ordinal)));
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add(job.Command.Executable);
            foreach (var argument in job.Command.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        foreach (var pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        return startInfo;
    }

    private async Task<JobCompletion> RunAsync(
        JobAssignment job,
        ProcessStartInfo startInfo,
        IJobEventSink events,
        long started,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => Forward(events, job.JobId, "stdout", e.Data);
        process.ErrorDataReceived += (_, e) => Forward(events, job.JobId, "stderr", e.Data);

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            return new JobCompletion(
                job.JobId,
                JobOutcomes.Failed,
                null,
                $"could not start '{job.Command.Executable}': {exception.Message}. Sessions never install " +
                "a toolchain (section 32.1) - provision it on this host first.",
                Elapsed(started));
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (job.TimeoutSeconds is { } seconds and > 0)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        }

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            var cancelled = cancellationToken.IsCancellationRequested;
            return new JobCompletion(
                job.JobId,
                cancelled ? JobOutcomes.Cancelled : JobOutcomes.Failed,
                null,
                cancelled ? "stopped by the control plane" : "exceeded its wall-clock limit",
                Elapsed(started));
        }

        return new JobCompletion(
            job.JobId,
            process.ExitCode == 0 ? JobOutcomes.Succeeded : JobOutcomes.Failed,
            process.ExitCode,
            process.ExitCode == 0
                ? null
                : string.Create(CultureInfo.InvariantCulture, $"the job command exited {process.ExitCode}"),
            Elapsed(started));
    }

    private static void Forward(IJobEventSink events, string jobId, string kind, string? line)
    {
        if (!string.IsNullOrEmpty(line))
        {
            events.Publish(jobId, kind, line);
        }
    }

    private static long Elapsed(long started) =>
        (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (NotSupportedException)
        {
            // Nothing useful to do.
        }
    }

    private void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"could not clean up {directory}: {exception.Message}");
        }
    }
}
