namespace Charter.Runners;

/// <summary>What became of a run reference the execution plane reported.</summary>
public enum RunReferenceDecision
{
    /// <summary>The callback carried no reference. Nothing to record and nothing to refuse.</summary>
    Absent,

    /// <summary>The reference names a workflow run in the session's own repository.</summary>
    Accepted,

    /// <summary>The reference names something else. It is not recorded and the callback is refused.</summary>
    Rejected,
}

/// <summary>The verdict, plus a sentence for the refusal body and the operator's log.</summary>
public sealed record RunReferenceCheck(RunReferenceDecision Decision, string? Refusal)
{
    /// <summary>True when the reference may be folded into the session's external reference.</summary>
    public bool IsRecordable => Decision is RunReferenceDecision.Accepted;

    /// <summary>True when the callback that carried it must be refused.</summary>
    public bool IsRejected => Decision is RunReferenceDecision.Rejected;
}

/// <summary>
/// The gate every run reference reported by a runner passes before it is written down (sections 2.1, 16).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> <c>run_url</c> arrives from the execution plane, and the execution
/// plane is untrusted by construction: section 16 has the agent consuming repository content Charter
/// does not control, and the shim that posts the callback holds <c>CHARTER_EVENT_TOKEN</c> in the same
/// process. So <c>run_url</c> is attacker-influenced input, not a fact.
/// </para>
/// <para>
/// It was nonetheless folded straight into <see cref="Charter.Orchestration.SessionJournalSummary.ExternalReference"/>,
/// and <see cref="GitHubActionsRunner.TryParseRunReference"/> reads the <em>repository</em> back out of
/// it. Cancelling one session therefore issued
/// <c>POST /repos/{whatever-the-url-said}/actions/runs/{id}/cancel</c> with the instance's own
/// credential — a write against any other repository connected to the instance — while the run that
/// was supposed to stop was never touched and section 11's cancel path was told it had been.
/// </para>
/// <para>
/// <strong>Reject rather than sanitise.</strong> A reference naming another repository is a lie, not a
/// formatting problem, so there is nothing to normalise: the callback carrying it is refused and no
/// reference is recorded. The session is left with whatever the control plane wrote at dispatch, which
/// is the only reference in the system that was never influenced by the runner.
/// </para>
/// <para>
/// The rule is deliberately narrow — an absolute <c>http(s)</c> URL, shaped like a workflow run, in
/// this session's own repository. Anything looser lets the plane hand back a reference the control
/// plane will later interpret as one of its own handles: <c>DockerRunner</c> reads the reference as a
/// container id and <c>AgentRunner</c> reads it as a job id, so a bare string or a
/// <c>charter-agent:job:…</c> URI is exactly as dangerous as a cross-repository URL.
/// </para>
/// </remarks>
public static class RunnerRunReference
{
    /// <summary>Longer than any real run URL, and short enough not to be a log-flooding tool.</summary>
    public const int MaxLength = 512;

    /// <summary>Decides whether <paramref name="runUrl"/> may be recorded against the session.</summary>
    /// <param name="runUrl">The <c>run_url</c> the runner reported. Untrusted.</param>
    /// <param name="sessionRepoFullName">
    /// The repository the session belongs to, read from its own aggregate — see
    /// <see cref="SessionCredentialGuard.SessionRepoFullNameAsync"/>. Never a value the caller supplied.
    /// </param>
    public static RunReferenceCheck Evaluate(string? runUrl, string? sessionRepoFullName)
    {
        if (string.IsNullOrWhiteSpace(runUrl))
        {
            return new RunReferenceCheck(RunReferenceDecision.Absent, null);
        }

        // No repository behind the session means nothing to compare against, so there is no way to
        // establish the reference is this session's. Fail closed.
        if (string.IsNullOrWhiteSpace(sessionRepoFullName))
        {
            return Reject(
                "Charter cannot tell which repository this session belongs to, so it will not record a "
                + "run reference for it.");
        }

        var candidate = runUrl.Trim();

        if (candidate.Length > MaxLength)
        {
            return Reject(Wrong(sessionRepoFullName));
        }

        // `charter-agent:job:…` parses as an absolute URI, and a bare container id does not parse at
        // all. Both are references the control plane mints for itself, and neither may arrive from a
        // runner, so the scheme is checked before the shape.
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)))
        {
            return Reject(Wrong(sessionRepoFullName));
        }

        if (!GitHubActionsRunner.TryParseRunReference(candidate, out var named, out _))
        {
            return Reject(Wrong(sessionRepoFullName));
        }

        // Repository names are case-insensitive on GitHub, so Ordinal here would refuse honest runs.
        return string.Equals(named, sessionRepoFullName, StringComparison.OrdinalIgnoreCase)
            ? new RunReferenceCheck(RunReferenceDecision.Accepted, null)
            : Reject(Wrong(sessionRepoFullName));
    }

    /// <summary>A bounded, single-line rendering of a rejected reference, for the operator's log.</summary>
    /// <remarks>
    /// Structured logging already keeps this out of the message template, so it cannot forge a log
    /// line; the trimming is about a runner that reports a megabyte, and the newline stripping is so a
    /// plain-text sink still shows one event per line. Nothing here is ever a token (section 19) — the
    /// event token travels in the <c>Authorization</c> header and is never part of a reference.
    /// </remarks>
    public static string Describe(string? runUrl)
    {
        if (string.IsNullOrWhiteSpace(runUrl))
        {
            return "(none)";
        }

        var flattened = runUrl.Trim().ReplaceLineEndings(" ");

        return flattened.Length > MaxLength ? flattened[..MaxLength] + "…" : flattened;
    }

    private static RunReferenceCheck Reject(string refusal)
        => new(RunReferenceDecision.Rejected, refusal);

    private static string Wrong(string repoFullName)
        => $"The run reference in this callback does not name a workflow run in {repoFullName}, which is "
           + "the repository this session belongs to. Charter refuses references it cannot attribute to "
           + "the session's own repository, so nothing was recorded. If the repository was renamed on "
           + "GitHub, reconnect it in Charter so the two agree.";
}
