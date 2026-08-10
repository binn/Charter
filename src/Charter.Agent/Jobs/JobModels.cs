using Charter.Agent.Protocol;

namespace Charter.Agent.Jobs;

/// <summary>How a job ended, in the words the wire uses.</summary>
public static class JobOutcomes
{
    public const string Succeeded = "succeeded";

    public const string Failed = "failed";

    public const string Cancelled = "cancelled";

    /// <summary>The agent gave the job back: lease lost, capability gone, or it was never runnable.</summary>
    public const string Abandoned = "abandoned";
}

/// <summary>A finished job, ready to report.</summary>
public sealed record JobCompletion(
    string JobId,
    string Outcome,
    int? ExitCode = null,
    string? Error = null,
    long DurationMs = 0);

/// <summary>An instruction from the session to stop a running job.</summary>
/// <param name="JobId">The job to stop.</param>
/// <param name="Reason">Why, in words fit for a log line.</param>
/// <param name="Report">
/// Whether to send a result once it has stopped. False when the lease has lapsed and the control
/// plane has already re-queued the work — reporting then would contradict whoever picked it up.
/// </param>
public sealed record JobStop(string JobId, string Reason, bool Report);

/// <summary>Everything a runner needs for one job, plus the bookkeeping around it.</summary>
public sealed record RunningJob(JobAssignment Assignment, DateTimeOffset StartedAt);
