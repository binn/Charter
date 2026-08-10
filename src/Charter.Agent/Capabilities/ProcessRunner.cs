using System.Diagnostics;
using System.Text;

namespace Charter.Agent.Capabilities;

/// <summary>
/// The result of trying to run a probe command.
/// </summary>
/// <param name="Started">
/// False when the executable is not on this host at all. That is the ordinary case for most probes —
/// a Linux box has no <c>xcodebuild</c> — and it must not be reported as a failure.
/// </param>
/// <param name="ExitCode">Process exit code, or -1 when it never started.</param>
/// <param name="StandardOutput">Captured stdout.</param>
/// <param name="StandardError">Captured stderr.</param>
public sealed record ProcessResult(bool Started, int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => Started && ExitCode == 0;

    public static ProcessResult NotFound { get; } = new(false, -1, string.Empty, string.Empty);

    public static ProcessResult Ok(string standardOutput) => new(true, 0, standardOutput, string.Empty);
}

/// <summary>Runs short-lived commands. Stubbed in tests so probing never touches the host.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>The real one. Captures both streams, kills the process on timeout.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => Append(stdout, e.Data);
        process.ErrorDataReceived += (_, e) => Append(stderr, e.Data);

        try
        {
            if (!process.Start())
            {
                return ProcessResult.NotFound;
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The executable is not installed on this host. An expected outcome for most probes.
            return ProcessResult.NotFound;
        }
        catch (InvalidOperationException)
        {
            return ProcessResult.NotFound;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return new ProcessResult(true, -1, stdout.ToString(), "timed out");
        }

        return new ProcessResult(true, process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static void Append(StringBuilder builder, string? line)
    {
        if (line is not null)
        {
            builder.AppendLine(line);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        catch (NotSupportedException)
        {
            // Remote or otherwise unkillable; nothing useful to do.
        }
    }
}
