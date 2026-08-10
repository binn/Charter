using Charter.Api.Contracts;
using Charter.Auth.Authorization;
using Charter.Domain;
using Charter.Refinement;

namespace Charter.Api.Requests;

/// <summary>
/// Turns loaded rows into the payload one viewer is allowed to receive.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Section 7.4 lives here.</strong> Every engineer-facing field is assigned <c>null</c> when
/// <see cref="SessionVisibility"/> withholds it, and <see cref="CharterApiJson"/> then declines to
/// write the property at all. The client's test is the presence of a key, so "absent" and "null" are
/// not the same answer and only one of them is correct.
/// </para>
/// <para>
/// Nothing here decides <em>who</em> may see <em>what</em>. The visibility record arrives already
/// decided by <see cref="SessionVisibilityPolicy"/> via <see cref="RequestVisibility"/>; this file
/// only obeys it. That separation is what stops a projection bug from becoming a permission bug.
/// </para>
/// </remarks>
public static class RequestProjection
{
    /// <summary>How many transcript rows one page of pane 2 carries (section 12).</summary>
    public const int TranscriptPageSize = 200;

    /// <summary>The list row.</summary>
    public static RequestSummaryResponse Summary(RequestAggregate aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);

        var request = aggregate.Request;
        var milestones = Milestones(aggregate, includeEventSeq: false);

        return new RequestSummaryResponse
        {
            Id = request.Id.ToString(),
            ProjectId = request.RepoId.ToString(),
            ProjectName = aggregate.Profile.Name,
            Title = Title(aggregate),
            Status = request.Status.ToApi(),
            CreatedAt = request.CreatedAt,
            UpdatedAt = request.UpdatedAt,
            LastMilestoneLabel = milestones.Count == 0 ? null : milestones[^1].Label,
            NeedsAttention = RequestPresentation.NeedsAttention(request.Status),
            AwaitingApprovalFrom = request.Status == RequestStatus.SpecReady
                ? aggregate.AwaitingApprovalFrom
                : null,
        };
    }

    /// <summary>The full detail, projected for one viewer.</summary>
    public static RequestDetailResponse Detail(
        RequestAggregate aggregate,
        SessionVisibility visibility,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentNullException.ThrowIfNull(visibility);

        var request = aggregate.Request;
        var milestones = Milestones(aggregate, includeEventSeq: visibility.Transcript);
        var session = aggregate.Session;

        return new RequestDetailResponse
        {
            Id = request.Id.ToString(),
            ProjectId = request.RepoId.ToString(),
            ProjectName = aggregate.Profile.Name,
            Title = Title(aggregate),
            Status = request.Status.ToApi(),
            CreatedAt = request.CreatedAt,
            UpdatedAt = request.UpdatedAt,
            LastMilestoneLabel = milestones.Count == 0 ? null : milestones[^1].Label,
            NeedsAttention = RequestPresentation.NeedsAttention(request.Status),
            AwaitingApprovalFrom = request.Status == RequestStatus.SpecReady
                ? aggregate.AwaitingApprovalFrom
                : null,
            RawText = request.RawText,
            TemplateId = request.TemplateId,
            Spec = Spec(aggregate),
            Refinement = Refinement(aggregate),
            Thread = Thread(aggregate, milestones),
            Artifacts = Artifacts(aggregate, visibility, now),
            Cancellable = Cancellable(session),

            // Section 7.4. Panes 2 and 3 are a permission, not a preference. `null` here means the
            // property is never written, which is the only form of "hidden" this API has.
            Transcript = visibility.Transcript ? Transcript(aggregate) : null,
            Changes = visibility.Code ? Changes(aggregate) : null,
        };
    }

    /// <summary>
    /// The requester rendering of the spec.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Section 10b: <em>"Requester view renders <c>title</c>, <c>outcome</c>,
    /// <c>acceptance_criteria</c>, and any <c>open_questions</c> still addressed to them. Nothing
    /// else."</em> The values are read through
    /// <see cref="RequesterSpecView"/> — the one requester projection in the codebase, which reads
    /// every member through a reference to the structured <see cref="SpecDocument"/> and so has
    /// nothing of its own that could go stale. There is deliberately no second projection here.
    /// </para>
    /// <para>
    /// <see cref="RequesterSpecView.AcceptanceCriteria"/> is the same list instance the engineer view
    /// returns, so the "What to check" list on the artifact card, the requester's spec card and the
    /// engineer's spec are byte-identical by construction rather than by convention.
    /// </para>
    /// </remarks>
    public static RequesterSpecResponse? Spec(RequestAggregate aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);

        if (aggregate.Spec is not { } spec)
        {
            return null;
        }

        var document = SpecDocumentMapper.ToDocument(spec);
        var view = document.ForRequester();

        var criteria = new List<AcceptanceCriterionResponse>(view.AcceptanceCriteria.Count);
        for (var index = 0; index < view.AcceptanceCriteria.Count; index++)
        {
            criteria.Add(new AcceptanceCriterionResponse
            {
                Id = RequestPresentation.CriterionId(spec.Id, index),
                Text = view.AcceptanceCriteria[index],
            });
        }

        var glossary = aggregate.Profile.GlossaryFor(view.Title, view.Outcome, string.Join(' ', view.AcceptanceCriteria));

        return new RequesterSpecResponse
        {
            Id = spec.Id.ToString(),
            Version = spec.Version,
            Title = view.Title,
            Outcome = view.Outcome,
            AcceptanceCriteria = criteria,

            // Section 10: what refinement still needs an answer to, and the requester is who can give
            // it. Absent rather than empty once nothing is open — which is every confirmable spec.
            OpenQuestions = view.OpenQuestions.Count == 0 ? null : view.OpenQuestions,
            Glossary = glossary.Count == 0 ? null : glossary,
            ApprovedAt = spec.ApprovedAt,
            ApprovedByName = aggregate.ApprovedByName,
        };
    }

    private static string Title(RequestAggregate aggregate)
        => aggregate.Spec is { } spec && !string.IsNullOrWhiteSpace(spec.Title)
            ? spec.Title
            : RequestPresentation.TitleFrom(aggregate.Request.RawText);

    /// <summary>
    /// Whether there is a runner to kill (section 11).
    /// </summary>
    /// <remarks>
    /// Cancelling "must actually kill the runner and settle token cost", so the button is offered only
    /// while something is genuinely mid-flight. A session that has already produced a preview has
    /// nothing left to stop, even though the domain does not call that state terminal — the work is
    /// waiting on a human from there on.
    /// </remarks>
    private static bool Cancellable(Session? session)
        => IsRunning(session) && session!.CancelRequestedAt is null;

    /// <summary>
    /// The states in which an agent is actually working (section 6).
    /// </summary>
    /// <remarks>
    /// Public because <see cref="RequestCommandService.CancelAsync"/> has to refuse exactly what this
    /// declines to offer. Two spellings of "is there something to stop" is how a button that is not
    /// shown turns out to still work.
    /// </remarks>
    public static bool IsRunning(Session? session)
        => session is { Status: SessionStatus.Queued or SessionStatus.Running or SessionStatus.NeedsInput };

    private static RefinementThreadResponse Refinement(RequestAggregate aggregate)
    {
        var status = aggregate.Request.Status;

        var mode = status switch
        {
            RequestStatus.Draft => ApiRefinementMode.Chat,
            RequestStatus.Refining or RequestStatus.SpecReady or RequestStatus.Rejected => ApiRefinementMode.Plan,
            _ => ApiRefinementMode.Build,
        };

        // Section 10b: a conversation stops taking replies once the spec is approved or the work has
        // been dispatched. Promotion never rewinds, so this is a function of the state machine.
        var canReply = status is RequestStatus.Draft or RequestStatus.Refining or RequestStatus.Rejected;

        return new RefinementThreadResponse
        {
            Mode = mode,
            Messages = aggregate.RefinementMessages,
            CharterIsThinking = false,
            CanReply = canReply,
        };
    }

    private static StatusThreadResponse Thread(
        RequestAggregate aggregate,
        IReadOnlyList<MilestoneResponse> milestones)
    {
        var session = aggregate.Session;

        // Section 11: `live` is what keeps a stream open and an elapsed timer running. It follows the
        // runner, not the request, so a preview waiting to be tried is not "live".
        var live = IsRunning(session) && session!.StartedAt is not null;

        return new StatusThreadResponse
        {
            Live = live,
            Milestones = milestones,
            StartedAt = session?.StartedAt,
            EndedAt = session?.EndedAt,

            // Section 11: one sentence for every kind of failure. The detail is an engineer's problem.
            FailureSummary = aggregate.Request.Status == RequestStatus.Failed
                             || session?.Status == SessionStatus.Failed
                ? RequestPresentation.FailureSummary
                : null,
            Feedback = aggregate.Feedback,
        };
    }

    /// <summary>
    /// The status thread: the promoted milestones of section 11, plus the thread-level entries
    /// section 6 names.
    /// </summary>
    /// <remarks>
    /// Every synthesised entry has a deterministic id derived from the request it belongs to, so a
    /// client that refetches after a container restart (section 2.3) and then replays the stream sees
    /// the same id for the same entry and dedupes it rather than growing a duplicate row.
    /// </remarks>
    public static IReadOnlyList<MilestoneResponse> Milestones(RequestAggregate aggregate, bool includeEventSeq)
    {
        ArgumentNullException.ThrowIfNull(aggregate);

        var request = aggregate.Request;
        var entries = new List<MilestoneResponse>();

        if (aggregate.Spec is { ApprovedAt: { } approvedAt })
        {
            entries.Add(new MilestoneResponse
            {
                Id = $"{request.Id:N}:approved",
                Kind = ApiMilestoneKind.Status,
                Label = "You approved the plan",
                OccurredAt = approvedAt,
                State = ApiMilestoneState.Done,
            });
        }

        var sequences = includeEventSeq
            ? aggregate.Events.ToDictionary(row => row.Id, row => row.Seq)
            : [];

        var failed = request.Status == RequestStatus.Failed || aggregate.Session?.Status == SessionStatus.Failed;
        var live = IsRunning(aggregate.Session) && aggregate.Session!.StartedAt is not null;

        var promoted = aggregate.Milestones.OrderBy(row => row.CreatedAt).ThenBy(row => row.Id).ToList();

        for (var index = 0; index < promoted.Count; index++)
        {
            var milestone = promoted[index];
            var last = index == promoted.Count - 1;
            var (kind, label) = RequestPresentation.Milestone(milestone.Label);

            entries.Add(new MilestoneResponse
            {
                Id = milestone.Id.ToString(),
                Kind = kind,
                Label = label,
                AnnotationMd = milestone.AnnotationMd,
                OccurredAt = milestone.CreatedAt,
                State = last switch
                {
                    true when failed => ApiMilestoneState.Failed,
                    true when live => ApiMilestoneState.Active,
                    _ => ApiMilestoneState.Done,
                },

                // Section 12: the pane-1-to-pane-2 link only exists for people who have pane 2.
                EventSeq = includeEventSeq && sequences.TryGetValue(milestone.EventId, out var seq) ? seq : null,
            });
        }

        if (Outcome(aggregate) is { } outcome)
        {
            entries.Add(outcome);
        }

        return entries;
    }

    private static MilestoneResponse? Outcome(RequestAggregate aggregate)
    {
        var request = aggregate.Request;
        var at = aggregate.Session?.EndedAt ?? request.UpdatedAt;

        var (suffix, kind, state) = request.Status switch
        {
            RequestStatus.NeedsInput => ("question", ApiMilestoneKind.Question, ApiMilestoneState.Active),
            RequestStatus.PreviewReady => ("ready", ApiMilestoneKind.Outcome, ApiMilestoneState.Done),
            RequestStatus.InReview => ("review", ApiMilestoneKind.Status, ApiMilestoneState.Active),
            RequestStatus.Merged => ("live", ApiMilestoneKind.Outcome, ApiMilestoneState.Done),

            // Section 6: done, not failed. `Done` rather than `Failed` is the whole point — the run
            // was correct and there was simply nothing to change.
            RequestStatus.NoChangesNeeded => ("no_changes", ApiMilestoneKind.Outcome, ApiMilestoneState.Done),
            RequestStatus.Failed => ("failed", ApiMilestoneKind.Outcome, ApiMilestoneState.Failed),
            RequestStatus.Cancelled => ("cancelled", ApiMilestoneKind.Outcome, ApiMilestoneState.Done),
            RequestStatus.Stale => ("stale", ApiMilestoneKind.Outcome, ApiMilestoneState.Done),
            _ => (null, ApiMilestoneKind.Outcome, ApiMilestoneState.Done),
        };

        if (suffix is null)
        {
            return null;
        }

        return new MilestoneResponse
        {
            Id = $"{request.Id:N}:{suffix}",
            Kind = kind,
            Label = RequestPresentation.StatusLabel(request.Status, aggregate.AwaitingApprovalFrom),

            // The one outcome whose label is not the whole story. "Nothing needed changing" invites
            // exactly one question, and section 6 says answer it here rather than leave the requester
            // to guess whether something broke.
            Detail = request.Status == RequestStatus.NoChangesNeeded
                ? RequestPresentation.NoChangesSummary
                : null,
            OccurredAt = at,
            State = state,
        };
    }

    private static TranscriptPaneResponse Transcript(RequestAggregate aggregate)
    {
        var events = aggregate.Events
            .OrderBy(row => row.Seq)
            .Select(row => new TranscriptEventResponse
            {
                Seq = row.Seq,
                Type = row.Type,
                Summary = RequestPresentation.TranscriptSummary(row.Type, row.Payload),
                CreatedAt = row.CreatedAt,
                Path = RequestPresentation.TranscriptPath(row.Type, row.Payload),
            })
            .ToList();

        // Section 12: the cursor is the lowest sequence already fetched, never an offset, so paging
        // cost does not grow with the transcript.
        var nextCursor = aggregate.HasEarlierEvents && events.Count > 0
            ? events[0].Seq.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;

        return new TranscriptPaneResponse { Events = events, NextCursor = nextCursor };
    }

    private static ChangesPaneResponse Changes(RequestAggregate aggregate)
    {
        var files = aggregate.Events
            .Select(row => RequestPresentation.TranscriptPath(row.Type, row.Payload))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.Ordinal)
            .Select(path => new ChangedFileResponse
            {
                Path = path,

                // Line counts arrive with the diff, which has no column yet. Zero is honest; a made-up
                // number would not be.
                Additions = 0,
                Deletions = 0,
                Risk = RequestPresentation.ClassifyPath(path),
            })
            .OrderBy(file => file.Risk)
            .ThenBy(file => file.Path, StringComparer.Ordinal)
            .ToList();

        return new ChangesPaneResponse { Files = files };
    }

    private static IReadOnlyList<VerificationArtifactResponse> Artifacts(
        RequestAggregate aggregate,
        SessionVisibility visibility,
        DateTimeOffset now)
    {
        if (aggregate.Artifacts.Count == 0)
        {
            return [];
        }

        var ordered = aggregate.Artifacts.OrderBy(row => row.CreatedAt).ThenBy(row => row.Id).ToList();

        // Section 27.7: several artifacts render as tabs in one card, primary first. An engineer-only
        // artifact is not shown to somebody without repository read at all — the audience column is a
        // second, coarser gate on top of section 7.4, not a rendering hint.
        var visible = ordered
            .Where(row => row.Audience != VerificationArtifactAudience.EngineerOnly || visibility.Transcript)
            .ToList();

        var details = RequestVisibility.CanSeeEngineerDetails(visibility)
            ? EngineerDetails(aggregate, now)
            : null;

        var responses = new List<VerificationArtifactResponse>(visible.Count);

        for (var index = 0; index < visible.Count; index++)
        {
            var artifact = visible[index];
            var state = artifact.DisplayStateAt(now);

            responses.Add(new VerificationArtifactResponse
            {
                Id = artifact.Id.ToString(),
                Kind = artifact.Kind.ToApi(),
                State = state.ToApi(),
                Audience = artifact.Audience.ToApi(),
                Label = RequestPresentation.ArtifactLabel(artifact.Kind),
                Primary = index == 0,
                ExpiresAt = artifact.ExpiresAt,
                InstructionsMd = artifact.InstructionsMd,
                FailureSummary = state == VerificationArtifactState.Failed
                    ? "There is nothing to try this time — the change did not get far enough to build one."
                    : null,
                Payload = ArtifactPayloads.For(artifact),

                // Section 27.7: "`details` is omitted by the API, not hidden by CSS."
                Details = details,
            });
        }

        return responses;
    }

    private static EngineerDetailsResponse? EngineerDetails(RequestAggregate aggregate, DateTimeOffset now)
    {
        if (aggregate.ChangeRequest is not { } changeRequest || aggregate.Session is not { } session)
        {
            return null;
        }

        return new EngineerDetailsResponse
        {
            ChangeRequestNumber = changeRequest.Number,
            ChangeRequestUrl = changeRequest.Url,
            ChangeRequestTerm = aggregate.ChangeRequestTerm,
            ChangeRequestTermShort = aggregate.ChangeRequestTermShort,
            CommitSha = changeRequest.HeadSha,

            // Empty when no ref was recorded. Absent detail reads as "not recorded"; a guessed branch
            // name would read as a fact somebody could try to `git checkout`.
            Branch = changeRequest.HeadBranch ?? string.Empty,
            Runner = session.Runner.ToApi(),

            // Elapsed, never remaining (section 6). A session still going reads as time-so-far rather
            // than as zero, which is the same thing the requester's own timer shows.
            DurationMs = RequestPresentation.ElapsedMs(session.StartedAt, session.EndedAt ?? now),
            CostUsd = session.CostUsd,
        };
    }
}
