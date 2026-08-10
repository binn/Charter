namespace Charter.Api.Contracts;

/// <summary>One contiguous run of changed lines, as the diff found them.</summary>
public sealed record DiffHunkResponse
{
    public required string Id { get; init; }

    /// <summary><c>@@ -12,7 +12,9 @@</c> — shown verbatim as the hunk's label.</summary>
    public required string Header { get; init; }

    /// <summary>1-based line in the modified file. Pane 3 reveals this line when the hunk is selected.</summary>
    public required int ModifiedStartLine { get; init; }

    public required int OriginalStartLine { get; init; }
}

/// <summary>
/// One file's before and after, for Monaco's diff editor (sections 3, 12).
/// </summary>
/// <remarks>
/// <para>
/// Fetched per file rather than shipped inside <see cref="RequestDetailResponse"/>: a session can
/// touch a hundred files and a requester's payload must carry none of them.
/// </para>
/// <para>
/// <see cref="Binary"/> and <see cref="Truncated"/> exist so the pane can say what it is not showing.
/// Section 27.4's rule — <em>do not pretend parity</em> — applies to a diff as much as to a
/// verification artifact: silently sending the first quarter of a generated file and letting Monaco
/// present it as the whole thing is the failure both flags exist to prevent.
/// </para>
/// </remarks>
public sealed record FileDiffResponse
{
    public required string Path { get; init; }

    /// <summary>Monaco language id, resolved from the path. <c>plaintext</c> when unrecognised.</summary>
    public required string Language { get; init; }

    /// <summary>Empty when the file was added.</summary>
    public required string OriginalText { get; init; }

    /// <summary>Empty when the file was deleted.</summary>
    public required string ModifiedText { get; init; }

    public required IReadOnlyList<DiffHunkResponse> Hunks { get; init; }

    /// <summary>No text to show. The pane says so rather than rendering an empty editor.</summary>
    public required bool Binary { get; init; }

    /// <summary>Very large file: the text is a prefix. The pane says so and links out.</summary>
    public required bool Truncated { get; init; }
}

/// <summary>
/// Section 14's highest-value section: where the agent departed from the spec, or made a call the
/// spec did not cover.
/// </summary>
/// <remarks>
/// <see cref="SpecSaid"/> is absent for the second case, and the difference matters — <em>"the spec
/// said X and it did Y"</em> and <em>"the spec was silent and it chose Y"</em> need different amounts
/// of scrutiny, so an absent key is the honest way to say the second.
/// </remarks>
public sealed record RecapDeviationResponse
{
    public required string Id { get; init; }

    public string? SpecSaid { get; init; }

    public required string AgentDid { get; init; }

    /// <summary>Where to look first. Links pane 3 straight to the file.</summary>
    public string? Path { get; init; }
}

/// <summary>One thing the session could not verify (section 14, part 4).</summary>
public sealed record RecapNoteResponse
{
    public required string Id { get; init; }

    public required string Text { get; init; }
}

/// <summary>
/// Section 14. The engineer recap: the same event stream as the walkthrough, opposite audience.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It must never say "looks good."</strong> That rule is enforced where the recap is written
/// — <c>RecapVerdictGuard</c> — and this type honours it structurally by carrying no verdict, no
/// score and no pass/fail field for a client to render as one.
/// </para>
/// <para>
/// Omitted entirely for a viewer without repository read (section 7.4): it names files, branches and
/// deviations, and is written for somebody who will read the diff.
/// </para>
/// </remarks>
public sealed record EngineerRecapResponse
{
    /// <summary>
    /// Section 7.5: when a session was auto-dispatched nobody vetted the spec, and the recap
    /// <em>leads</em> with that. <see cref="SpecMd"/> is then present, because a summary of a
    /// specification nobody approved is not reviewable.
    /// </summary>
    public required bool AutoDispatched { get; init; }

    /// <summary>One paragraph, what and why, tied back to the approved spec. Markdown.</summary>
    public required string SummaryMd { get; init; }

    /// <summary>The spec in full. Present when <see cref="AutoDispatched"/>.</summary>
    public string? SpecMd { get; init; }

    public required IReadOnlyList<RecapDeviationResponse> Deviations { get; init; }

    /// <summary>
    /// <strong>Risk-ranked, not alphabetical</strong> (section 14). Ordered by
    /// <c>RecapFileRiskRanker</c> when the recap was written, and passed through in that order — the
    /// client never re-sorts, so re-sorting here would discard the only ranking that exists.
    /// </summary>
    public required IReadOnlyList<ChangedFileResponse> Files { get; init; }

    /// <summary>Tests not written, edge cases noticed and skipped.</summary>
    public required IReadOnlyList<RecapNoteResponse> CouldNotVerify { get; init; }

    /// <summary>Paths in suggested review order, starting where the risk is.</summary>
    public required IReadOnlyList<string> ReviewOrder { get; init; }

    /// <summary>
    /// Where the recap was posted as a change request comment (section 14). Absent means there was
    /// nowhere to post it and this view is the only copy.
    /// </summary>
    public string? PostedToUrl { get; init; }

    /// <summary>"pull request", "merge request" — supplied by the provider, never assumed.</summary>
    public string? PostedToTerm { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }
}

/// <summary>Who took a session over, and when (section 7.5).</summary>
public sealed record HandedOffResponse
{
    public required DateTimeOffset At { get; init; }

    /// <summary>A display name. Never a user id (section 7.1).</summary>
    public required string ByName { get; init; }
}

/// <summary>
/// Section 7.5's four post-hoc actions, all first-class.
/// </summary>
/// <remarks>
/// <para>
/// The booleans drive <em>affordances only</em>. They are not the authorisation check: the command
/// service refuses the POST regardless of what the client drew, which is the same separation section
/// 7.2 makes for <see cref="ViewerCapabilitiesResponse"/>.
/// </para>
/// <para>
/// The whole object is omitted for a viewer who may perform none of them, so the panel is absent
/// rather than present-and-empty (section 7.4).
/// </para>
/// </remarks>
public sealed record SessionActionsResponse
{
    public required bool CanApprove { get; init; }

    public required bool CanSteer { get; init; }

    public required bool CanRevise { get; init; }

    public required bool CanTakeOver { get; init; }

    /// <summary>
    /// The branch take-over stops agent writes to. Named in the confirmation, because "stops writes
    /// to that branch" is only meaningful if the reader can see which branch.
    /// </summary>
    public required string Branch { get; init; }

    /// <summary>
    /// Set once somebody has taken over. The session is <c>handed_off</c> and Charter stops touching
    /// it; steer and revise are gone for good at that point, which is why they read <c>false</c>
    /// above rather than being merely disabled in the client.
    /// </summary>
    public HandedOffResponse? HandedOff { get; init; }
}

/// <summary><c>POST /api/requests/{id}/session/steer</c>.</summary>
public sealed record SteerSessionBody
{
    /// <summary>What to do next. Same branch, same thread (section 7.5).</summary>
    public string? Instruction { get; init; }
}

/// <summary><c>POST /api/requests/{id}/session/revise</c>.</summary>
public sealed record ReviseSessionBody
{
    /// <summary>The edited spec, in full: forking a spec means replacing it, not patching it.</summary>
    public string? RevisedSpecMd { get; init; }
}
