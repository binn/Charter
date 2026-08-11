using System.Text.Json;
using Charter.Api.Contracts;
using Charter.Data;
using Charter.Domain;
using Charter.Hubs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Charter.VersionControl;

/// <summary>
/// One provider-neutral change-request state report, translated from a webhook delivery.
/// </summary>
/// <param name="Repo">The repository, as the provider names it.</param>
/// <param name="Number">The change request number.</param>
/// <param name="State">What the provider now says it is.</param>
/// <param name="HeadRevision">The head commit, where the delivery carried one.</param>
/// <param name="SourceBranch">The branch, where the provider has branches and reported one.</param>
public sealed record ChangeRequestStateReport(
    string Repo,
    int Number,
    ChangeRequestState State,
    string? HeadRevision = null,
    string? SourceBranch = null);

/// <summary>
/// A human has picked one change request up (section 6's <c>InReview</c>).
/// </summary>
/// <param name="Repo">The repository, as the provider names it.</param>
/// <param name="Number">The change request number.</param>
/// <param name="Kind">Whether somebody was asked to review, or has reviewed.</param>
/// <param name="State">The provider's word for the review, where it had one. Never branched on.</param>
public sealed record ChangeRequestReviewReport(
    string Repo,
    int Number,
    ChangeRequestReviewKind Kind,
    string? State = null);

/// <summary>The two shapes "somebody is looking at this" arrives in.</summary>
public enum ChangeRequestReviewKind
{
    /// <summary>A reviewer was requested — on a repository with CODEOWNERS, this happens first.</summary>
    Requested,

    /// <summary>A review was submitted, whatever it said.</summary>
    Submitted,
}

/// <summary>A push landed on a branch. Section 17's staleness sweep runs from this.</summary>
/// <param name="Repo">The repository.</param>
/// <param name="Branch">The branch the push landed on.</param>
/// <param name="HeadRevision">Where the branch now points.</param>
/// <param name="PreviousRevision">
/// Where it pointed before. Without it the <em>which files landed</em> half of section 17's rule
/// cannot be evaluated, and the sweep marks nothing rather than guessing.
/// </param>
public sealed record BranchPushReport(
    string Repo,
    string Branch,
    string HeadRevision,
    string? PreviousRevision = null);

/// <summary>
/// Keeps change request state current, and applies section 17's staleness rule.
/// </summary>
/// <remarks>
/// <para>
/// State arrives by webhook rather than by polling, because a poll loop over every open change
/// request is both slower and, on a busy instance, an easy way to spend a rate limit on nothing.
/// Every write is keyed on the row the provider named, so a redelivered webhook is a no-op.
/// </para>
/// <para>
/// <strong>Staleness needs both halves.</strong> Section 17 marks a change request stale when it is
/// behind the base branch <em>and</em> overlaps on changed files. Being merely behind is normal and
/// harmless — most open change requests are behind most of the time — and marking those stale would
/// train everybody to ignore the flag, which is worse than not having one.
/// </para>
/// </remarks>
public sealed class ChangeRequestStateTracker
{
    private readonly CharterDbContext _database;
    private readonly IVersionControlProviderRegistry _registry;
    private readonly TimeProvider _clock;
    private readonly ILogger<ChangeRequestStateTracker> _logger;
    private readonly IRequestStreamPublisher? _stream;

    public ChangeRequestStateTracker(
        CharterDbContext database,
        IVersionControlProviderRegistry registry,
        TimeProvider clock,
        ILogger<ChangeRequestStateTracker> logger,
        IRequestStreamPublisher? stream = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _registry = registry;
        _clock = clock;
        _logger = logger;
        _stream = stream;
    }

    /// <summary>
    /// The request states a change request's own progress may still move it out of.
    /// </summary>
    /// <remarks>
    /// A thread that has already finished — merged, cancelled, failed, taken over — is never dragged
    /// backwards by a webhook that arrives late, and section 6 has no edge from any of those to
    /// <c>InReview</c>.
    /// </remarks>
    private static readonly RequestStatus[] InFlight =
    [
        RequestStatus.Queued,
        RequestStatus.Running,
        RequestStatus.NeedsInput,
        RequestStatus.PrOpen,
        RequestStatus.PreviewReady,
    ];

    /// <summary>Applies one state report. Returns false when no row matches it.</summary>
    public async Task<bool> ApplyAsync(
        ChangeRequestStateReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        var row = await FindAsync(report.Repo, report.Number, cancellationToken);

        if (row is null)
        {
            // A change request Charter did not open is not Charter's business. Repositories have
            // human contributors, and every one of their pull requests arrives here too.
            _logger.LogDebug(
                "Ignoring state for {Repository}#{Number}: Charter did not open it",
                report.Repo,
                report.Number);

            return false;
        }

        row.UpdateState(report.State, report.HeadRevision, _clock.GetUtcNow(), report.SourceBranch);
        var merged = await SyncSessionAsync(row, cancellationToken);
        await _database.SaveChangesAsync(cancellationToken);

        if (merged is not null)
        {
            await AnnounceAsync(merged.Id, RequestStatus.Merged, cancellationToken);
        }

        _logger.LogInformation(
            "Change request {Repository}#{Number} is now {State}",
            report.Repo,
            report.Number,
            report.State);

        return true;
    }

    /// <summary>
    /// Section 6's <c>InReview</c>: a human picked the change request up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the state the whole pipeline was missing a way into. Nothing Charter does can produce
    /// it — reviewing happens on the provider, by a person, outside the trust boundary (section 7.4) —
    /// so the only honest source is the provider saying so, which is this.
    /// </para>
    /// <para>
    /// It does not notify. Section 6 allows exactly two notifying states and this is not one of them:
    /// <em>an engineer is checking it</em> is reassurance for somebody already watching the thread,
    /// not news worth an email, and a Charter that mails on every state is a Charter that gets muted.
    /// </para>
    /// </remarks>
    /// <returns>False when no row matches, or when the thread has already moved past review.</returns>
    public async Task<bool> ReviewAsync(
        ChangeRequestReviewReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        var row = await FindAsync(report.Repo, report.Number, cancellationToken);

        if (row is null)
        {
            return false;
        }

        var session = await _database.Sessions
            .FirstOrDefaultAsync(candidate => candidate.Id == row.SessionId, cancellationToken);

        if (session is null || session.IsTerminal || session.Status == SessionStatus.InReview)
        {
            return false;
        }

        var now = _clock.GetUtcNow();
        session.TransitionTo(SessionStatus.InReview, now);

        var request = await LoadRequestAsync(row.SessionId, cancellationToken);
        var moved = request is not null && InFlight.Contains(request.Status);

        if (moved)
        {
            request!.TransitionTo(RequestStatus.InReview, now);
        }

        await _database.SaveChangesAsync(cancellationToken);

        if (moved)
        {
            await AnnounceAsync(request!.Id, RequestStatus.InReview, cancellationToken);
        }

        _logger.LogInformation(
            "Change request {Repository}#{Number} is being reviewed ({Kind}); session {SessionId} is InReview",
            report.Repo,
            report.Number,
            report.Kind,
            row.SessionId);

        return true;
    }

    /// <summary>
    /// Section 17: marks open change requests stale when the base branch moved ahead of them
    /// <em>and</em> their changed files overlap.
    /// </summary>
    /// <returns>The change requests that were newly marked stale.</returns>
    public async Task<IReadOnlyList<ChangeRequest>> MarkStaleAsync(
        BranchPushReport push,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(push);

        var repo = await FindRepoAsync(push.Repo, cancellationToken);

        if (repo is null || !string.Equals(repo.BaseBranch, push.Branch, StringComparison.Ordinal))
        {
            // Only the base branch moving can make anything stale.
            return [];
        }

        var provider = _registry.For(repo);
        var reference = _registry.ReferenceFor(repo);

        if (!provider.Capabilities.ChangedFileListing)
        {
            // Without the file list only one half of the test can be evaluated, and half of section
            // 17's rule is the half that produces false positives. Better to mark nothing.
            _logger.LogInformation(
                "{Provider} cannot list changed files, so staleness is not evaluated for {Repository}",
                provider.Id,
                repo.FullName);

            return [];
        }

        if (push.PreviousRevision is not { Length: > 0 } previous)
        {
            _logger.LogDebug(
                "A push to {Repository}/{Branch} carried no previous revision, so nothing was marked stale",
                repo.FullName,
                push.Branch);

            return [];
        }

        var open = await OpenChangeRequestsAsync(repo.Id, cancellationToken);

        if (open.Count == 0)
        {
            return [];
        }

        // What actually landed on the base branch.
        var landed = await provider.CompareAsync(reference, previous, push.HeadRevision, cancellationToken);
        var landedFiles = new HashSet<string>(landed.ChangedFiles, StringComparer.Ordinal);

        if (landedFiles.Count == 0)
        {
            return [];
        }

        var stale = new List<ChangeRequest>();

        foreach (var changeRequest in open.Where(candidate => !candidate.IsStale))
        {
            // One call answers both halves: how far the base has moved past this head, and which
            // files this change request touches relative to their merge base.
            var comparison = await provider.CompareAsync(
                reference,
                push.HeadRevision,
                changeRequest.HeadSha,
                cancellationToken);

            var behind = comparison.BehindBy > 0;

            if (!behind)
            {
                continue;
            }

            if (!comparison.ChangedFiles.Any(landedFiles.Contains))
            {
                // Behind but disjoint. Normal, and not worth a flag (section 17): most open change
                // requests are behind most of the time, and a flag that fires on all of them is one
                // everybody learns to ignore.
                continue;
            }

            changeRequest.MarkStale(_clock.GetUtcNow());
            stale.Add(changeRequest);
        }

        if (stale.Count > 0)
        {
            await _database.SaveChangesAsync(cancellationToken);

            // On the transcript, because a flag on a row nobody queries is not a signal. This is what
            // an engineer sees when they open the session, and what section 14's recap can rank
            // alongside everything else that happened to the change.
            foreach (var changeRequest in stale)
            {
                await AppendStaleAsync(changeRequest, repo.BaseBranch, push.HeadRevision, cancellationToken);
            }

            await _database.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Marked {Count} change request(s) on {Repository} stale after {Branch} moved",
                stale.Count,
                repo.FullName,
                push.Branch);
        }

        return stale;
    }

    /// <summary>The open change requests Charter opened against one repository.</summary>
    private async Task<IReadOnlyList<ChangeRequest>> OpenChangeRequestsAsync(
        Guid repoId,
        CancellationToken cancellationToken)
        => await (from changeRequest in _database.ChangeRequests
                  where changeRequest.State == ChangeRequestState.Open
                        || changeRequest.State == ChangeRequestState.Draft
                  join session in _database.Sessions on changeRequest.SessionId equals session.Id
                  join spec in _database.Specs on session.SpecId equals spec.Id
                  join request in _database.Requests on spec.RequestId equals request.Id
                  where request.RepoId == repoId
                  select changeRequest)
            .ToListAsync(cancellationToken);

    private async Task<ChangeRequest?> FindAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        var repo = await FindRepoAsync(repoPath, cancellationToken);

        if (repo is null)
        {
            return null;
        }

        return await (from changeRequest in _database.ChangeRequests
                      where changeRequest.Number == number
                      join session in _database.Sessions on changeRequest.SessionId equals session.Id
                      join spec in _database.Specs on session.SpecId equals spec.Id
                      join request in _database.Requests on spec.RequestId equals request.Id
                      where request.RepoId == repo.Id
                      select changeRequest)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private Task<Repo?> FindRepoAsync(string repoPath, CancellationToken cancellationToken)
    {
        var name = repoPath.Trim();

        return _database.Repos.FirstOrDefaultAsync(repo => repo.FullName == name, cancellationToken);
    }

    /// <summary>
    /// Moves the session <em>and the requester's thread</em> along with the change request (section 6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Merged is the only state Charter treats as terminal here. A closed change request is not
    /// necessarily a finished session — section 7.5's <em>revise and rebuild</em> opens another one —
    /// so closing is recorded and nothing else.
    /// </para>
    /// <para>
    /// The request moves too, and that is the point of the whole pipeline rather than a detail: the
    /// last line of section 6's label table is <em>this is live</em>, and a requester who never sees it
    /// has watched their request stop at "an engineer is checking it" forever. It still does not
    /// notify — section 6 keeps the notifying set at two — so the thread says it and nothing is mailed.
    /// </para>
    /// </remarks>
    /// <returns>The request that was moved to <c>Merged</c>, when one was.</returns>
    private async Task<Request?> SyncSessionAsync(
        ChangeRequest changeRequest,
        CancellationToken cancellationToken)
    {
        if (changeRequest.State != ChangeRequestState.Merged)
        {
            return null;
        }

        var now = _clock.GetUtcNow();

        var session = await _database.Sessions
            .FirstOrDefaultAsync(candidate => candidate.Id == changeRequest.SessionId, cancellationToken);

        if (session is not null && !session.IsTerminal)
        {
            session.TransitionTo(SessionStatus.Merged, now);
        }

        var request = await LoadRequestAsync(changeRequest.SessionId, cancellationToken);

        // A merge is terminal in the direction that matters, so this moves the thread from anywhere
        // that is not already an outcome — including InReview, which nothing else moves out of.
        if (request is null
            || request.Status is RequestStatus.Merged
                or RequestStatus.Cancelled
                or RequestStatus.Rejected
                or RequestStatus.NoChangesNeeded)
        {
            return null;
        }

        request.TransitionTo(RequestStatus.Merged, now);

        return request;
    }

    /// <summary>
    /// Records the staleness on the session's transcript.
    /// </summary>
    /// <remarks>
    /// The session's own status is left alone on purpose. <c>Stale</c> is terminal
    /// (<see cref="Session.IsTerminal"/>), and section 17's remedy for a stale change request is a
    /// rebase — not an ending. Retiring the session here would close a thread a human can still
    /// finish, and would do it on the strength of a merge somebody else made.
    /// </remarks>
    private async Task AppendStaleAsync(
        ChangeRequest changeRequest,
        string baseBranch,
        string headRevision,
        CancellationToken cancellationToken)
    {
        var seq = await _database.Events
            .Where(@event => @event.SessionId == changeRequest.SessionId)
            .MaxAsync(@event => (long?)@event.Seq, cancellationToken) ?? 0L;

        var payload = new
        {
            reason = "base_branch_moved",
            branch = baseBranch,
            revision = headRevision,
            number = changeRequest.Number,
            message = $"{baseBranch} has moved ahead of this change and touches files it also "
                + "changes, so it needs rebasing before it is merged (spec §17).",
        };

        _database.Events.Add(Event.Append(
            changeRequest.SessionId,
            seq + 1,
            ChangeRequestEventTypes.MarkedStale,
            JsonSerializer.Serialize(payload),
            _clock.GetUtcNow()));
    }

    /// <summary>The requester's thread behind a session, tracked so its status can be moved.</summary>
    private async Task<Request?> LoadRequestAsync(Guid sessionId, CancellationToken cancellationToken)
        => await (from session in _database.Sessions
                  where session.Id == sessionId
                  join spec in _database.Specs on session.SpecId equals spec.Id
                  join request in _database.Requests on spec.RequestId equals request.Id
                  select request)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Pushes the new state to anybody watching the thread.
    /// </summary>
    /// <remarks>
    /// Best effort, and deliberately after the save: section 2.3 forbids the stream from being the
    /// source of truth, so a hub that is unreachable costs a live update and never a state change. The
    /// same status is derivable from the next <c>GET</c>.
    /// </remarks>
    private async Task AnnounceAsync(Guid requestId, RequestStatus status, CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            return;
        }

        try
        {
            await _stream.PublishAsync(
                requestId,
                RequestStreamEvents.Status(status.ToApi()),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Could not stream the new status of request {RequestId} to the people watching it",
                requestId);
        }
    }

    /// <summary>Reads a string property out of an event payload, for callers that only have JSON.</summary>
    internal static string? Read(string payload, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);

            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty(property, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
