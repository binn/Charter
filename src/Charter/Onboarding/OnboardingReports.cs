using System.Text;

namespace Charter.Onboarding;

/// <summary>
/// What the read-only recon run found (section 9, step 2).
/// </summary>
/// <remarks>
/// The output of an agent run over untrusted repository content (section 16), so it is a narrow
/// record of facts rather than free-form text handed onwards. The one field that is prose,
/// <see cref="Notes"/>, only ever reaches <c>.charter/conventions.md</c> in a pull request a human
/// reads before it lands.
/// </remarks>
public sealed record ReconReport
{
    /// <summary>Recon has not run.</summary>
    public static ReconReport Empty { get; } = new();

    /// <summary>The detected stack, such as <c>dotnet:10</c> or <c>node:22</c>.</summary>
    public IReadOnlyList<string> DetectedStack { get; init; } = [];

    /// <summary>The prebuilt runner image the stack implies (section 32.1).</summary>
    public string? RunnerImage { get; init; }

    /// <summary>Directories recon believes are safe to work in.</summary>
    public IReadOnlyList<string> ProposedAllow { get; init; } = [];

    /// <summary>Anything extra recon wants denied, on top of the defaults.</summary>
    public IReadOnlyList<string> ProposedDeny { get; init; } = [];

    /// <summary>The build and test commands it found.</summary>
    public IReadOnlyList<CharterCheck> Checks { get; init; } = [];

    /// <summary>The dev-seed command, when there is one. Optional (section 9).</summary>
    public string? Seed { get; init; }

    /// <summary>The repository's own agent guidance, imported by reference and never overwritten.</summary>
    public ExistingAgentGuidance ExistingGuidance { get; init; } = ExistingAgentGuidance.None;

    /// <summary>A requester-facing project name.</summary>
    public string? ProjectName { get; init; }

    /// <summary>The base branch recon observed.</summary>
    public string? BaseBranch { get; init; }

    /// <summary>The commit recon read.</summary>
    public string? CommitSha { get; init; }

    /// <summary>Anything an agent would otherwise rediscover, for <c>conventions.md</c>.</summary>
    public string? Notes { get; init; }

    /// <summary>Turns the report into the scope proposal, applying the deny-by-default floor.</summary>
    public ScopeProposal ToProposal(string baseBranch)
        => ScopeProposal.Propose(
            ProposedAllow,
            ProposedDeny,
            BaseBranch ?? baseBranch,
            RunnerImage,
            Seed,
            Checks,
            ProjectName);
}

/// <summary>
/// The six integration points the smoke test exercises at once (section 9, step 4).
/// </summary>
/// <remarks>
/// Section 9's argument for the smoke test is that nothing else validates all of these together, so
/// they are enumerated individually rather than collapsed into a boolean: a smoke test that failed
/// must say <em>where</em>, or the engineer is left bisecting six subsystems by hand.
/// </remarks>
public sealed record SmokeTestReport
{
    /// <summary>Charter filed the canned trivial request.</summary>
    public bool RequestFiled { get; init; }

    /// <summary>The agent ran and produced a diff.</summary>
    public bool AgentRan { get; init; }

    /// <summary>Every check in <c>.charter/config.yml</c> passed.</summary>
    public bool ChecksPassed { get; init; }

    /// <summary>A pull request opened.</summary>
    public bool PullRequestOpened { get; init; }

    /// <summary>A preview environment deployed.</summary>
    public bool PreviewDeployed { get; init; }

    /// <summary>The preview URL bound back to the pull request (section 18).</summary>
    public bool PreviewUrlBound { get; init; }

    /// <summary>
    /// Whether the preview appeared to have data in it.
    /// </summary>
    /// <remarks>
    /// Section 9: seed data is optional, and not all software needs it, so this <em>warns</em> and
    /// never blocks. Making it a gate would refuse onboarding to every repository without a dev seed
    /// path, which is a policy nobody asked for.
    /// </remarks>
    public bool PreviewHasData { get; init; } = true;

    /// <summary>Anything else that went wrong, for the engineer.</summary>
    public IReadOnlyList<string> Failures { get; init; } = [];

    /// <summary>The pull request the smoke test opened, if it got that far.</summary>
    public int? PullRequestNumber { get; init; }

    /// <summary>The preview URL, if one bound.</summary>
    public string? PreviewUrl { get; init; }

    /// <summary>A report where everything worked, for tests and for the happy path.</summary>
    public static SmokeTestReport Passing { get; } = new()
    {
        RequestFiled = true,
        AgentRan = true,
        ChecksPassed = true,
        PullRequestOpened = true,
        PreviewDeployed = true,
        PreviewUrlBound = true,
        PreviewHasData = true,
    };

    /// <summary>
    /// Whether the repository has earned <c>ready</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="PreviewHasData"/> is deliberately absent from this expression. It is the one signal
    /// section 9 says must warn rather than block.
    /// </remarks>
    public bool Passed =>
        RequestFiled
        && AgentRan
        && ChecksPassed
        && PullRequestOpened
        && PreviewDeployed
        && PreviewUrlBound
        && Failures.Count == 0;

    /// <summary>
    /// The warnings a passing smoke test still carries. Section 9's exact wording for the empty
    /// preview, because it is the message an engineer will see most often.
    /// </summary>
    public IReadOnlyList<string> Warnings => PreviewHasData
        ? []
        : [
            "Preview deployed but appears to have no data — requesters may not be able to evaluate changes.",
        ];

    /// <summary>Which of the six points did not happen, in the order they should have.</summary>
    public IReadOnlyList<string> MissingSteps
    {
        get
        {
            var missing = new List<string>();

            if (!RequestFiled)
            {
                missing.Add("Charter never filed the canned request");
            }

            if (!AgentRan)
            {
                missing.Add("the agent did not run");
            }

            if (!ChecksPassed)
            {
                missing.Add("the repository's checks did not pass");
            }

            if (!PullRequestOpened)
            {
                missing.Add("no pull request opened");
            }

            if (!PreviewDeployed)
            {
                missing.Add("no preview environment deployed");
            }

            if (!PreviewUrlBound)
            {
                missing.Add("the preview URL never bound back to the pull request");
            }

            missing.AddRange(Failures);

            return missing;
        }
    }

    /// <summary>A one-paragraph summary for the engineer running onboarding.</summary>
    public string Describe()
    {
        var builder = new StringBuilder();

        if (Passed)
        {
            builder.Append("Smoke test passed: the request, the agent run, the checks, the pull request, ");
            builder.Append("the preview deploy and the URL binding all worked.");
        }
        else
        {
            builder.Append("Smoke test failed: ").Append(string.Join("; ", MissingSteps)).Append('.');
        }

        foreach (var warning in Warnings)
        {
            builder.Append(' ').Append(warning);
        }

        return builder.ToString();
    }
}
