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
