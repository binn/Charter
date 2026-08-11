using Charter.Domain;
using Charter.VersionControl;
using Microsoft.Extensions.Logging;

namespace Charter.GitHub;

/// <summary>
/// GitHub behind <see cref="IVersionControlProvider"/> — the reference implementation, and the only
/// one in Phase 1 (change spec 001 part A.4).
/// </summary>
/// <remarks>
/// <para>
/// A wrapper, not a second client. Every call goes through <see cref="IGitHubRepositoryClient"/> and
/// <see cref="IGitHubAppTokenProvider"/>, which already carry the retry-on-401, the single-repository
/// token scoping and the "never log a token" discipline of section 7.4. What this type adds is the
/// translation between GitHub's vocabulary and Charter's, and the capability declarations that let
/// everything above it stop knowing which provider it is talking to.
/// </para>
/// <para>
/// The capabilities are declared honestly. GitHub genuinely can enforce the merge gate, honour
/// CODEOWNERS, protect a branch, dispatch its own CI, create a repository and transfer one — so all
/// of those are true. What is <em>not</em> claimed here is that any given repository has protection
/// configured; that is <see cref="MergeGateInspector"/>'s question, and part A.5 turns on the
/// difference.
/// </para>
/// </remarks>
public sealed class GitHubVersionControlProvider : IVersionControlProvider
{
    /// <summary>The provider id repositories are recorded under.</summary>
    public const string ProviderId = "github";

    private readonly IGitHubRepositoryClient _client;
    private readonly IGitHubAppTokenProvider _tokens;
    private readonly ILogger<GitHubVersionControlProvider> _logger;

    public GitHubVersionControlProvider(
        IGitHubRepositoryClient client,
        IGitHubAppTokenProvider tokens,
        ILogger<GitHubVersionControlProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(logger);

        _client = client;
        _tokens = tokens;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Id => ProviderId;

    /// <inheritdoc />
    public string DisplayName => "GitHub";

    /// <inheritdoc />
    public VersionControlCapabilities Capabilities { get; } = new()
    {
        ChangeRequests = true,
        Webhooks = true,

        // A GitHub App installation token: scoped to one repository, minted per unit of work, and
        // expiring on its own. This is the property the rest of the security model leans on.
        AppStyleAuth = true,
        BranchProtection = true,
        CodeOwners = true,
        RepoCreation = true,
        RepoTransfer = true,
        CiDispatch = true,

        // GitHub refuses the merge itself when a protection rule says so, which is what section
        // 7.4's guarantee rests on. Whether a given repository has such a rule is a separate
        // question, and onboarding asks it.
        MergeGateEnforcement = MergeGateEnforcement.ProviderEnforced,
        ChangeRequestComments = true,
        ChangeRequestLabels = true,
        ChangedFileListing = true,
    };

    /// <inheritdoc />
    public VersionControlTerms Terms => VersionControlTerms.PullRequest;

    /// <inheritdoc />
    public async Task<VersionControlCredential> AuthenticateRepoAsync(
        RepoRef repo,
        VersionControlAccess access = VersionControlAccess.Read,
        CancellationToken cancellationToken = default)
    {
        var token = await _tokens.GetInstallationTokenAsync(Repository(repo), Scope(access), cancellationToken);

        return new VersionControlCredential
        {
            Repository = token.Repository,
            Token = token.Token,
            ExpiresAt = token.ExpiresAt,
            Access = access,

            // What GitHub's git transport expects in the user half of a basic-auth URL.
            Username = "x-access-token",
        };
    }

    /// <inheritdoc />
    public async Task<WorkspaceCheckout> PrepareWorkspaceAsync(
        RepoRef repo,
        string revision,
        string? workingBranch = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);

        var credential = await AuthenticateRepoAsync(repo, VersionControlAccess.Contribute, cancellationToken);

        return new WorkspaceCheckout
        {
            RemoteUrl = new Uri($"https://github.com/{repo.Path}.git"),
            Credential = credential,
            Revision = revision,
            WorkingBranch = workingBranch,
        };
    }

    /// <inheritdoc />
    public Task<string?> GetBranchHeadAsync(
        RepoRef repo,
        string branch,
        CancellationToken cancellationToken = default)
        => _client.GetBranchHeadShaAsync(Repository(repo), branch, cancellationToken);

    /// <inheritdoc />
    public Task CreateBranchAsync(
        RepoRef repo,
        string branch,
        string fromRevision,
        CancellationToken cancellationToken = default)
        => _client.CreateBranchAsync(Repository(repo), branch, fromRevision, cancellationToken);

    /// <inheritdoc />
    public async Task<PushResult> PushAsync(
        RepoRef repo,
        string branch,
        string revision,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var repository = Repository(repo);
        var head = await _client.GetBranchHeadShaAsync(repository, branch, cancellationToken);

        if (head is null)
        {
            await _client.CreateBranchAsync(repository, branch, revision, cancellationToken);
            return new PushResult(branch, revision, true);
        }

        if (string.Equals(head, revision, StringComparison.OrdinalIgnoreCase))
        {
            // Already there. Publishing a ref twice is the ordinary case after a restart, not an
            // error, and GitHub would accept the no-op write anyway — this just does not make it.
            return new PushResult(branch, revision, false);
        }

        var moved = await _client.UpdateBranchAsync(repository, branch, revision, force, cancellationToken);

        return new PushResult(branch, moved.Sha, false);
    }

    /// <inheritdoc />
    public async Task<ChangeRequestSnapshot> OpenChangeRequestAsync(
        OpenChangeRequestCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var repository = Repository(command.Repo);

        var opened = await _client.OpenPullRequestAsync(
            repository,
            command.Source,
            command.Target,
            command.Title,
            command.BodyMarkdown,
            cancellationToken);

        var labels = command.Labels is { Count: > 0 }
            ? await _client.AddLabelsAsync(repository, opened.Number, command.Labels, cancellationToken)
            : [];

        return new ChangeRequestSnapshot
        {
            Number = opened.Number,
            Url = opened.Url,
            State = ChangeRequestState.Open,
            HeadRevision = opened.HeadSha,
            SourceBranch = opened.HeadBranch,
            TargetBranch = command.Target,
            AuthorLogin = opened.AuthorLogin,
            Labels = labels,
        };
    }

    /// <inheritdoc />
    public async Task<bool> CommentOnChangeRequestAsync(
        ChangeRequestRef changeRequest,
        string bodyMarkdown,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changeRequest);

        await _client.CommentOnPullRequestAsync(
            Repository(changeRequest.Repo),
            changeRequest.Number,
            bodyMarkdown,
            cancellationToken);

        return true;
    }

    /// <inheritdoc />
    public async Task<ChangeRequestSnapshot?> GetChangeRequestStateAsync(
        ChangeRequestRef changeRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changeRequest);

        var detail = await _client.GetPullRequestAsync(
            Repository(changeRequest.Repo),
            changeRequest.Number,
            cancellationToken);

        return detail is null
            ? null
            : new ChangeRequestSnapshot
            {
                Number = detail.Number,
                Url = detail.Url,
                State = MapState(detail.State, detail.Merged, detail.Draft),
                HeadRevision = detail.HeadSha,
                SourceBranch = detail.HeadBranch,
                TargetBranch = detail.BaseBranch,
                AuthorLogin = detail.AuthorLogin,
                Labels = detail.Labels,
            };
    }

    /// <inheritdoc />
    public async Task<bool> LabelChangeRequestAsync(
        ChangeRequestRef changeRequest,
        IReadOnlyList<string> labels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changeRequest);
        ArgumentNullException.ThrowIfNull(labels);

        if (labels.Count == 0)
        {
            return true;
        }

        await _client.AddLabelsAsync(
            Repository(changeRequest.Repo),
            changeRequest.Number,
            labels,
            cancellationToken);

        return true;
    }

    /// <inheritdoc />
    public async Task<RevisionComparison> CompareAsync(
        RepoRef repo,
        string baseRevision,
        string headRevision,
        CancellationToken cancellationToken = default)
    {
        var comparison = await _client.CompareAsync(
            Repository(repo),
            baseRevision,
            headRevision,
            cancellationToken);

        return new RevisionComparison(comparison.BehindBy, comparison.AheadBy, comparison.Files);
    }

    /// <inheritdoc />
    public async Task<WebhookRegistration> RegisterWebhookAsync(
        RepoRef repo,
        WebhookSubscription subscription,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        var hook = await _client.RegisterWebhookAsync(
            Repository(repo),
            subscription.CallbackUrl,
            subscription.Secret.Reveal(),
            [.. subscription.Events.Select(GitHubEventName)],
            cancellationToken);

        return new WebhookRegistration(
            hook.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            subscription.CallbackUrl,
            hook.Created);
    }

    /// <inheritdoc />
    public async Task<BranchProtectionStatus> GetBranchProtectionAsync(
        RepoRef repo,
        string branch,
        CancellationToken cancellationToken = default)
    {
        var protection = await _client.GetBranchProtectionAsync(Repository(repo), branch, cancellationToken);

        if (!protection.Protected)
        {
            return new BranchProtectionStatus(false, Detail: protection.Detail);
        }

        // A rule with no review requirement protects the branch from force pushes and nothing else.
        // Reporting that as a merge gate would overstate exactly the property section 7.4 rests on.
        var requiresReview = protection.RequiredApprovals is > 0 || protection.RequiresCodeOwnerReview;

        return new BranchProtectionStatus(
            true,
            requiresReview,
            protection.RequiredApprovals,
            protection.RequiresCodeOwnerReview,
            protection.DismissesStaleReviews,
            protection.EnforcedForAdministrators,
            protection.Detail);
    }

    /// <inheritdoc />
    public async Task<RepoRef> CreateRepositoryAsync(
        NewRepositoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Capability-gated even though GitHub supports it: the guard is what a second provider will
        // rely on, and a guard that only exists on the providers that fail is a guard nobody tested.
        Require(Capabilities.RepoCreation, "creating a repository");

        var installationId = request.InstallationId
                             ?? throw new VersionControlCapabilityException(
                                 $"Creating '{request.Owner}/{request.Name}' needs the GitHub App "
                                 + "installation id of the owning organisation, and the request named none. "
                                 + "A repository that does not exist yet cannot be authenticated to "
                                 + "(section 26.10).");

        var created = await _client.CreateRepositoryAsync(
            installationId,
            request.Owner,
            request.Name,
            request.Private,
            request.Description,
            cancellationToken);

        _logger.LogInformation("Created {Repository} on GitHub", created.FullName);

        return new RepoRef
        {
            ProviderId = ProviderId,
            Path = created.FullName,
            InstallationId = installationId,
            BaseBranch = created.DefaultBranch,
        };
    }

    /// <inheritdoc />
    public async Task<RepoRef> TransferRepositoryAsync(
        RepoRef repo,
        string newOwner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        Require(Capabilities.RepoTransfer, "transferring a repository");

        var transferred = await _client.TransferRepositoryAsync(Repository(repo), newOwner, cancellationToken);

        _logger.LogInformation("Transferred {Repository} to {Owner}", repo.Path, newOwner);

        return repo with { Path = transferred.FullName };
    }

    /// <inheritdoc />
    public async Task ApplyBranchProtectionAsync(
        RepoRef repo,
        BranchProtectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(Capabilities.BranchProtection, "applying branch protection");

        await _client.ApplyBranchProtectionAsync(
            Repository(repo),
            request.Branch,
            request.RequiredApprovals,
            request.RequireCodeOwnerReview,
            request.DismissStaleReviews,
            request.EnforceForAdministrators,
            cancellationToken);
    }

    /// <summary>Maps GitHub's <c>state</c>, <c>merged</c> and <c>draft</c> onto Charter's four.</summary>
    internal static ChangeRequestState MapState(string? state, bool merged, bool draft)
    {
        if (merged)
        {
            return ChangeRequestState.Merged;
        }

        if (string.Equals(state, "closed", StringComparison.OrdinalIgnoreCase))
        {
            return ChangeRequestState.Closed;
        }

        return draft ? ChangeRequestState.Draft : ChangeRequestState.Open;
    }

    /// <summary>Charter's neutral event names, in GitHub's spelling.</summary>
    internal static string GitHubEventName(string name) => name switch
    {
        "change_request" => "pull_request",

        // Section 6's InReview arrives on its own event, not on the change request's. A subscription
        // that asks only for change requests never learns that a human picked the work up.
        "change_request_review" => "pull_request_review",
        "check_suite" => "check_suite",
        "push" => "push",
        "installation" => "installation",
        _ => name,
    };

    private void Require(bool capability, string operation)
    {
        if (!capability)
        {
            throw new VersionControlCapabilityException(ProviderId, operation);
        }
    }

    private static GitHubTokenScope Scope(VersionControlAccess access) => access switch
    {
        VersionControlAccess.Contribute => GitHubTokenScope.Contribute,
        VersionControlAccess.Administer => GitHubTokenScope.Administer,
        _ => GitHubTokenScope.ReadOnly,
    };

    private static GitHubRepository Repository(RepoRef repo)
    {
        ArgumentNullException.ThrowIfNull(repo);

        return GitHubRepository.Parse(
            repo.Path,
            repo.InstallationId
            ?? throw new VersionControlCapabilityException(
                $"{repo.Path} has no GitHub App installation recorded, so no token can be minted for it."));
    }
}
