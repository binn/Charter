namespace Charter.Api.Contracts;

/// <summary>
/// An acceptance criterion, verbatim.
/// </summary>
/// <remarks>
/// Section 10b: authored in plain language and shared byte-for-byte between the requester view, the
/// engineer view, and the "What to check" list on the artifact card. The text comes from
/// <see cref="Charter.Refinement.RequesterSpecView.AcceptanceCriteria"/>, which returns the same list
/// instance the engineer view returns, so the two cannot disagree about what was agreed.
/// </remarks>
public sealed record AcceptanceCriterionResponse
{
    public required string Id { get; init; }

    public required string Text { get; init; }
}

/// <summary>Section 8 <c>glossary.yml</c>, for the inline Explain lens (section 10b).</summary>
public sealed record GlossaryTermResponse
{
    public required string Term { get; init; }

    public required string Definition { get; init; }
}

/// <summary>
/// The requester's rendering of a Spec.
/// </summary>
/// <remarks>
/// <para>
/// Section 10b: "Requester view renders <c>title</c>, <c>outcome</c>, <c>acceptance_criteria</c>."
/// There is deliberately no <c>TechnicalApproach</c>, <c>Scope</c> or <c>Risks</c> property on this
/// type — <em>not even a nullable one</em>. An optional property invites
/// <c>spec.technicalApproach &amp;&amp; …</c> on the client, and that is exactly the CSS-hiding
/// pattern section 7.4 forbids. A field that does not exist cannot be leaked by a future projection
/// bug.
/// </para>
/// <para>
/// <see cref="OpenQuestions"/> is the one exception, and it is a deliberate one: open questions are
/// written <em>to</em> the requester, they are what blocks their confirm button, and they carry no
/// repository path, SHA or cost. See <see cref="Charter.Refinement.RequesterSpecView.OpenQuestions"/>
/// for the reasoning, and section 10b, which was amended to match.
/// </para>
/// <para>
/// Every value here is read through <see cref="Charter.Refinement.RequesterSpecView"/>, the one
/// requester projection in the codebase. There is no second rendering to drift from.
/// </para>
/// </remarks>
public sealed record RequesterSpecResponse
{
    public required string Id { get; init; }

    public required int Version { get; init; }

    public required string Title { get; init; }

    /// <summary>Plain language: what the requester will see change.</summary>
    public required string Outcome { get; init; }

    public required IReadOnlyList<AcceptanceCriterionResponse> AcceptanceCriteria { get; init; }

    /// <summary>
    /// What refinement still needs an answer to (section 10). Absent — not empty — once nothing is
    /// open, which is the state every confirmable spec is in.
    /// </summary>
    public IReadOnlyList<string>? OpenQuestions { get; init; }

    public IReadOnlyList<GlossaryTermResponse>? Glossary { get; init; }

    public DateTimeOffset? ApprovedAt { get; init; }

    public string? ApprovedByName { get; init; }
}

/// <summary>A quick reply the client may render instead of a text box.</summary>
public sealed record RefinementChoiceResponse
{
    public required string Id { get; init; }

    public required string Label { get; init; }
}

/// <summary>One turn of the refinement conversation (section 10).</summary>
public sealed record RefinementMessageResponse
{
    /// <summary>Stable for the life of the message. The client dedupes on it (section 2.3).</summary>
    public required string Id { get; init; }

    public required ApiRefinementAuthor Author { get; init; }

    public required ApiRefinementMessageKind Kind { get; init; }

    public required string Body { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public IReadOnlyList<RefinementChoiceResponse>? Choices { get; init; }

    /// <summary>Set on <c>spec_proposed</c>: which Spec version this message introduced.</summary>
    public int? SpecVersion { get; init; }

    /// <summary>Set on <c>refusal</c>. The requester is told a human is now involved.</summary>
    public bool? RoutedToEngineer { get; init; }
}

/// <summary>Section 10. A chat surface, not a form.</summary>
public sealed record RefinementThreadResponse
{
    public required ApiRefinementMode Mode { get; init; }

    public required IReadOnlyList<RefinementMessageResponse> Messages { get; init; }

    /// <summary>Drives the typing indicator. Carries no time estimate (section 6).</summary>
    public required bool CharterIsThinking { get; init; }

    public required bool CanReply { get; init; }
}

/// <summary>
/// A translated milestone (section 11). Never raw transcript text.
/// </summary>
public sealed record MilestoneResponse
{
    public required string Id { get; init; }

    public required ApiMilestoneKind Kind { get; init; }

    /// <summary>Already plain language on the server. The client does not paraphrase it.</summary>
    public required string Label { get; init; }

    public string? Detail { get; init; }

    /// <summary>Section 13. One sentence, generated lazily and only if the user opted in.</summary>
    public string? AnnotationMd { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required ApiMilestoneState State { get; init; }

    /// <summary>
    /// Section 12: clicking a milestone scrolls pane 2 to the events that produced it. Present only
    /// when the transcript itself is present, i.e. only for viewers with repository read access
    /// (section 7.4).
    /// </summary>
    public long? EventSeq { get; init; }
}

/// <summary>Section 11. Two buttons, and whatever they typed after "Not quite".</summary>
public sealed record FeedbackResponse
{
    public required ApiFeedbackVerdict Verdict { get; init; }

    public string? Note { get; init; }

    public required DateTimeOffset SubmittedAt { get; init; }
}

/// <summary>
/// The one thread a request has, forever (section 11).
/// </summary>
/// <remarks>
/// Elapsed time is computed by the client from <see cref="StartedAt"/>. Section 6: <em>"Never show
/// an ETA. Elapsed time only."</em> — so there is no predicted-finish member here, and adding one
/// would fail <c>ApiNoEtaTests</c>.
/// </remarks>
public sealed record StatusThreadResponse
{
    public required bool Live { get; init; }

    public required IReadOnlyList<MilestoneResponse> Milestones { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>
    /// Section 11: plain language, dignified. Budget exhaustion, a stuck agent and failing checks all
    /// arrive here as the same sentence; the real detail goes to the engineer view. Never a stack
    /// trace.
    /// </summary>
    public string? FailureSummary { get; init; }

    public FeedbackResponse? Feedback { get; init; }
}

/// <summary>One row of pane 2 (section 12).</summary>
public sealed record TranscriptEventResponse
{
    public required long Seq { get; init; }

    public required string Type { get; init; }

    public required string Summary { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Set on file-write events; clicking one opens pane 3 at this path.</summary>
    public string? Path { get; init; }
}

/// <summary>
/// Pane 2 (section 12), cursor-paginated. Present only with repository read access (section 7.4).
/// </summary>
public sealed record TranscriptPaneResponse
{
    public required IReadOnlyList<TranscriptEventResponse> Events { get; init; }

    /// <summary>The lowest sequence still unfetched, or <c>null</c> at the beginning of the stream.</summary>
    public required string? NextCursor { get; init; }
}

/// <summary>One file pane 3 would show (section 12).</summary>
public sealed record ChangedFileResponse
{
    public required string Path { get; init; }

    public required int Additions { get; init; }

    public required int Deletions { get; init; }

    /// <summary>Section 14: auth, migrations, money math and external calls float to the top.</summary>
    public required ApiChangeRisk Risk { get; init; }
}

/// <summary>Pane 3 (section 12). Present only with repository read access (section 7.4).</summary>
public sealed record ChangesPaneResponse
{
    public required IReadOnlyList<ChangedFileResponse> Files { get; init; }
}

/// <summary>A row in the request list.</summary>
public sealed record RequestSummaryResponse
{
    public required string Id { get; init; }

    public required string ProjectId { get; init; }

    public required string ProjectName { get; init; }

    /// <summary>The Spec title once one exists; before that, a trimmed first line of the raw request.</summary>
    public required string Title { get; init; }

    public required ApiRequestStatus Status { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Latest translated milestone label (section 11). Never raw transcript text.</summary>
    public string? LastMilestoneLabel { get; init; }

    /// <summary>
    /// True only for the two states that notify (section 6): <c>needs_input</c> and
    /// <c>preview_ready</c>. Computed here so the badge, the email and the row cannot disagree.
    /// </summary>
    public required bool NeedsAttention { get; init; }

    /// <summary>Present when the spec is waiting on the spend gate and auto-dispatch did not apply.</summary>
    public string? AwaitingApprovalFrom { get; init; }
}

/// <summary>
/// The full request, projected for one viewer.
/// </summary>
/// <remarks>
/// <see cref="Transcript"/> and <see cref="Changes"/> are <c>null</c> — and therefore <em>absent
/// from the JSON</em> — unless <see cref="Charter.Auth.Authorization.SessionVisibility.Transcript"/>
/// and <c>.Code</c> say otherwise. Section 7.4: transcripts leak file paths, environment variable
/// names and error output, so absence is the permission check.
/// </remarks>
public sealed record RequestDetailResponse
{
    public required string Id { get; init; }

    public required string ProjectId { get; init; }

    public required string ProjectName { get; init; }

    public required string Title { get; init; }

    public required ApiRequestStatus Status { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public string? LastMilestoneLabel { get; init; }

    public required bool NeedsAttention { get; init; }

    public string? AwaitingApprovalFrom { get; init; }

    /// <summary>What the person actually typed, in their own words (section 5).</summary>
    public required string RawText { get; init; }

    public string? TemplateId { get; init; }

    /// <summary>Section 10b. Requester rendering only.</summary>
    public RequesterSpecResponse? Spec { get; init; }

    public required RefinementThreadResponse Refinement { get; init; }

    public required StatusThreadResponse Thread { get; init; }

    /// <summary>Section 27.1: several artifacts render as tabs in one card, never as many cards.</summary>
    public required IReadOnlyList<VerificationArtifactResponse> Artifacts { get; init; }

    /// <summary>Section 11: cancelling must kill the runner and settle cost, so the server owns this.</summary>
    public required bool Cancellable { get; init; }

    /// <summary>Pane 2. Omitted entirely unless the viewer has repository read access (section 7.4).</summary>
    public TranscriptPaneResponse? Transcript { get; init; }

    /// <summary>Pane 3. Omitted on the same terms as <see cref="Transcript"/>.</summary>
    public ChangesPaneResponse? Changes { get; init; }
}

/// <summary><c>POST /api/requests</c>.</summary>
public sealed record CreateRequestBody
{
    public string? ProjectId { get; init; }

    public string? RawText { get; init; }

    public string? TemplateId { get; init; }
}

/// <summary><c>POST /api/requests/{id}/refinement</c>.</summary>
public sealed record SendRefinementMessageBody
{
    public string? Body { get; init; }

    /// <summary>Set when the requester picked a quick reply rather than typing.</summary>
    public string? ChoiceId { get; init; }
}

/// <summary><c>POST /api/requests/{id}/spec/{version}/changes-requested</c>.</summary>
public sealed record RequestSpecChangesBody
{
    public string? Note { get; init; }
}

/// <summary><c>POST /api/requests/{id}/feedback</c>.</summary>
public sealed record SubmitFeedbackBody
{
    public ApiFeedbackVerdict? Verdict { get; init; }

    public string? Note { get; init; }
}

/// <summary>The spend gate queue (section 7.5).</summary>
public sealed record PendingApprovalResponse
{
    public required string RequestId { get; init; }

    public required string SpecId { get; init; }

    public required string Title { get; init; }

    public required string Outcome { get; init; }

    public required string RequesterName { get; init; }

    public required string ProjectName { get; init; }

    /// <summary>
    /// The per-session ceiling this request would run under (section 7.5,
    /// <c>max_session_usd</c>). A spend ceiling, not a prediction of anything, and specifically not a
    /// prediction of when the work finishes (section 6).
    /// </summary>
    public required decimal EstimatedCostUsd { get; init; }

    public required DateTimeOffset SubmittedAt { get; init; }
}
