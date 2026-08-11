using Charter.Data;
using Charter.Orchestration;
using Microsoft.EntityFrameworkCore;

namespace Charter.Runners;

/// <summary>Why a session may not be given credentials, or <see cref="None"/> when it may.</summary>
public enum SessionCredentialRefusal
{
    /// <summary>The session is live, dispatched, and belongs to a repository Charter knows.</summary>
    None,

    /// <summary>No such session, or no repository behind it.</summary>
    UnknownSession,

    /// <summary>The session reached a terminal state (section 6). Nothing is running to hold a token.</summary>
    Ended,

    /// <summary>Cancel was pressed. The runner is being stopped, not started.</summary>
    Cancelled,

    /// <summary>No backend holds this session, so nothing legitimate is asking for its credentials.</summary>
    NotDispatched,
}

/// <summary>
/// The verdict, plus the repository the session's token may be scoped to.
/// </summary>
/// <param name="Refusal">Why not, or <see cref="SessionCredentialRefusal.None"/>.</param>
/// <param name="RepoFullName">
/// The repository the <em>session</em> belongs to, read from its own aggregate. The one repository a
/// token for this session may be minted for (sections 7.4, 33.5) — never one a caller named.
/// </param>
public sealed record SessionCredentialDecision(SessionCredentialRefusal Refusal, string? RepoFullName)
{
    public bool Allowed => Refusal is SessionCredentialRefusal.None;

    /// <summary>A sentence for a log line or a refusal body. Names no session and no repository.</summary>
    public string Explanation => Refusal switch
    {
        SessionCredentialRefusal.None => "This session may be given credentials.",
        SessionCredentialRefusal.Ended =>
            "This session has already ended, so no credentials will be issued for it.",
        SessionCredentialRefusal.Cancelled =>
            "This session is being cancelled, so no credentials will be issued for it.",
        SessionCredentialRefusal.NotDispatched =>
            "This session has not been dispatched, so no credentials will be issued for it.",
        _ => "There is no live session to issue credentials for.",
    };
}

/// <summary>
/// Thrown where a mint is attempted for a session that may not have one.
/// </summary>
/// <remarks>
/// An exception rather than a return value because the agent plane's mint is called from inside a
/// claim loop that already distinguishes "could not mint" from "granted", and this outcome needs a
/// third answer: not a transient failure to retry, but dead work to settle.
/// </remarks>
public sealed class SessionNotLiveException : InvalidOperationException
{
    public SessionNotLiveException(SessionCredentialDecision decision, string? message = null)
        : base(message ?? Describe(decision))
        => Refusal = decision.Refusal;

    public SessionNotLiveException()
        : base("There is no live session to issue credentials for.")
    {
    }

    public SessionNotLiveException(string message)
        : base(message)
    {
    }

    public SessionNotLiveException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Which of the refusals of <see cref="SessionCredentialRefusal"/> applied.</summary>
    public SessionCredentialRefusal Refusal { get; } = SessionCredentialRefusal.UnknownSession;

    private static string Describe(SessionCredentialDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return decision.Explanation;
    }
}

/// <summary>
/// The one gate every credential mint passes, whichever plane asked (sections 7.4, 16, 33.5).
/// </summary>
/// <remarks>
/// <para>
/// A session's credential is its blast radius. Section 16 makes that explicit: what a compromised
/// session can reach is exactly the repository token it holds, for exactly as long as it holds it. So
/// the window in which one can be minted has to be the window in which a session is genuinely running
/// — not "whenever a caller who once held a valid secret asks".
/// </para>
/// <para>
/// This is shared rather than written twice because it was written once. The HTTP exchange checked
/// the session and the agent plane did not, so a job whose session had been cancelled — and
/// <c>JobQueue.FailAsync</c> puts a cancelled session's job back to <c>Pending</c> with an attempt
/// left, so this is an ordinary path and not a contrived one — was still worth a contribute-scoped
/// GitHub token and a twelve-hour event token to whichever agent claimed it next.
/// </para>
/// </remarks>
public static class SessionCredentialGuard
{
    /// <summary>Reads the session and its journal, and says whether a mint is allowed.</summary>
    public static async Task<SessionCredentialDecision> EvaluateAsync(
        CharterDbContext db,
        SessionJournal journal,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(journal);

        var session = await db.Sessions
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == sessionId, cancellationToken);

        if (session is null)
        {
            return new SessionCredentialDecision(SessionCredentialRefusal.UnknownSession, null);
        }

        if (session.IsTerminal)
        {
            return new SessionCredentialDecision(SessionCredentialRefusal.Ended, null);
        }

        // Section 11: the cancel button must reach the runner. A token minted while the cancel is in
        // flight outlives the thing that justified it by up to twelve hours.
        if (session.CancelRequestedAt is not null)
        {
            return new SessionCredentialDecision(SessionCredentialRefusal.Cancelled, null);
        }

        var repo = await RepoFullNameAsync(db, session.SpecId, cancellationToken);
        if (repo is null)
        {
            return new SessionCredentialDecision(SessionCredentialRefusal.UnknownSession, null);
        }

        // The dispatch claim is written before any backend is called (SessionCoordinator), so a
        // session an agent can claim work for has always passed this. Its absence means nothing
        // legitimate is running.
        var summary = await journal.SummarizeAsync(sessionId, cancellationToken);

        return summary.Dispatched
            ? new SessionCredentialDecision(SessionCredentialRefusal.None, repo)
            : new SessionCredentialDecision(SessionCredentialRefusal.NotDispatched, repo);
    }

    /// <summary>
    /// The repository a session belongs to, read from its own aggregate.
    /// </summary>
    /// <remarks>
    /// The trustworthy answer to "which repository is this session allowed to touch", for callers that
    /// need only that and not the whole verdict — <see cref="RunnerRunReference"/> validating a
    /// <c>run_url</c> against it, or a cancel checking the reference it is about to act on. Kept beside
    /// <see cref="EvaluateAsync"/> so there is exactly one join from a session to its repository, and
    /// so no caller is tempted to take the repository from something the execution plane sent.
    /// </remarks>
    public static async Task<string?> SessionRepoFullNameAsync(
        CharterDbContext db,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var specId = await db.Sessions
            .AsNoTracking()
            .Where(candidate => candidate.Id == sessionId)
            .Select(candidate => (Guid?)candidate.SpecId)
            .FirstOrDefaultAsync(cancellationToken);

        return specId is { } id ? await RepoFullNameAsync(db, id, cancellationToken) : null;
    }

    /// <summary>The repository behind a spec: spec → request → repo.</summary>
    public static async Task<string?> RepoFullNameAsync(
        CharterDbContext db,
        Guid specId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        return await (from spec in db.Specs.AsNoTracking()
                      where spec.Id == specId
                      join request in db.Requests.AsNoTracking() on spec.RequestId equals request.Id
                      join repo in db.Repos.AsNoTracking() on request.RepoId equals repo.Id
                      select repo.FullName)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
