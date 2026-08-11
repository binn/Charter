namespace Charter.Api.Contracts;

/// <summary>The onboarding states of section 9, in the client's spelling.</summary>
public enum ApiRepoStatus
{
    Pending,
    Recon,
    Configuring,
    SmokeTest,
    Ready,
    Disabled,
}

/// <summary>The five steps of the section 9 wizard, plus the gate that ends it.</summary>
public enum ApiOnboardingStepId
{
    Connect,
    Recon,
    ConfirmScope,
    SmokeTest,
    Primer,
    MergeGate,
}

/// <summary>A connected repository, as an engineer sees it.</summary>
/// <remarks>
/// Every route that produces one of these is engineer or admin only. A requester never sees a
/// repository name (section 7.1) and reaches none of these endpoints; what they get is the project
/// list, which is <c>GET /api/projects</c> and carries no repository at all.
/// </remarks>
public sealed record RepoResponse
{
    public required string Id { get; init; }

    /// <summary><c>owner/name</c>, as the provider spells it.</summary>
    public required string FullName { get; init; }

    public required string BaseBranch { get; init; }

    public required ApiRepoStatus Status { get; init; }

    /// <summary>Section 9: false until the smoke test passes. Readiness is earned.</summary>
    public required bool RequesterVisible { get; init; }

    /// <summary>True once an engineer has published the primer (section 8).</summary>
    public required bool HasPrimer { get; init; }

    public required DateTimeOffset ConnectedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary><c>GET /api/repos</c>.</summary>
public sealed record ReposResponse
{
    public required IReadOnlyList<RepoResponse> Repos { get; init; }
}

/// <summary>One row of the onboarding wizard.</summary>
public sealed record OnboardingStepResponse
{
    public required ApiOnboardingStepId Id { get; init; }

    /// <summary>What the step is called, in the words section 9 uses.</summary>
    public required string Label { get; init; }

    public required bool Done { get; init; }

    /// <summary>True for the one step the engineer should do next.</summary>
    public required bool Current { get; init; }
}

/// <summary>The six integration points the smoke test proves, in the order it exercises them.</summary>
public enum ApiSmokeTestCheckpointId
{
    RequestFiled,
    AgentRan,
    ChecksPassed,
    PullRequest,
    PreviewDeployed,
    UrlBound,
}

/// <summary>How one of the six turned out.</summary>
/// <remarks>
/// <c>Skipped</c> is what a point gets when an earlier one failed and it therefore never ran. It is
/// not <c>Failed</c>: telling an engineer that the preview deploy broke when nothing ever asked it
/// to deploy sends them to the wrong subsystem.
/// </remarks>
public enum ApiSmokeTestCheckpointState
{
    Pending,
    Running,
    Passed,
    Failed,
    Skipped,
}

/// <summary>One of the six integration points, as the wizard renders it.</summary>
public sealed record SmokeTestCheckpointResponse
{
    public required ApiSmokeTestCheckpointId Id { get; init; }

    /// <summary>What this point is called, in words an engineer reads once.</summary>
    public required string Label { get; init; }

    public required ApiSmokeTestCheckpointState State { get; init; }

    /// <summary>One line: what this point actually did. Absent when there is nothing to add.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// What the last smoke test did (section 9, step 4).
/// </summary>
/// <remarks>
/// Read back out of the audit log rather than a status column, because the audit log is where the
/// run was recorded and a second copy would be a second thing to keep true.
/// </remarks>
public sealed record SmokeTestOutcomeResponse
{
    public required bool Passed { get; init; }

    public required DateTimeOffset At { get; init; }

    /// <summary>The pull request the smoke test opened, when it got that far.</summary>
    public int? PullRequestNumber { get; init; }

    /// <summary>Section 18: whether the preview URL bound back to the pull request.</summary>
    public required bool PreviewBound { get; init; }

    /// <summary>
    /// All six integration points, individually.
    /// </summary>
    /// <remarks>
    /// Section 9's argument for the smoke test is that nothing else validates these together, which
    /// only helps if the reader can see which one broke. Absent for a run recorded before the six
    /// were written down; the client reconstructs what it can prove and marks the rest unknown.
    /// </remarks>
    public IReadOnlyList<SmokeTestCheckpointResponse>? Checkpoints { get; init; }

    /// <summary>Section 9: an empty preview warns rather than blocks. Absent when there is nothing to say.</summary>
    public IReadOnlyList<string>? Warnings { get; init; }
}

/// <summary>One path recon proposed a decision about (section 9, step 3).</summary>
public sealed record ScopeEntryResponse
{
    public required string Path { get; init; }

    /// <summary><c>file</c> or <c>directory</c>.</summary>
    public required string Kind { get; init; }

    public required bool Allowed { get; init; }

    /// <summary>
    /// True for the deny-by-default floor. Rendered as a denial with the reason attached rather than
    /// as a toggle, because whatever the client sends is filtered through the floor again anyway.
    /// </summary>
    public bool? Locked { get; init; }

    /// <summary>Why recon proposed this. Absent when there is nothing useful to say.</summary>
    public string? Reason { get; init; }
}

/// <summary>A named command recon found.</summary>
public sealed record ScopeCommandResponse
{
    public required string Label { get; init; }

    public required string Command { get; init; }
}

/// <summary>
/// What the recon session found and proposed (section 9, step 2).
/// </summary>
/// <remarks>
/// Absent from <see cref="RepoOnboardingResponse"/> until recon has run, so the wizard offers the
/// recon step rather than rendering an empty file tree.
/// </remarks>
public sealed record ScopeProposalResponse
{
    /// <summary>Recon's detected stack, shown verbatim.</summary>
    public required IReadOnlyList<string> DetectedStack { get; init; }

    /// <summary>Build, test and seed commands, so the engineer can sanity-check them.</summary>
    public required IReadOnlyList<ScopeCommandResponse> Commands { get; init; }

    /// <summary>Section 9: existing agent guidance is imported and extended, never overwritten.</summary>
    public IReadOnlyList<string>? ImportedFrom { get; init; }

    public required IReadOnlyList<ScopeEntryResponse> Entries { get; init; }
}

/// <summary>What the merge gate is worth for this repository (change spec 001 part A.5).</summary>
public sealed record MergeGateResponse
{
    /// <summary><c>provider_enforced</c> or <c>advisory</c>.</summary>
    public required string Enforcement { get; init; }

    public required string Branch { get; init; }

    /// <summary>Whether a protection rule covers the base branch at all.</summary>
    public required bool ProtectionConfigured { get; init; }

    public required bool RequiresReview { get; init; }

    public required DateTimeOffset CheckedAt { get; init; }

    /// <summary>The plain warning, when the gate is advisory. Absent when it is enforced.</summary>
    public string? Warning { get; init; }
}

/// <summary><c>GET /api/repos/{id}</c>: where this repository is in section 9.</summary>
public sealed record RepoOnboardingResponse
{
    public required RepoResponse Repo { get; init; }

    public required IReadOnlyList<OnboardingStepResponse> Steps { get; init; }

    /// <summary>The scope-config pull request, once recon has proposed one. Absent before that.</summary>
    public int? ScopeConfigPullRequest { get; init; }

    /// <summary>The last smoke test. Absent until one has run.</summary>
    public SmokeTestOutcomeResponse? LastSmokeTest { get; init; }

    /// <summary>The last merge-gate check. Absent until one has run.</summary>
    public MergeGateResponse? MergeGate { get; init; }

    /// <summary>What recon proposed. Absent until recon has run.</summary>
    public ScopeProposalResponse? ProposedScope { get; init; }

    /// <summary>
    /// What the primer editor should open with (section 9, step 5).
    /// </summary>
    /// <remarks>
    /// The published primer once there is one — editing a published page starts from the page — and
    /// before that the draft Charter scaffolded from what recon found. Absent when recon has not run
    /// and nothing has been published, because there is then nothing to edit but an empty box.
    /// </remarks>
    public string? PrimerDraftMd { get; init; }
}

/// <summary><c>POST /api/repos</c> (section 9, step 1).</summary>
public sealed record ConnectRepoBody
{
    /// <summary><c>owner/name</c>.</summary>
    public string? FullName { get; init; }

    /// <summary>The GitHub App installation that grants access to it.</summary>
    public long? InstallationId { get; init; }

    /// <summary>Defaults to <c>main</c>.</summary>
    public string? BaseBranch { get; init; }
}

/// <summary><c>POST /api/repos/{id}/scope</c> (section 9, step 3).</summary>
/// <remarks>
/// Both lists are optional: sending neither accepts what recon proposed. Whatever arrives is still
/// filtered through the deny-by-default floor, so a client cannot widen scope past it.
/// </remarks>
public sealed record ConfirmScopeBody
{
    public IReadOnlyList<string>? Allow { get; init; }

    public IReadOnlyList<string>? Deny { get; init; }
}

/// <summary>One row of <c>repo_scopes</c>, addressed to a member or to a role (section 7.3).</summary>
/// <remarks>
/// The absence of a granting row is the refusal, so the list is exactly what exists — there is no
/// synthesised "denied" row for everybody who has no grant, because that would be a list of the
/// whole organisation and would read as a policy rather than as the default.
/// </remarks>
public sealed record RepoAccessGrantResponse
{
    /// <summary>The member this grant names. Absent on a role grant.</summary>
    public string? MemberId { get; init; }

    /// <summary>
    /// That member's name, so the screen can say who this is.
    /// </summary>
    /// <remarks>
    /// Sent with the grant rather than left to a second lookup: an access list rendered as a column
    /// of opaque ids is a list nobody audits, which defeats the point of showing it.
    /// </remarks>
    public string? MemberName { get; init; }

    /// <summary>That member's email. Absent on a role grant.</summary>
    public string? MemberEmail { get; init; }

    /// <summary>The role this grant names. Absent on a member grant.</summary>
    public ApiRole? Role { get; init; }

    public required bool CanRequest { get; init; }
}

/// <summary><c>GET /api/repos/{id}/access</c>: who may file against this repository.</summary>
public sealed record RepoAccessResponse
{
    public required IReadOnlyList<RepoAccessGrantResponse> Grants { get; init; }

    /// <summary>
    /// Section 9: false until the smoke test passes, whatever the grants say.
    /// </summary>
    /// <remarks>
    /// Carried beside the grants because the two conditions are independent and an admin who has
    /// just granted access to a repository mid-onboarding needs to know why nobody can see it yet.
    /// </remarks>
    public required bool RequesterVisible { get; init; }
}

/// <summary><c>POST /api/repos/{id}/access</c>: grant or withhold, one row at a time.</summary>
/// <remarks>
/// Exactly one of <see cref="MemberId"/> and <see cref="Role"/>, never both — the same exclusivity
/// the <c>repo_scopes</c> check constraint enforces.
/// </remarks>
public sealed record RepoAccessGrantBody
{
    public string? MemberId { get; init; }

    public ApiRole? Role { get; init; }

    /// <summary>False writes a withholding row, which beats every granting row at the same level.</summary>
    public bool CanRequest { get; init; }
}

/// <summary><c>POST /api/repos/{id}/primer</c> (section 9, step 5).</summary>
public sealed record PublishPrimerBody
{
    public string? Markdown { get; init; }
}

/// <summary>What one onboarding step did.</summary>
public sealed record OnboardingActionResponse
{
    public required ApiRepoStatus Status { get; init; }

    /// <summary>One line, safe to show an engineer.</summary>
    public required string Explanation { get; init; }

    /// <summary>Anything odd but survivable — a refused path, an empty preview.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>The scope-config pull request, when this step opened or updated one.</summary>
    public int? PullRequestNumber { get; init; }

    /// <summary>Its URL. Absent when no pull request was opened.</summary>
    public string? PullRequestUrl { get; init; }
}
