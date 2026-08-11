using Charter.Api.Contracts;
using Charter.Api.Projects;
using Charter.Auth.Authorization;
using Charter.Data;
using Charter.Domain;
using Charter.Notifications;
using Charter.Runners;
using Charter.VersionControl;
using Microsoft.EntityFrameworkCore;

namespace Charter.Api.Requests;

/// <summary>What one member may be shown of one request, together with the rows to show.</summary>
public sealed record RequestView(RequestAggregate Aggregate, SessionVisibility Visibility);

/// <summary>
/// Every read path behind <c>/api/projects</c>, <c>/api/requests</c> and <c>/api/approvals</c>.
/// </summary>
/// <remarks>
/// This type loads rows and asks <see cref="SessionVisibilityPolicy"/> and
/// <see cref="RepoAccessPolicy"/> what may be sent. It contains no authorisation rules of its own —
/// a filter here that "helpfully" narrowed a result set would be a second, undocumented policy, and
/// the first time the two disagreed the API would be the one that was wrong.
/// </remarks>
public sealed class RequestQueryService
{
    private readonly CharterDbContext database;
    private readonly ICharterAuthorizationService authorization;
    private readonly IVersionControlProviderRegistry providers;
    private readonly TimeProvider clock;

    public RequestQueryService(
        CharterDbContext database,
        ICharterAuthorizationService authorization,
        IVersionControlProviderRegistry providers,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(clock);

        this.database = database;
        this.authorization = authorization;
        this.providers = providers;
        this.clock = clock;
    }

    /// <summary>
    /// The projects this member may file against.
    /// </summary>
    /// <remarks>
    /// Section 7.3 is deny-by-default and section 9 makes readiness earned, so both are enforced by a
    /// project simply not being in the list. There is no disabled state to render, because a disabled
    /// row would tell a requester that a repository they cannot use exists.
    /// </remarks>
    public async Task<IReadOnlyList<ProjectResponse>> ListProjectsAsync(
        MemberSnapshot member,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        var snapshots = await LoadRepoSnapshotsAsync(member.OrgId, cancellationToken);
        var repos = await database.Repos
            .AsNoTracking()
            .Where(row => row.OrgId == member.OrgId)
            .OrderBy(row => row.CreatedAt)
            .ToListAsync(cancellationToken);

        var projects = new List<ProjectResponse>();

        foreach (var repo in repos)
        {
            if (!snapshots.TryGetValue(repo.Id, out var snapshot)
                || RepoAccessPolicy.CanFileRequest(member, snapshot).IsDenied)
            {
                continue;
            }

            var profile = RepoProjectProfile.For(repo);

            projects.Add(new ProjectResponse
            {
                Id = repo.Id.ToString(),
                Name = profile.Name,
                Description = profile.Description,
                PrimerMd = repo.PrimerMd,
                Templates = profile.Templates,
            });
        }

        return projects;
    }

    /// <summary>The request list, newest first, filtered to what this member may see (section 7.4).</summary>
    public async Task<IReadOnlyList<RequestSummaryResponse>> ListRequestsAsync(
        MemberSnapshot member,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        var snapshots = await LoadRepoSnapshotsAsync(member.OrgId, cancellationToken);

        var requests = await database.Requests
            .AsNoTracking()
            .Where(row => row.OrgId == member.OrgId)
            .OrderByDescending(row => row.UpdatedAt)
            .Take(200)
            .ToListAsync(cancellationToken);

        if (requests.Count == 0)
        {
            return [];
        }

        var repos = await LoadReposAsync(requests.Select(row => row.RepoId), cancellationToken);
        var specs = await LoadLatestSpecsAsync(requests.Select(row => row.Id), cancellationToken);
        var sessions = await LoadLatestSessionsAsync(specs.Values.Select(spec => spec.Id), cancellationToken);
        var approver = await ResolveApproverNameAsync(member.OrgId, cancellationToken);

        var summaries = new List<RequestSummaryResponse>(requests.Count);

        foreach (var request in requests)
        {
            if (!repos.TryGetValue(request.RepoId, out var repo)
                || !snapshots.TryGetValue(request.RepoId, out var snapshot))
            {
                continue;
            }

            specs.TryGetValue(request.Id, out var spec);
            var session = spec is null ? null : sessions.GetValueOrDefault(spec.Id);

            var visibility = RequestVisibility.Resolve(member, snapshot, request, session);
            if (visibility.IsEmpty)
            {
                // Section 7.4: nothing at all to show. Omission, not a greyed-out row.
                continue;
            }

            var milestones = spec is null || session is null
                ? []
                : await LoadMilestonesAsync(session.Id, cancellationToken);

            summaries.Add(RequestProjection.Summary(new RequestAggregate
            {
                Request = request,
                Repo = repo,
                Profile = RepoProjectProfile.For(repo),
                Spec = spec,
                Session = session,
                Milestones = milestones,
                AwaitingApprovalFrom = approver,
            }));
        }

        return summaries;
    }

    /// <summary>
    /// One request, with the visibility it was projected under.
    /// </summary>
    /// <returns><c>null</c> when the request does not exist or this member may see nothing of it.</returns>
    public async Task<RequestView?> LoadAsync(
        MemberSnapshot member,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        var request = await database.Requests
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == requestId, cancellationToken);

        if (request is null || request.OrgId != member.OrgId)
        {
            return null;
        }

        var repo = await database.Repos
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == request.RepoId, cancellationToken);

        if (repo is null)
        {
            return null;
        }

        var scopes = await database.RepoScopes
            .AsNoTracking()
            .Where(row => row.RepoId == repo.Id)
            .ToListAsync(cancellationToken);

        var snapshot = RepoSnapshot.From(repo, scopes);

        var spec = await database.Specs
            .AsNoTracking()
            .Where(row => row.RequestId == request.Id)
            .OrderByDescending(row => row.Version)
            .FirstOrDefaultAsync(cancellationToken);

        var session = spec is null
            ? null
            : await database.Sessions
                .AsNoTracking()
                .Where(row => row.SpecId == spec.Id)
                .OrderByDescending(row => row.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

        var visibility = RequestVisibility.Resolve(member, snapshot, request, session);
        if (visibility.IsEmpty)
        {
            // Indistinguishable from "no such request", by design (section 7.3).
            return null;
        }

        var milestones = session is null ? [] : await LoadMilestonesAsync(session.Id, cancellationToken);
        var artifacts = session is null
            ? []
            : await database.VerificationArtifacts
                .AsNoTracking()
                .Where(row => row.SessionId == session.Id)
                .OrderBy(row => row.CreatedAt)
                .ToListAsync(cancellationToken);

        var changeRequest = session is null || !RequestVisibility.CanSeeEngineerDetails(visibility)
            ? null
            : await database.ChangeRequests
                .AsNoTracking()
                .Where(row => row.SessionId == session.Id)
                .OrderByDescending(row => row.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

        // Section 7.4: pane 2 is a permission. The rows are not loaded at all for somebody who may not
        // see them, so a projection bug cannot leak what was never fetched.
        var events = session is null || !visibility.Transcript
            ? []
            : await LoadEventsAsync(session.Id, cancellationToken);

        // Pane 2 says "of 12,480", so the count is a count rather than the page's own length.
        var totalEvents = session is null || !visibility.Transcript
            ? 0
            : await database.Events
                .AsNoTracking()
                .LongCountAsync(row => row.SessionId == session.Id, cancellationToken);

        var earlier = totalEvents > events.Count;

        // Pane 3 lists the whole change while pane 2 shows one page of it, so the file list is its
        // own query. Deriving it from the page would hide a file whose only write fell outside.
        var changedPaths = session is null || !visibility.Code
            ? []
            : await LoadChangedPathsAsync(session.Id, cancellationToken);

        // Section 14. Loaded on the same terms as pane 2 — it names files, branches and deviations.
        var recap = session is null || !visibility.Transcript
            ? null
            : await database.Recaps
                .AsNoTracking()
                .Where(row => row.SessionId == session.Id)
                .OrderByDescending(row => row.GeneratedAt)
                .FirstOrDefaultAsync(cancellationToken);

        // Section 6: the question the agent stopped to ask. Read only in the one state it can exist
        // in, and from the transcript rather than a column — the event is already the durable record
        // of it (section 2.3), and pane 1 showing it is not the same permission as pane 2.
        var openQuestion = session is not null && request.Status == RequestStatus.NeedsInput
            ? await LoadOpenQuestionAsync(session.Id, cancellationToken)
            : null;

        // Section 7.5: who took it over. The session row records that it happened; the audit log is
        // what attributes it to a named human, which is what guardrail 5 of section 7.3 is for.
        var handedOffBy = session is { Status: SessionStatus.HandedOff }
            ? await ResolveHandOffActorAsync(member.OrgId, session.Id, cancellationToken)
            : null;

        var approvedBy = spec?.ApprovedBy is { } approverId
            ? await ResolveDisplayNameAsync(approverId, cancellationToken)
            : null;

        // Section 2.3: the refinement thread is rebuilt from rows, not from anything a hub frame left
        // behind. Turns arrive with the conversation, ordered by the record itself.
        var conversation = await database.Conversations
            .AsNoTracking()
            .Where(row => row.RequestId == request.Id)
            .OrderByDescending(row => row.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);

        // Section 11: one thread per request, forever, so the verdict shown is the latest of however
        // many rounds it took.
        var feedback = await database.RequestFeedback
            .AsNoTracking()
            .Where(row => row.RequestId == request.Id)
            .OrderByDescending(row => row.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return new RequestView(
            new RequestAggregate
            {
                Request = request,
                Repo = repo,
                Profile = RepoProjectProfile.For(repo),
                Spec = spec,
                Session = session,
                Milestones = milestones,
                Events = events,
                Artifacts = artifacts,
                ChangeRequest = changeRequest,

                // Change spec 001 part A.2: the provider supplies the word, so the engineer detail
                // reads "pull request 142" on GitHub and "merge request 142" on GitLab.
                ChangeRequestTerm = providers.TermsFor(repo).ChangeRequest,
                ChangeRequestTermShort = providers.TermsFor(repo).ChangeRequestShort,
                RefinementMessages = RefinementThread.Derive(request, spec, conversation),
                Feedback = RequestPresentation.Feedback(feedback),
                ApprovedByName = approvedBy,
                AwaitingApprovalFrom = await ResolveApproverNameAsync(member.OrgId, cancellationToken),
                HasEarlierEvents = earlier,
                TotalEvents = totalEvents,
                ChangedPaths = changedPaths,
                Recap = recap,
                HandedOffByName = handedOffBy,
                OpenQuestion = openQuestion,
            },
            visibility);
    }

    /// <summary>The newest unanswered question on a session, cleaned for a requester.</summary>
    /// <remarks>
    /// Newest wins because section 11 keeps one thread: an agent that asked twice is waiting on the
    /// second question, and showing the first would have the requester answer something already
    /// settled.
    /// </remarks>
    private async Task<string?> LoadOpenQuestionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var payload = await database.Events
            .AsNoTracking()
            .Where(row => row.SessionId == sessionId && row.Type == RunnerEventTypes.Question)
            .OrderByDescending(row => row.Seq)
            .Select(row => row.Payload)
            .FirstOrDefaultAsync(cancellationToken);

        if (payload is null || RunnerEventTypes.ReadQuestion(payload) is not { } question)
        {
            return null;
        }

        return RequesterSafeText.Scrub(question) is { Length: > 0 } scrubbed ? scrubbed : null;
    }

    /// <summary>The spend gate queue (section 7.5): specs waiting, that this member may approve.</summary>
    public async Task<IReadOnlyList<PendingApprovalResponse>> ListPendingApprovalsAsync(
        MemberSnapshot member,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        var waiting = await (
            from spec in database.Specs.AsNoTracking()
            join request in database.Requests.AsNoTracking() on spec.RequestId equals request.Id
            where request.OrgId == member.OrgId
                  && request.Status == RequestStatus.SpecReady
                  && spec.ApprovedAt == null
            orderby request.UpdatedAt
            select new { spec, request }).ToListAsync(cancellationToken);

        if (waiting.Count == 0)
        {
            return [];
        }

        var repos = await LoadReposAsync(waiting.Select(row => row.request.RepoId), cancellationToken);
        var names = await LoadDisplayNamesAsync(waiting.Select(row => row.request.RequesterId), cancellationToken);

        var approvals = new List<PendingApprovalResponse>();

        foreach (var row in waiting)
        {
            var decision = await authorization.CanApproveSpecAsync(member, row.spec.Id, cancellationToken);
            if (decision.IsDenied)
            {
                continue;
            }

            if (!repos.TryGetValue(row.request.RepoId, out var repo))
            {
                continue;
            }

            var profile = RepoProjectProfile.For(repo);

            approvals.Add(new PendingApprovalResponse
            {
                RequestId = row.request.Id.ToString(),
                SpecId = row.spec.Id.ToString(),
                Title = row.spec.Title,
                Outcome = row.spec.Outcome,
                RequesterName = names.GetValueOrDefault(row.request.RequesterId) ?? "Someone at your organisation",
                ProjectName = profile.Name,

                // The repository's own ceiling (section 7.5). A spend limit, not a forecast.
                EstimatedCostUsd = profile.MaxSessionUsd ?? 0m,
                SubmittedAt = row.request.UpdatedAt,
            });
        }

        return approvals;
    }

    /// <summary>The clock the projection stamps artifact expiry against.</summary>
    public DateTimeOffset Now() => clock.GetUtcNow();

    private async Task<IReadOnlyDictionary<Guid, RepoSnapshot>> LoadRepoSnapshotsAsync(
        Guid orgId,
        CancellationToken cancellationToken)
    {
        var repos = await database.Repos
            .AsNoTracking()
            .Where(row => row.OrgId == orgId)
            .ToListAsync(cancellationToken);

        var repoIds = repos.ConvertAll(repo => repo.Id);

        var scopes = await database.RepoScopes
            .AsNoTracking()
            .Where(row => repoIds.Contains(row.RepoId))
            .ToListAsync(cancellationToken);

        return repos.ToDictionary(
            repo => repo.Id,
            repo => RepoSnapshot.From(repo, scopes.Where(scope => scope.RepoId == repo.Id)));
    }

    private async Task<IReadOnlyDictionary<Guid, Repo>> LoadReposAsync(
        IEnumerable<Guid> ids,
        CancellationToken cancellationToken)
    {
        var wanted = ids.Distinct().ToList();

        return await database.Repos
            .AsNoTracking()
            .Where(row => wanted.Contains(row.Id))
            .ToDictionaryAsync(row => row.Id, cancellationToken);
    }

    private async Task<IReadOnlyDictionary<Guid, Spec>> LoadLatestSpecsAsync(
        IEnumerable<Guid> requestIds,
        CancellationToken cancellationToken)
    {
        var wanted = requestIds.Distinct().ToList();

        var specs = await database.Specs
            .AsNoTracking()
            .Where(row => wanted.Contains(row.RequestId))
            .ToListAsync(cancellationToken);

        return specs
            .GroupBy(row => row.RequestId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(row => row.Version).First());
    }

    private async Task<IReadOnlyDictionary<Guid, Session>> LoadLatestSessionsAsync(
        IEnumerable<Guid> specIds,
        CancellationToken cancellationToken)
    {
        var wanted = specIds.Distinct().ToList();

        if (wanted.Count == 0)
        {
            return new Dictionary<Guid, Session>();
        }

        var sessions = await database.Sessions
            .AsNoTracking()
            .Where(row => wanted.Contains(row.SpecId))
            .ToListAsync(cancellationToken);

        return sessions
            .GroupBy(row => row.SpecId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(row => row.CreatedAt).First());
    }

    private async Task<IReadOnlyList<Milestone>> LoadMilestonesAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
        => await database.Milestones
            .AsNoTracking()
            .Where(row => row.SessionId == sessionId)
            .OrderBy(row => row.CreatedAt)
            .ToListAsync(cancellationToken);

    private async Task<IReadOnlyList<Event>> LoadEventsAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        // Section 12: cursor-paginated by seq, newest page first, so pane 2 opens at the live end.
        var page = await database.Events
            .AsNoTracking()
            .Where(row => row.SessionId == sessionId)
            .OrderByDescending(row => row.Seq)
            .Take(RequestProjection.TranscriptPageSize)
            .ToListAsync(cancellationToken);

        page.Reverse();
        return page;
    }

    /// <summary>Every path this session's transcript recorded a write to, in first-write order.</summary>
    private async Task<IReadOnlyList<string>> LoadChangedPathsAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var payloads = await database.Events
            .AsNoTracking()
            .Where(row => row.SessionId == sessionId && row.Type == EventTypes.FileWrite)
            .OrderBy(row => row.Seq)
            .Select(row => row.Payload)
            .ToListAsync(cancellationToken);

        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var payload in payloads)
        {
            if (RequestPresentation.TranscriptPath(EventTypes.FileWrite, payload) is { } path && seen.Add(path))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    /// <summary>
    /// Who took a session over (section 7.5), by display name.
    /// </summary>
    /// <remarks>
    /// The audit row is the record. Reading the name from it rather than from a column on the session
    /// keeps the one place that answers "which named human did this" as the one place, and means a
    /// take-over performed by a build that predates any such column still reads correctly.
    /// </remarks>
    private async Task<string?> ResolveHandOffActorAsync(
        Guid orgId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var target = sessionId.ToString();

        var actor = await database.AuditLogs
            .AsNoTracking()
            .Where(row => row.OrgId == orgId
                          && row.Action == ApiAuditActions.SessionHandedOff
                          && row.TargetId == target)
            .OrderByDescending(row => row.CreatedAt)
            .Select(row => row.ActorUserId)
            .FirstOrDefaultAsync(cancellationToken);

        return actor is { } userId ? await ResolveDisplayNameAsync(userId, cancellationToken) : null;
    }

    /// <summary>
    /// Section 6: <em>Waiting on {approver} to approve</em>.
    /// </summary>
    /// <remarks>
    /// The roles column is a Postgres <c>text[]</c> and the containment test is not something EF will
    /// translate from an <see cref="IReadOnlyList{T}"/> property, so the filter runs in memory over
    /// the organisation's members. Organisations are small; this is not the query to optimise.
    /// </remarks>
    /// <summary>
    /// Section 6's <em>Waiting on {approver} to approve</em>, by display name and never an id.
    /// </summary>
    /// <remarks>
    /// Public because <see cref="RefinementStreamPublisher"/> has to put the same name on a live
    /// status frame that the detail body carries, and a second lookup written beside it would be a
    /// second answer to the same question.
    /// </remarks>
    public async Task<string?> ResolveApproverNameAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var members = await database.Members
            .AsNoTracking()
            .Where(row => row.OrgId == orgId)
            .OrderBy(row => row.CreatedAt)
            .ToListAsync(cancellationToken);

        var approver = members.Find(row => row.HasRole(MemberRole.Approver));

        return approver is null ? null : await ResolveDisplayNameAsync(approver.UserId, cancellationToken);
    }

    private Task<string?> ResolveDisplayNameAsync(Guid userId, CancellationToken cancellationToken)
        => database.Users
            .AsNoTracking()
            .Where(row => row.Id == userId)
            .Select(row => (string?)row.DisplayName)
            .SingleOrDefaultAsync(cancellationToken);

    private async Task<IReadOnlyDictionary<Guid, string>> LoadDisplayNamesAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken)
    {
        var wanted = userIds.Distinct().ToList();

        return await database.Users
            .AsNoTracking()
            .Where(row => wanted.Contains(row.Id))
            .ToDictionaryAsync(row => row.Id, row => row.DisplayName, cancellationToken);
    }
}
