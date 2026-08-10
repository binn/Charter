using Charter.Domain;

namespace Charter.Orchestration;

/// <summary>What a restarted control plane should do about one session.</summary>
public enum SessionRecoveryAction
{
    /// <summary>Nothing. Either it is finished, or somebody else's queued job will pick it up.</summary>
    None,

    /// <summary>
    /// A backend already has it. Carry on streaming from the recorded cursor and, above all, do not
    /// dispatch it again.
    /// </summary>
    Adopt,

    /// <summary>It was approved and queued but never handed to a backend. Enqueue the work.</summary>
    Dispatch,

    /// <summary>Somebody pressed cancel and the process died before the runner was stopped.</summary>
    Cancel,

    /// <summary>The runner reported a terminal result nobody applied to the session row.</summary>
    Settle,
}

/// <summary>The decision, with the reason it will be logged and journalled under.</summary>
/// <param name="ResumeFromSeq">
/// The last <see cref="Event.Seq"/> already durable. Streaming continues after it; anything the
/// runner replays below it is refused by the journal's idempotency, not de-duplicated by hand.
/// </param>
public sealed record SessionRecoveryPlan(
    SessionRecoveryAction Action,
    string Reason,
    long ResumeFromSeq,
    SessionStatus? SettleAs = null);

/// <summary>Everything the decision depends on, so the decision itself needs no database.</summary>
/// <param name="HasOpenJob">
/// True when a pending or claimed <see cref="JobType.Build"/> job already names this session. Without
/// this the recovery sweep and the queue would both enqueue, and the session would be built twice.
/// </param>
public sealed record SessionRecoveryInput(
    Guid SessionId,
    SessionStatus Status,
    bool CancelRequested,
    SessionJournalSummary Journal,
    bool HasOpenJob);

/// <summary>
/// The recovery rules of section 2.3, as a pure function.
/// </summary>
/// <remarks>
/// <para>
/// This is the part of the design that has to be right, so it is the part with no I/O in it. Given
/// what the session row says and what its event stream says, there is exactly one correct thing to do
/// on startup, and it can be enumerated in a table rather than discovered while debugging a
/// half-restarted production instance.
/// </para>
/// <para>
/// The ordering is not arbitrary. Cancellation outranks everything because a user asked for the run
/// to stop and the process died before it did. A reported terminal result outranks adoption because
/// the run is over and only the bookkeeping is outstanding. Adoption outranks dispatch because
/// dispatching a session a backend already holds is the one failure section 2.3 names outright.
/// </para>
/// </remarks>
public static class SessionRecovery
{
    /// <summary>The one correct action for this session, right now.</summary>
    public static SessionRecoveryPlan Decide(SessionRecoveryInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var cursor = input.Journal.LastSeq;

        if (IsTerminal(input.Status))
        {
            return new SessionRecoveryPlan(SessionRecoveryAction.None, "The session is already finished.", cursor);
        }

        if (input.CancelRequested)
        {
            return new SessionRecoveryPlan(
                SessionRecoveryAction.Cancel,
                "Cancellation was requested and never completed.",
                cursor);
        }

        if (input.Journal.TerminalState is { } terminal)
        {
            var settleAs = MapTerminal(terminal);

            return settleAs is null
                ? new SessionRecoveryPlan(
                    SessionRecoveryAction.Adopt,
                    "The runner reported completion; the pull request has not been bound yet.",
                    cursor)
                : new SessionRecoveryPlan(
                    SessionRecoveryAction.Settle,
                    $"The runner reported '{terminal}' and the session row was never updated.",
                    cursor,
                    settleAs);
        }

        if (input.Journal.Dispatched)
        {
            return new SessionRecoveryPlan(
                SessionRecoveryAction.Adopt,
                $"A {Describe(input.Journal.Runner)} run is already in flight; resuming from event {cursor}.",
                cursor);
        }

        if (input.HasOpenJob)
        {
            return new SessionRecoveryPlan(
                SessionRecoveryAction.None,
                "The build job is still in the queue and will be claimed by the dispatcher.",
                cursor);
        }

        return new SessionRecoveryPlan(
            SessionRecoveryAction.Dispatch,
            "The session was queued but never dispatched, and no job remains for it.",
            cursor);
    }

    /// <summary>
    /// Maps the runner's terminal word onto a session status, or null when it is not terminal here.
    /// </summary>
    /// <remarks>
    /// <c>completed</c> deliberately maps to nothing. Section 6 puts <c>PROpen</c> after
    /// <c>Running</c>, and opening the pull request is phase 3's job (section 23). Marking a session
    /// as having an open pull request because the agent process exited zero would be a claim Charter
    /// cannot back up.
    /// </remarks>
    public static SessionStatus? MapTerminal(string terminalState) => terminalState switch
    {
        "failed" => SessionStatus.Failed,
        "cancelled" => SessionStatus.Cancelled,
        "stale" => SessionStatus.Stale,
        _ => null,
    };

    private static bool IsTerminal(SessionStatus status) => status is SessionStatus.Merged
        or SessionStatus.Failed
        or SessionStatus.Cancelled
        or SessionStatus.Stale
        or SessionStatus.HandedOff;

    private static string Describe(RunnerKind? kind) => kind switch
    {
        RunnerKind.Agent => "Charter Agent",
        RunnerKind.GitHubActions => "GitHub Actions",
        RunnerKind.Docker => "Docker",
        _ => "runner",
    };
}
