using Charter.Auth.Audit;
using Charter.Data;
using Charter.Domain;
using Charter.GitHub;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Charter.Onboarding;

/// <summary>The dotted verbs onboarding writes to the audit log (section 7.3, guardrail 5).</summary>
public static class OnboardingAuditActions
{
    /// <summary>A repository was connected. It is requestable by nobody at this point.</summary>
    public const string RepoConnected = "repo.connected";

    /// <summary>A read-only recon run was queued.</summary>
    public const string ReconStarted = "repo.recon.started";

    /// <summary>Recon finished and the scope config was proposed as a pull request.</summary>
    public const string ScopeProposed = "repo.scope.proposed";

    /// <summary>An engineer confirmed the scope config.</summary>
    public const string ScopeConfirmed = "repo.scope.confirmed";

    /// <summary>The smoke test passed and the repository became requestable.</summary>
    public const string RepoReady = "repo.ready";

    /// <summary>The smoke test failed.</summary>
    public const string SmokeTestFailed = "repo.smoke_test.failed";

    /// <summary>The primer was published.</summary>
    public const string PrimerPublished = "repo.primer.published";

    /// <summary>An admin disabled or re-enabled the repository.</summary>
    public const string RepoStatusChanged = "repo.status.changed";
}

/// <summary>The result of one onboarding step.</summary>
/// <param name="Succeeded">Whether the step did what was asked.</param>
/// <param name="Status">The repository's status afterwards.</param>
/// <param name="Explanation">One line, safe to show an engineer.</param>
public sealed record OnboardingOutcome(bool Succeeded, RepoStatus Status, string Explanation)
{
    /// <summary>Anything odd but survivable — an unparsed key, an empty preview.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>The scope-config pull request, once there is one.</summary>
    public string? PullRequestUrl { get; init; }

    /// <summary>Its number.</summary>
    public int? PullRequestNumber { get; init; }

    /// <summary>The job the execution plane will claim, when this step queued one.</summary>
    public Guid? JobId { get; init; }
}

/// <summary>
/// The onboarding flow of section 9: connect, recon, scope confirmation, smoke test, primer.
/// </summary>
/// <remarks>
/// <para>
/// A wizard that ends in <em>proof</em>. Every step here persists through
/// <see cref="CharterDbContext"/> and moves the repository through
/// <see cref="OnboardingStateMachine"/>; nothing holds progress in memory, so an operator can close
/// the tab, the container can restart, and onboarding picks up where it was (section 2.3).
/// </para>
/// <para>
/// The agent runs themselves are not here. Recon and the smoke test are dispatched through
/// <see cref="IOnboardingRunDispatcher"/> and report back through
/// <see cref="IOnboardingRunCallbacks"/>, which is the entire contract between section 9 and the
/// execution plane.
/// </para>
/// <para>
/// The scope config is written as a pull request and never as a direct commit. That is not politeness
/// — section 8's whole argument is that changing a guardrail should cost a review, and a tool that
/// commits its own guardrails straight to the base branch has quietly exempted itself from the rule
/// it is asking everyone else to follow.
/// </para>
/// </remarks>
public sealed class OnboardingService : IOnboardingRunCallbacks
{
    /// <summary>The branch the scope-config pull request is opened from.</summary>
    public const string ConfigBranch = "charter/onboarding";

    private readonly CharterDbContext _database;
    private readonly IGitHubRepositoryClient _github;
    private readonly ICharterFolderLoader _folders;
    private readonly IOnboardingRunDispatcher _dispatcher;
    private readonly IAuditWriter _audit;
    private readonly TimeProvider _clock;
    private readonly ILogger<OnboardingService> _logger;

    public OnboardingService(
        CharterDbContext database,
        IGitHubRepositoryClient github,
        ICharterFolderLoader folders,
        IOnboardingRunDispatcher dispatcher,
        IAuditWriter audit,
        TimeProvider clock,
        ILogger<OnboardingService> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(github);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _github = github;
        _folders = folders;
        _dispatcher = dispatcher;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Step 1: records a connected repository. It is requestable by nobody (sections 7.3, 9).
    /// </summary>
    public async Task<Repo> ConnectAsync(
        Guid orgId,
        long installationId,
        string fullName,
        string baseBranch = "main",
        Guid? actorUserId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);

        var name = fullName.Trim();

        var existing = await _database.Repos
            .FirstOrDefaultAsync(repo => repo.OrgId == orgId && repo.FullName == name, cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        var repo = Repo.Connect(orgId, installationId, name, baseBranch, _clock.GetUtcNow());

        _database.Repos.Add(repo);
        await _database.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            new AuditEntry
            {
                OrgId = orgId,
                ActorUserId = actorUserId,
                Action = OnboardingAuditActions.RepoConnected,
                TargetType = nameof(Repo),
                TargetId = repo.Id.ToString(),
                Metadata = new Dictionary<string, string?>
                {
                    ["full_name"] = repo.FullName,
                    ["base_branch"] = repo.BaseBranch,
                },
            },
            cancellationToken);

        _logger.LogInformation(
            "Connected {Repository}; it is requestable by nobody until its smoke test passes",
            repo.FullName);

        return repo;
    }

    /// <summary>Step 2: queues the read-only recon run.</summary>
    public async Task<OnboardingOutcome> StartReconAsync(
        Guid repoId,
        Guid? actorUserId = null,
        CancellationToken cancellationToken = default)
    {
        var repo = await FindAsync(repoId, cancellationToken);

        if (repo is null)
        {
            return new OnboardingOutcome(false, RepoStatus.Pending, "no such repository");
        }

        var signal = repo.Status == RepoStatus.Pending ? OnboardingSignal.StartRecon : OnboardingSignal.ReRecon;
        var transition = OnboardingStateMachine.Next(repo.Status, signal);

        if (!transition.Allowed)
        {
            return new OnboardingOutcome(false, repo.Status, transition.Explanation);
        }

        var jobId = await _dispatcher.DispatchReconAsync(
            PayloadFor(repo, actorUserId, readOnly: true),
            cancellationToken);

        await ApplyAsync(repo, transition, cancellationToken);

        await _audit.RecordAsync(
            new AuditEntry
            {
                OrgId = repo.OrgId,
                ActorUserId = actorUserId,
                Action = OnboardingAuditActions.ReconStarted,
                TargetType = nameof(Repo),
                TargetId = repo.Id.ToString(),
                Metadata = new Dictionary<string, string?> { ["job_id"] = jobId.ToString() },
            },
            cancellationToken);

        return new OnboardingOutcome(true, repo.Status, transition.Explanation) { JobId = jobId };
    }

    /// <inheritdoc />
    public async Task<OnboardingOutcome> CompleteReconAsync(
        Guid repoId,
        ReconReport report,
        Guid? actorUserId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        var repo = await FindAsync(repoId, cancellationToken);

        if (repo is null)
        {
            return new OnboardingOutcome(false, RepoStatus.Pending, "no such repository");
        }

        // Re-recon on a ready repository reports without moving it, so there is nothing to advance.
        var transition = OnboardingStateMachine.Next(repo.Status, OnboardingSignal.ReconCompleted);

        if (!transition.Allowed && repo.Status != RepoStatus.Ready)
        {
            return new OnboardingOutcome(false, repo.Status, transition.Explanation);
        }

        var proposal = report.ToProposal(repo.BaseBranch);

        var pullRequest = await ProposeScopeConfigAsync(repo, proposal, report, cancellationToken);

        if (transition.Allowed)
        {
            await ApplyAsync(repo, transition, cancellationToken);
        }

        await _audit.RecordAsync(
            new AuditEntry
            {
                OrgId = repo.OrgId,
                ActorUserId = actorUserId,
                Action = OnboardingAuditActions.ScopeProposed,
                TargetType = nameof(Repo),
                TargetId = repo.Id.ToString(),
                Metadata = new Dictionary<string, string?>
                {
                    ["pull_request"] = pullRequest?.Number.ToString(),
                    ["allowed_paths"] = proposal.Allow.Count.ToString(),
                    ["refused_paths"] = proposal.Refusals.Count.ToString(),
                },
            },
            cancellationToken);

        var warnings = proposal.Refusals
            .Select(refusal =>
                $"Recon suggested allowing '{refusal.Path}' but {refusal.Category} is denied by default; "
                + "move it in the pull request if it genuinely belongs in scope.")
            .ToList();

        if (proposal.Seed is null)
        {
            warnings.Add(
                "No dev-seed command was detected. That is fine — the smoke test warns rather than "
                + "blocks when a preview looks empty.");
        }

        return new OnboardingOutcome(true, repo.Status, "recon finished; the scope config is proposed as a pull request")
        {
            Warnings = warnings,
            PullRequestUrl = pullRequest?.Url,
            PullRequestNumber = pullRequest?.Number,
        };
    }

    /// <inheritdoc />
    public async Task<OnboardingOutcome> FailReconAsync(
        Guid repoId,
        string reason,
        Guid? actorUserId = null,
        CancellationToken cancellationToken = default)
    {
        var repo = await FindAsync(repoId, cancellationToken);

        if (repo is null)
        {
            return new OnboardingOutcome(false, RepoStatus.Pending, "no such repository");
        }

        var transition = OnboardingStateMachine.Next(repo.Status, OnboardingSignal.ReconFailed);

        if (!transition.Allowed)
        {
            return new OnboardingOutcome(false, repo.Status, transition.Explanation);
        }

        await ApplyAsync(repo, transition, cancellationToken);

        _logger.LogWarning("Recon failed for {Repository}: {Reason}", repo.FullName, reason);

        return new OnboardingOutcome(true, repo.Status, transition.Explanation);
    }

    /// <summary>
    /// Step 3: the engineer confirms the scope, and the smoke test is queued.
    /// </summary>
    /// <param name="repoId">The repository.</param>
    /// <param name="allow">
    /// The final allow list from the file-tree toggles, or <see langword="null"/> to accept what
    /// recon proposed. Whatever arrives is still filtered through the deny-by-default floor.
    /// </param>
    /// <param name="deny">Anything extra to deny, on top of the defaults.</param>
    /// <param name="actorUserId">The engineer confirming.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<OnboardingOutcome> ConfirmScopeAsync(
        Guid repoId,
        IEnumerable<string>? allow = null,
        IEnumerable<string>? deny = null,
        Guid? actorUserId = null,
        CancellationToken cancellationToken = default)
    {
        var repo = await FindAsync(repoId, cancellationToken);

        if (repo is null)
        {
            return new OnboardingOutcome(false, RepoStatus.Pending, "no such repository");
        }

        var transition = OnboardingStateMachine.Next(repo.Status, OnboardingSignal.ScopeProposed);

        if (!transition.Allowed)
        {
            return new OnboardingOutcome(false, repo.Status, transition.Explanation);
        }

        GitHubPullRequestResult? pullRequest = null;

        if (allow is not null || deny is not null)
        {
            // The toggles produce a new commit on the same branch, so the pull request an engineer is
            // already looking at is the one that changes. Still filtered: a UI cannot widen past the
            // deny-by-default floor any more than recon can.
            var proposal = ScopeProposal.Propose(allow, deny, repo.BaseBranch);

            pullRequest = await ProposeScopeConfigAsync(repo, proposal, ReconReport.Empty, cancellationToken);
        }

        var jobId = await _dispatcher.DispatchSmokeTestAsync(
            PayloadFor(repo, actorUserId, readOnly: false),
            cancellationToken);

        await ApplyAsync(repo, transition, cancellationToken);

        await _audit.RecordAsync(
            new AuditEntry
            {
                OrgId = repo.OrgId,
                ActorUserId = actorUserId,
                Action = OnboardingAuditActions.ScopeConfirmed,
                TargetType = nameof(Repo),
                TargetId = repo.Id.ToString(),
                Metadata = new Dictionary<string, string?> { ["job_id"] = jobId.ToString() },
            },
            cancellationToken);

        return new OnboardingOutcome(true, repo.Status, "scope confirmed; the smoke test is queued")
        {
            JobId = jobId,
            PullRequestUrl = pullRequest?.Url,
            PullRequestNumber = pullRequest?.Number,
        };
    }

    /// <inheritdoc />
    public async Task<OnboardingOutcome> CompleteSmokeTestAsync(
        Guid repoId,
        SmokeTestReport report,
        Guid? actorUserId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        var repo = await FindAsync(repoId, cancellationToken);

        if (repo is null)
        {
            return new OnboardingOutcome(false, RepoStatus.Pending, "no such repository");
        }

        var transition = OnboardingStateMachine.Next(
            repo.Status,
            report.Passed ? OnboardingSignal.SmokeTestPassed : OnboardingSignal.SmokeTestFailed);

        if (!transition.Allowed)
        {
            return new OnboardingOutcome(false, repo.Status, transition.Explanation);
        }

        // Reading the guardrails back off the base branch here rather than trusting what was
        // proposed: by now the scope-config pull request may have been edited or merged, and the
        // snapshot must reflect what is committed, not what Charter suggested.
        var snapshotWarnings = await RefreshSnapshotAsync(repo, cancellationToken);

        await ApplyAsync(repo, transition, cancellationToken);

        await _audit.RecordAsync(
            new AuditEntry
            {
                OrgId = repo.OrgId,
                ActorUserId = actorUserId,
                Action = report.Passed
                    ? OnboardingAuditActions.RepoReady
                    : OnboardingAuditActions.SmokeTestFailed,
                TargetType = nameof(Repo),
                TargetId = repo.Id.ToString(),
                Metadata = new Dictionary<string, string?>
                {
                    ["pull_request"] = report.PullRequestNumber?.ToString(),
                    ["preview_bound"] = report.PreviewUrlBound.ToString(),
                },
            },
            cancellationToken);

        // Section 9: an empty preview warns, it does not block. A repository whose smoke test
        // otherwise passed becomes ready with a warning attached, not stuck in smoke_test.
        var warnings = report.Warnings.Concat(snapshotWarnings).ToList();

        if (report.Passed)
        {
            _logger.LogInformation(
                "{Repository} passed its smoke test and is now requestable{Caveat}",
                repo.FullName,
                warnings.Count > 0 ? " (with warnings)" : string.Empty);
        }

        return new OnboardingOutcome(report.Passed, repo.Status, report.Describe())
        {
            Warnings = warnings,
            PullRequestNumber = report.PullRequestNumber,
        };
    }

    /// <summary>Step 5: publishes the primer an engineer edited.</summary>
    public async Task<OnboardingOutcome> PublishPrimerAsync(
        Guid repoId,
        string primerMarkdown,
        Guid? actorUserId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primerMarkdown);

        var repo = await FindAsync(repoId, cancellationToken);

        if (repo is null)
        {
            return new OnboardingOutcome(false, RepoStatus.Pending, "no such repository");
        }

        repo.PublishPrimer(primerMarkdown, _clock.GetUtcNow());
        await _database.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            new AuditEntry
            {
                OrgId = repo.OrgId,
                ActorUserId = actorUserId,
                Action = OnboardingAuditActions.PrimerPublished,
                TargetType = nameof(Repo),
                TargetId = repo.Id.ToString(),
            },
            cancellationToken);

        return new OnboardingOutcome(true, repo.Status, "primer published");
    }

    /// <summary>Disables or re-enables a repository (section 5, <c>disabled</c>).</summary>
    public async Task<OnboardingOutcome> SetEnabledAsync(
        Guid repoId,
        bool enabled,
        Guid? actorUserId = null,
        CancellationToken cancellationToken = default)
    {
        var repo = await FindAsync(repoId, cancellationToken);

        if (repo is null)
        {
            return new OnboardingOutcome(false, RepoStatus.Pending, "no such repository");
        }

        var transition = OnboardingStateMachine.Next(
            repo.Status,
            enabled ? OnboardingSignal.Enable : OnboardingSignal.Disable);

        if (!transition.Allowed)
        {
            return new OnboardingOutcome(false, repo.Status, transition.Explanation);
        }

        await ApplyAsync(repo, transition, cancellationToken);

        await _audit.RecordAsync(
            new AuditEntry
            {
                OrgId = repo.OrgId,
                ActorUserId = actorUserId,
                Action = OnboardingAuditActions.RepoStatusChanged,
                TargetType = nameof(Repo),
                TargetId = repo.Id.ToString(),
                Metadata = new Dictionary<string, string?> { ["status"] = repo.Status.ToString() },
            },
            cancellationToken);

        return new OnboardingOutcome(true, repo.Status, transition.Explanation);
    }

    /// <summary>
    /// Re-reads <c>.charter/</c> off the base branch and stores the snapshot.
    /// </summary>
    /// <remarks>
    /// Called when the scope-config pull request merges, on a push to the base branch, and after the
    /// smoke test. The committed file is the source of truth; this column is a copy kept close to
    /// hand for the authoriser and the project list.
    /// </remarks>
    public async Task<IReadOnlyList<string>> RefreshSnapshotAsync(
        Repo repo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);

        try
        {
            var folder = await _folders.LoadAsync(
                GitHubRepository.For(repo),
                repo.BaseBranch,
                cancellationToken);

            repo.RecordConfigSnapshot(folder.Exists ? folder.ToSnapshotJson() : null, _clock.GetUtcNow());
            await _database.SaveChangesAsync(cancellationToken);

            return folder.Warnings;
        }
        catch (GitHubApiException ex)
        {
            // A snapshot refresh that cannot reach GitHub must not fail the step that asked for it.
            // The authoriser fails closed on a missing snapshot, which is the right way round.
            _logger.LogWarning(ex, "Could not refresh the .charter/ snapshot for {Repository}", repo.FullName);

            return [$"Charter could not read .charter/ from {repo.BaseBranch}: {ex.Message}"];
        }
    }

    /// <inheritdoc cref="RefreshSnapshotAsync(Repo, CancellationToken)"/>
    public async Task<IReadOnlyList<string>> RefreshSnapshotAsync(
        Guid repoId,
        CancellationToken cancellationToken = default)
    {
        var repo = await FindAsync(repoId, cancellationToken);

        return repo is null ? ["no such repository"] : await RefreshSnapshotAsync(repo, cancellationToken);
    }

    private async Task<GitHubPullRequestResult?> ProposeScopeConfigAsync(
        Repo repo,
        ScopeProposal proposal,
        ReconReport report,
        CancellationToken cancellationToken)
    {
        var repository = GitHubRepository.For(repo);

        var files = new List<GitHubFileEdit>
        {
            new(".charter/config.yml", proposal.ToConfigYaml()),

            // Section 9: import and extend, never overwrite. CLAUDE.md and AGENTS.md are not in this
            // list and never will be — what Charter writes is its own file, which points at theirs.
            new(".charter/conventions.md", AgentGuidance.DraftConventions(report.ExistingGuidance, report.Notes)),
        };

        try
        {
            var baseSha = await _github.GetBranchHeadShaAsync(repository, repo.BaseBranch, cancellationToken);

            if (baseSha is null)
            {
                _logger.LogWarning(
                    "{Repository} has no branch named {Branch}, so the scope config cannot be proposed",
                    repo.FullName,
                    repo.BaseBranch);

                return null;
            }

            if (await _github.GetBranchHeadShaAsync(repository, ConfigBranch, cancellationToken) is null)
            {
                await _github.CreateBranchAsync(repository, ConfigBranch, baseSha, cancellationToken);
            }

            await _github.CommitFilesAsync(
                repository,
                ConfigBranch,
                "Add Charter guardrails (.charter/config.yml)",
                files,
                cancellationToken);

            return await _github.OpenPullRequestAsync(
                repository,
                ConfigBranch,
                repo.BaseBranch,
                "Charter: propose guardrails for this repository",
                proposal.ToPullRequestBody(repo.FullName),
                cancellationToken);
        }
        catch (GitHubApiException ex)
        {
            // Onboarding does not collapse because a pull request could not be opened — the engineer
            // is told, and the repository stays where it is so the step can be retried.
            _logger.LogWarning(ex, "Could not propose the scope config for {Repository}", repo.FullName);

            return null;
        }
    }

    private OnboardingJobPayload PayloadFor(Repo repo, Guid? actorUserId, bool readOnly) => new()
    {
        RepoId = repo.Id,
        OrgId = repo.OrgId,
        RepoFullName = repo.FullName,
        InstallationId = repo.GithubInstallationId,
        BaseBranch = repo.BaseBranch,
        RequestedByUserId = actorUserId,
        ReadOnly = readOnly,
    };

    private Task<Repo?> FindAsync(Guid repoId, CancellationToken cancellationToken)
        => _database.Repos.FirstOrDefaultAsync(repo => repo.Id == repoId, cancellationToken);

    private async Task ApplyAsync(Repo repo, OnboardingTransition transition, CancellationToken cancellationToken)
    {
        if (transition.Status != repo.Status)
        {
            repo.TransitionTo(transition.Status, _clock.GetUtcNow());
            await _database.SaveChangesAsync(cancellationToken);
        }
    }
}
