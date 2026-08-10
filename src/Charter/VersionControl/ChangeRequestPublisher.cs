using System.Text;
using System.Text.Json;
using Charter.Data;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Charter.VersionControl;

/// <summary>The labels Charter applies to a change request, and why.</summary>
public static class ChangeRequestLabels
{
    /// <summary>
    /// Section 7.5: the session was auto-dispatched, so nobody vetted the specification before the
    /// build. The engineer must know that before reading a line of the diff.
    /// </summary>
    public const string UnreviewedSpec = "unreviewed-spec";

    /// <summary>Section 15: the change carries a migration.</summary>
    public const string SchemaChange = "schema-change";
}

/// <summary>Event types the version control layer reads or writes on a session's transcript.</summary>
public static class ChangeRequestEventTypes
{
    /// <summary>
    /// The runner published the session's work. Payload: <c>branch</c> and <c>revision</c>.
    /// </summary>
    /// <remarks>
    /// Optional. Where a runner does not report it, the publisher falls back to the conventional
    /// session branch, so a backend that only knows how to <c>git push</c> still works.
    /// </remarks>
    public const string BranchPushed = "branch_pushed";

    /// <summary>Charter opened a change request. Payload: <c>number</c>, <c>url</c>, <c>labels</c>.</summary>
    public const string ChangeRequestOpened = "change_request_opened";

    /// <summary>The agent finished and changed nothing. A real outcome, not an error.</summary>
    public const string NoChanges = "no_changes";

    /// <summary>Section 17: the change request went stale.</summary>
    public const string MarkedStale = "change_request_stale";
}

/// <summary>What happened when a completed session was published.</summary>
public enum ChangeRequestPublication
{
    /// <summary>A change request was opened and recorded.</summary>
    Opened,

    /// <summary>One already existed for this session. Not an error — this is the guarantee working.</summary>
    AlreadyOpen,

    /// <summary>
    /// The agent produced no changes. Section 6 has no state for "it did nothing wrong and there is
    /// nothing to review", so this is recorded as an outcome rather than dressed up as a failure.
    /// </summary>
    NoChanges,

    /// <summary>The provider has no change request surface (part A.6). The diff is the artifact.</summary>
    NotSupported,

    /// <summary>Nothing to do: no such session, or it is terminal.</summary>
    Skipped,

    /// <summary>The provider refused or was unreachable. The sweep tries again.</summary>
    Failed,
}

/// <summary>The result of one publication attempt.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Explanation">One line, safe to show an engineer.</param>
/// <param name="ChangeRequest">The row, when there is one.</param>
public sealed record ChangeRequestPublicationResult(
    ChangeRequestPublication Outcome,
    string Explanation,
    ChangeRequest? ChangeRequest = null)
{
    /// <summary>Labels that were asked for, whether or not the provider could apply them.</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];
}

/// <summary>
/// The Phase 1 loop step nothing else owns: <em>session → change request</em>.
/// </summary>
/// <remarks>
/// <para>
/// When a session's work completes, the branch is published and a change request is opened against
/// the repository's base branch, with the number, URL, head revision, branch and state recorded
/// (section 5). Section 7.5's <c>unreviewed-spec</c> and section 15's <c>schema-change</c> labels are
/// applied here, because this is the only place that knows both the session's provenance and the
/// provider's labelling capability.
/// </para>
/// <para>
/// Everything is idempotent against the database rather than against anything remembered. The
/// control plane can restart between pushing a branch and opening a change request (section 2.3), so
/// each step asks what is already true before doing anything: an existing row short-circuits, an
/// already-published branch is not re-pushed, and a re-run produces the same answer.
/// </para>
/// </remarks>
public sealed class ChangeRequestPublisher
{
    private readonly CharterDbContext _database;
    private readonly IVersionControlProviderRegistry _registry;
    private readonly TimeProvider _clock;
    private readonly ILogger<ChangeRequestPublisher> _logger;

    public ChangeRequestPublisher(
        CharterDbContext database,
        IVersionControlProviderRegistry registry,
        TimeProvider clock,
        ILogger<ChangeRequestPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _registry = registry;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>The branch a session's work lands on, where the provider has branches.</summary>
    /// <remarks>
    /// A convention rather than a stored column, so a runner that has never spoken to the control
    /// plane can compute it, and so a session that is re-run lands on the same branch (section 7.5's
    /// <em>revise and rebuild</em>). A runner that pushes somewhere else says so with a
    /// <see cref="ChangeRequestEventTypes.BranchPushed"/> event and is believed.
    /// </remarks>
    public static string BranchFor(Guid sessionId) => $"charter/session-{sessionId:N}";

    /// <summary>Opens the change request for one completed session.</summary>
    public async Task<ChangeRequestPublicationResult> PublishAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var session = await _database.Sessions
            .FirstOrDefaultAsync(candidate => candidate.Id == sessionId, cancellationToken);

        if (session is null || session.IsTerminal)
        {
            return new ChangeRequestPublicationResult(
                ChangeRequestPublication.Skipped,
                "The session is finished or no longer exists.");
        }

        var existing = await _database.ChangeRequests
            .FirstOrDefaultAsync(row => row.SessionId == sessionId, cancellationToken);

        if (existing is not null)
        {
            await MoveToChangeRequestOpenAsync(session, cancellationToken);

            return new ChangeRequestPublicationResult(
                ChangeRequestPublication.AlreadyOpen,
                $"Change request {existing.Number} is already recorded for this session.",
                existing);
        }

        var context = await LoadAsync(session, cancellationToken);

        if (context is null)
        {
            return new ChangeRequestPublicationResult(
                ChangeRequestPublication.Skipped,
                "The session is not attached to a connected repository.");
        }

        var (spec, repo) = context.Value;

        IVersionControlProvider provider;
        RepoRef reference;

        try
        {
            provider = _registry.For(repo);
            reference = _registry.ReferenceFor(repo);
        }
        catch (VersionControlCapabilityException exception)
        {
            // An instance with no provider wired, or a repository whose provider is not registered.
            // The sweep must carry on to the next session rather than taking the reconciliation pass
            // down with it.
            _logger.LogWarning(
                exception,
                "No version control provider can reach {Repository}, so session {SessionId} was left alone",
                repo.FullName,
                sessionId);

            return new ChangeRequestPublicationResult(
                ChangeRequestPublication.Skipped,
                exception.Message);
        }

        if (!provider.Capabilities.ChangeRequests)
        {
            // Part A.6: a real degradation, documented as one. The branch is still published, so the
            // engineer has something to fetch; the review surface is Charter's own.
            _logger.LogInformation(
                "{Provider} has no change request surface, so session {SessionId} is reviewed in Charter",
                provider.Id,
                sessionId);

            return new ChangeRequestPublicationResult(
                ChangeRequestPublication.NotSupported,
                $"{provider.DisplayName} has no {provider.Terms.ChangeRequest} surface, so the diff and "
                + "the recap stay in Charter.");
        }

        try
        {
            var pushed = await PublishBranchAsync(provider, reference, session, cancellationToken);

            if (pushed is null)
            {
                return await RecordNoChangesAsync(session, provider, cancellationToken);
            }

            var (branch, revision) = pushed.Value;

            var comparison = await provider.CompareAsync(
                reference,
                session.BaseCommitSha ?? reference.BaseBranch,
                revision,
                cancellationToken);

            if (comparison.ChangedFiles.Count == 0 && comparison.AheadBy == 0)
            {
                return await RecordNoChangesAsync(session, provider, cancellationToken);
            }

            var labels = await ResolveLabelsAsync(session, provider, cancellationToken);

            var snapshot = await provider.OpenChangeRequestAsync(
                new OpenChangeRequestCommand(
                    reference,
                    branch,
                    reference.BaseBranch,
                    Title(spec),
                    Body(spec, session, labels, provider),
                    labels),
                cancellationToken);

            // Labels are applied after opening rather than only in the create call: not every
            // provider accepts them at creation, and a change request that opened without its
            // `unreviewed-spec` label is worse than one that gained it a second later.
            if (labels.Count > 0 && snapshot.Labels.Count == 0)
            {
                await provider.LabelChangeRequestAsync(
                    new ChangeRequestRef(reference, snapshot.Number),
                    labels,
                    cancellationToken);
            }

            var row = ChangeRequest.Open(
                session.Id,
                snapshot.Number,
                snapshot.Url,
                string.IsNullOrWhiteSpace(snapshot.HeadRevision) ? revision : snapshot.HeadRevision,
                snapshot.State,
                _clock.GetUtcNow(),
                headBranch: snapshot.SourceBranch ?? branch);

            _database.ChangeRequests.Add(row);
            await _database.SaveChangesAsync(cancellationToken);

            await AppendAsync(
                session.Id,
                ChangeRequestEventTypes.ChangeRequestOpened,
                $$"""
                  {"number":{{snapshot.Number}},"url":{{JsonSerializer.Serialize(snapshot.Url)}},"labels":{{JsonSerializer.Serialize(labels)}},"term":{{JsonSerializer.Serialize(provider.Terms.ChangeRequest)}}}
                  """,
                cancellationToken);

            await MoveToChangeRequestOpenAsync(session, cancellationToken);

            _logger.LogInformation(
                "Opened {Term} {Number} for session {SessionId} on {Repository}{Labels}",
                provider.Terms.ChangeRequest,
                snapshot.Number,
                session.Id,
                reference.Path,
                labels.Count == 0 ? string.Empty : $" ({string.Join(", ", labels)})");

            return new ChangeRequestPublicationResult(
                ChangeRequestPublication.Opened,
                $"{provider.Terms.ChangeRequest} {snapshot.Number} is open.",
                row)
            { Labels = labels };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The sweep runs again. A session that could not be published stays where it is rather
            // than being failed for something the provider did.
            _logger.LogWarning(
                exception,
                "Could not open a {Term} for session {SessionId}",
                provider.Terms.ChangeRequest,
                sessionId);

            return new ChangeRequestPublicationResult(
                ChangeRequestPublication.Failed,
                $"Charter could not open a {provider.Terms.ChangeRequest}: {exception.Message}");
        }
    }

    /// <summary>
    /// Publishes every session whose runner reported completion and which has no change request yet.
    /// </summary>
    /// <remarks>
    /// Driven by the reconciliation pass rather than by the callback that reported the result, so a
    /// control plane that restarted between the two still opens the change request (section 2.3).
    /// </remarks>
    public async Task<IReadOnlyList<ChangeRequestPublicationResult>> PublishCompletedAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var candidates = await _database.Sessions
            .AsNoTracking()
            .Where(session => session.Status == SessionStatus.Running || session.Status == SessionStatus.NeedsInput)
            .Where(session => !_database.ChangeRequests.Any(row => row.SessionId == session.Id))
            .Where(session => _database.Events.Any(@event =>
                @event.SessionId == session.Id && @event.Type == EventTypes.SessionEnded))
            .OrderBy(session => session.CreatedAt)
            .Take(limit)
            .Select(session => session.Id)
            .ToListAsync(cancellationToken);

        var results = new List<ChangeRequestPublicationResult>(candidates.Count);

        foreach (var id in candidates)
        {
            if (!await CompletedCleanlyAsync(id, cancellationToken))
            {
                continue;
            }

            results.Add(await PublishAsync(id, cancellationToken));
        }

        return results;
    }

    /// <summary>
    /// Publishes the session branch, and answers null when there is nothing to publish.
    /// </summary>
    /// <remarks>
    /// The bytes are pushed by whoever holds the working copy — the runner, with its own short-TTL
    /// credential (section 7.4). What happens here is the ref half: a branch the runner already
    /// pushed is left alone, and a revision the runner reported without publishing is published.
    /// </remarks>
    private async Task<(string Branch, string Revision)?> PublishBranchAsync(
        IVersionControlProvider provider,
        RepoRef repo,
        Session session,
        CancellationToken cancellationToken)
    {
        var reported = await ReadBranchPushAsync(session.Id, cancellationToken);
        var branch = reported?.Branch ?? BranchFor(session.Id);
        var reportedRevision = reported?.Revision;
        var head = await provider.GetBranchHeadAsync(repo, branch, cancellationToken);

        if (head is null)
        {
            // No branch. Publish the revision the runner reported, or accept that the agent changed
            // nothing — there is no third possibility, and inventing one would mean opening an empty
            // change request.
            if (reportedRevision is not { Length: > 0 } revision)
            {
                return null;
            }

            await provider.CreateBranchAsync(repo, branch, revision, cancellationToken);
            return (branch, revision);
        }

        if (reportedRevision is { Length: > 0 } target
            && !string.Equals(target, head, StringComparison.OrdinalIgnoreCase))
        {
            var push = await provider.PushAsync(repo, branch, target, cancellationToken: cancellationToken);
            return (branch, push.Revision);
        }

        // A branch that never moved off the base commit is an agent that changed nothing, whatever
        // else it did while it was running.
        if (session.BaseCommitSha is { Length: > 0 } baseSha
            && string.Equals(baseSha, head, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return (branch, head);
    }

    private async Task<ChangeRequestPublicationResult> RecordNoChangesAsync(
        Session session,
        IVersionControlProvider provider,
        CancellationToken cancellationToken)
    {
        const string explanation =
            "The agent finished without changing anything, so there is nothing to review.";

        await AppendAsync(
            session.Id,
            ChangeRequestEventTypes.NoChanges,
            $$"""{"reason":"no_changes","message":{{JsonSerializer.Serialize(explanation)}}}""",
            cancellationToken);

        // Section 6 has no terminal state for "it did nothing wrong and there is nothing to review",
        // so the session ends as Failed and the `no_changes` event carries the real sentence. The UI
        // reads the event rather than the generic Failed copy: telling a requester their request
        // "turned out to be bigger than expected" when the agent simply found nothing to do would be
        // a lie the state machine forced.
        session.TransitionTo(SessionStatus.Failed, _clock.GetUtcNow());
        await _database.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Session {SessionId} produced no changes; no {Term} was opened",
            session.Id,
            provider.Terms.ChangeRequest);

        return new ChangeRequestPublicationResult(ChangeRequestPublication.NoChanges, explanation);
    }

    /// <summary>Section 7.5 and section 15, in the order an engineer reads them.</summary>
    private async Task<IReadOnlyList<string>> ResolveLabelsAsync(
        Session session,
        IVersionControlProvider provider,
        CancellationToken cancellationToken)
    {
        if (!provider.Capabilities.ChangeRequestLabels)
        {
            // Where the provider has no labels the facts move into the body instead. They are not
            // dropped: "no human approved this specification" is not a nice-to-have.
            return [];
        }

        var labels = new List<string>(2);

        if (session.AutoDispatched)
        {
            labels.Add(ChangeRequestLabels.UnreviewedSpec);
        }

        if (await HasSchemaChangeAsync(session.Id, cancellationToken))
        {
            labels.Add(ChangeRequestLabels.SchemaChange);
        }

        return labels;
    }

    private async Task<bool> HasSchemaChangeAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var payloads = await _database.Events
            .AsNoTracking()
            .Where(@event => @event.SessionId == sessionId && @event.Type == EventTypes.CheckResult)
            .Select(@event => @event.Payload)
            .ToListAsync(cancellationToken);

        return payloads.Any(payload =>
            Read(payload, "check") is "migration_classification"
            || Read(payload, "label") is ChangeRequestLabels.SchemaChange);
    }

    private async Task<(string Branch, string? Revision)?> ReadBranchPushAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var payload = await _database.Events
            .AsNoTracking()
            .Where(@event => @event.SessionId == sessionId
                             && @event.Type == ChangeRequestEventTypes.BranchPushed)
            .OrderByDescending(@event => @event.Seq)
            .Select(@event => @event.Payload)
            .FirstOrDefaultAsync(cancellationToken);

        if (payload is null)
        {
            return null;
        }

        var branch = Read(payload, "branch");

        return branch is { Length: > 0 }
            ? (branch, Read(payload, "revision") ?? Read(payload, "sha"))
            : null;
    }

    /// <summary>True when the runner's terminal report was a clean completion.</summary>
    private async Task<bool> CompletedCleanlyAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var payloads = await _database.Events
            .AsNoTracking()
            .Where(@event => @event.SessionId == sessionId && @event.Type == EventTypes.SessionEnded)
            .OrderByDescending(@event => @event.Seq)
            .Select(@event => @event.Payload)
            .Take(1)
            .ToListAsync(cancellationToken);

        var state = payloads.Count == 0 ? null : Read(payloads[0], "state");

        return state is not null && state is not ("failed" or "cancelled" or "stale");
    }

    private async Task<(Spec Spec, Repo Repo)?> LoadAsync(Session session, CancellationToken cancellationToken)
    {
        var row = await (from spec in _database.Specs
                         where spec.Id == session.SpecId
                         join request in _database.Requests on spec.RequestId equals request.Id
                         join repo in _database.Repos on request.RepoId equals repo.Id
                         select new { Spec = spec, Repo = repo })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : (row.Spec, row.Repo);
    }

    private async Task MoveToChangeRequestOpenAsync(Session session, CancellationToken cancellationToken)
    {
        if (session.Status is SessionStatus.Running or SessionStatus.NeedsInput or SessionStatus.Queued)
        {
            session.TransitionTo(SessionStatus.PrOpen, _clock.GetUtcNow());
            await _database.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task AppendAsync(
        Guid sessionId,
        string type,
        string payload,
        CancellationToken cancellationToken)
    {
        var seq = await _database.Events
            .Where(@event => @event.SessionId == sessionId)
            .MaxAsync(@event => (long?)@event.Seq, cancellationToken) ?? 0L;

        _database.Events.Add(Event.Append(sessionId, seq + 1, type, payload, _clock.GetUtcNow()));
        await _database.SaveChangesAsync(cancellationToken);
    }

    private static string Title(Spec spec)
    {
        var title = spec.Title.Trim();

        return title.Length <= 72 ? title : title[..71].TrimEnd() + "…";
    }

    /// <summary>
    /// The change request body. It states facts and never editorialises on quality (section 14).
    /// </summary>
    private static string Body(
        Spec spec,
        Session session,
        IReadOnlyList<string> labels,
        IVersionControlProvider provider)
    {
        var body = new StringBuilder();

        if (session.AutoDispatched)
        {
            // Section 7.5: the engineer must know this before anything else, and it appears in the
            // body as well as in the label because a provider that lost the label would otherwise
            // have lost the disclosure.
            body.AppendLine("> **No human approved this specification before the build.**");
            body.AppendLine("> This session was auto-dispatched under the repository's policy (spec §7.5).");
            body.AppendLine();
        }

        if (labels.Contains(ChangeRequestLabels.SchemaChange, StringComparer.Ordinal))
        {
            body.AppendLine("> This change contains a database migration (spec §15).");
            body.AppendLine();
        }

        body.AppendLine("## Specification");
        body.AppendLine();
        body.AppendLine(spec.BodyMd.Trim());
        body.AppendLine();
        body.AppendLine("---");
        body.AppendLine();
        body.AppendLine($"Opened by Charter for session `{session.Id:D}`.");
        body.AppendLine();
        body.AppendLine(
            $"Charter has no merge button. Whether this {provider.Terms.ChangeRequest} is fit to ship is "
            + "decided here, by the people who own this repository (spec §7.4).");

        return body.ToString();
    }

    private static string? Read(string payload, string property)
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
