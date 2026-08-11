namespace Charter.VersionControl;

/// <summary>What became of a branch name the execution plane reported.</summary>
public enum BranchReferenceDecision
{
    /// <summary>The transcript carried no <c>branch_pushed</c>. The convention applies.</summary>
    Absent,

    /// <summary>The reported branch is this session's own branch.</summary>
    Accepted,

    /// <summary>The reported branch is some other ref. Nothing is published.</summary>
    Rejected,
}

/// <summary>The verdict, plus a sentence for the refusal and for the operator's log.</summary>
/// <param name="Decision">What was decided.</param>
/// <param name="Refusal">Plain English, safe to show an engineer. Null unless rejected.</param>
public sealed record BranchReferenceCheck(BranchReferenceDecision Decision, string? Refusal)
{
    /// <summary>True when the branch may be published.</summary>
    public bool IsAccepted => Decision is BranchReferenceDecision.Accepted;

    /// <summary>True when the session's work must not be published at all.</summary>
    public bool IsRejected => Decision is BranchReferenceDecision.Rejected;
}

/// <summary>
/// The gate every branch name reported by a runner passes before Charter moves a ref (sections 2.1, 16.3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> Section 16.3: a value that arrives from the execution plane may be
/// displayed, but it may never be the thing the control plane addresses. <c>branch_pushed</c> carries a
/// <c>branch</c>, and the shim that posts it holds <c>CHARTER_EVENT_TOKEN</c> in the same process as an
/// agent reading untrusted repository content — so the name in it is attacker-influenced input, not a fact.
/// </para>
/// <para>
/// It was nonetheless published verbatim: <see cref="ChangeRequestPublisher"/> took the reported branch and
/// <see cref="IVersionControlProvider.PushAsync"/> issued
/// <c>PATCH /repos/{owner}/{name}/git/refs/heads/{branch}</c> against it with a contribute-scoped token. The
/// push is fast-forward-only, and that is not a defence: an agent's commit is a descendant of the base
/// branch in exactly the normal case, so a session could advance <c>main</c> — the ref branch protection and
/// CODEOWNERS exist to guard — without a merge, a review, or a change request. Nothing in the publish path
/// consulted <see cref="IVersionControlProvider.GetBranchProtectionAsync"/>, and it should not have to: the
/// control plane already knows which branch this session's work belongs on.
/// </para>
/// <para>
/// <strong>Reject rather than sanitise.</strong> A <c>branch_pushed</c> naming another ref is a lie, not a
/// formatting problem. Quietly substituting the session's own branch would publish a revision that was
/// never pushed to it and turn an attack into a silent near-miss nobody investigates, so the publication is
/// refused whole, loudly, and the session ends rather than being retried by the next sweep.
/// </para>
/// <para>
/// The rule is strict equality with <see cref="ChangeRequestPublisher.BranchFor"/>, because that is the only
/// branch any dispatch ever names — <c>SessionDispatchPlanner</c>, <c>GitHubActionsRunner</c>,
/// <c>DockerRunner</c> and the shim all compute it from the session id and nothing else. There is no honest
/// case where a runner lands work somewhere else.
/// </para>
/// </remarks>
public static class SessionBranchReference
{
    /// <summary>Longer than any real branch name, and short enough not to be a log-flooding tool.</summary>
    public const int MaxLength = 512;

    /// <summary>Decides whether <paramref name="branch"/> may be published for this session.</summary>
    /// <param name="branch">The branch the runner reported. Untrusted.</param>
    /// <param name="sessionId">The session, read from its own row. Never a value the caller supplied.</param>
    public static BranchReferenceCheck Evaluate(string? branch, Guid sessionId)
    {
        if (string.IsNullOrWhiteSpace(branch))
        {
            return new BranchReferenceCheck(BranchReferenceDecision.Absent, null);
        }

        var expected = ChangeRequestPublisher.BranchFor(sessionId);
        var candidate = branch.Trim();

        // Ordinal, not OrdinalIgnoreCase: git refs are case-sensitive, so `Charter/Session-…` names a
        // different ref from `charter/session-…` and treating the two as one would be a rewrite.
        return candidate.Length <= MaxLength && string.Equals(candidate, expected, StringComparison.Ordinal)
            ? new BranchReferenceCheck(BranchReferenceDecision.Accepted, null)
            : new BranchReferenceCheck(BranchReferenceDecision.Rejected, Wrong(expected));
    }

    /// <summary>A bounded, single-line rendering of a refused branch, for the operator's log.</summary>
    /// <remarks>
    /// Structured logging keeps this out of the message template, so it cannot forge a log line; the
    /// trimming is about a runner that reports a megabyte, and the newline stripping is so a plain-text
    /// sink still shows one event per line. Nothing here is ever a token (section 19) — the event token
    /// travels in the <c>Authorization</c> header and is never part of a branch name.
    /// </remarks>
    public static string Describe(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
        {
            return "(none)";
        }

        var flattened = branch.Trim().ReplaceLineEndings(" ");

        return flattened.Length > MaxLength ? flattened[..MaxLength] + "…" : flattened;
    }

    private static string Wrong(string expected)
        => $"The runner reported publishing to a branch other than this session's own ({expected}). Charter "
           + "moves no ref it did not name itself, so nothing was published and no change request was "
           + "opened. The session's work is still on whatever the runner actually pushed; an engineer can "
           + "read it there.";
}
