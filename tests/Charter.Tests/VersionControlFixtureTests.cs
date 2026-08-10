using Charter.Configuration;
using Charter.Domain;
using Charter.GitHub;
using Charter.VersionControl;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>
/// The seam itself: capabilities, terms, the registry, and the doubles the other version control
/// tests share.
/// </summary>
/// <remarks>
/// Nothing here makes a network call. <see cref="FakeVersionControlProvider"/> is an in-memory
/// provider, which is also the cheapest possible check that the interface of change spec 001 part
/// A.2 can be implemented by something that is not GitHub — the failure mode a one-implementation
/// seam has is that it cannot.
/// </remarks>
public class VersionControlFixtureTests
{
    [Fact]
    public void TheGitHubProviderDeclaresItsCapabilitiesHonestly()
    {
        var provider = VersionControlTestFixtures.GitHubProvider();

        Assert.Equal("github", provider.Id);
        Assert.True(provider.Capabilities.ChangeRequests);
        Assert.True(provider.Capabilities.Webhooks);
        Assert.True(provider.Capabilities.AppStyleAuth);
        Assert.True(provider.Capabilities.BranchProtection);
        Assert.True(provider.Capabilities.CodeOwners);
        Assert.True(provider.Capabilities.RepoCreation);
        Assert.True(provider.Capabilities.RepoTransfer);
        Assert.True(provider.Capabilities.CiDispatch);
        Assert.Equal(MergeGateEnforcement.ProviderEnforced, provider.Capabilities.MergeGateEnforcement);
    }

    [Fact]
    public void TheProviderSuppliesTheWordTheUiUses()
    {
        // Change spec 001 part A.2: pull request on GitHub, merge request on GitLab. The UI never
        // hard-codes either.
        Assert.Equal("pull request", VersionControlTestFixtures.GitHubProvider().Terms.ChangeRequest);
        Assert.Equal("merge request", VersionControlTerms.MergeRequest.ChangeRequest);
        Assert.Equal("MR", VersionControlTerms.MergeRequest.ChangeRequestShort);

        // The neutral fallback exists so a read path cannot fail for want of a provider.
        Assert.Equal("change request", VersionControlTerms.ChangeRequestDefault.ChangeRequest);
    }

    [Fact]
    public async Task AnOptionalOperationRefusesRatherThanPretending()
    {
        var provider = new FakeVersionControlProvider
        {
            DeclaredCapabilities = VersionControlCapabilities.None with { ChangeRequests = true },
        };

        var repo = VersionControlTestFixtures.RepoRef(provider.Id);

        await Assert.ThrowsAsync<VersionControlCapabilityException>(() =>
            provider.TransferRepositoryAsync(repo, "acme", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TheRegistryFallsBackToNeutralWordingWhenNoProviderIsRegistered()
    {
        var registry = new VersionControlProviderRegistry([]);
        var repo = VersionControlTestFixtures.Repo();

        // A projection must not fail because an instance wired no provider.
        Assert.Equal("change request", registry.TermsFor(repo).ChangeRequest);
        Assert.Null(registry.Find("github"));
        Assert.Throws<VersionControlCapabilityException>(() => registry.For(repo));
    }

    [Fact]
    public void TheRegistryResolvesTheProviderAndItsReference()
    {
        var provider = new FakeVersionControlProvider();
        var registry = new VersionControlProviderRegistry([provider]);
        var repo = VersionControlTestFixtures.Repo();

        Assert.Same(provider, registry.For(repo));

        var reference = registry.ReferenceFor(repo);

        Assert.Equal(repo.FullName, reference.Path);
        Assert.Equal(repo.BaseBranch, reference.BaseBranch);
        Assert.Equal(repo.GithubInstallationId, reference.InstallationId);
        Assert.Equal(repo.Id, reference.RepoId);
    }
}

/// <summary>Builders the version control tests share.</summary>
internal static class VersionControlTestFixtures
{
    public const string RepoFullName = "acme/widgets";

    public const long InstallationId = 4242;

    public static Repo Repo(string fullName = RepoFullName, string baseBranch = "main")
        => Charter.Domain.Repo.Connect(Guid.CreateVersion7(), InstallationId, fullName, baseBranch);

    public static RepoRef RepoRef(string providerId = "github", string baseBranch = "main")
        => new()
        {
            ProviderId = providerId,
            Path = RepoFullName,
            InstallationId = InstallationId,
            BaseBranch = baseBranch,
        };

    /// <summary>A stub that answers the token exchange, and nothing else until a test says so.</summary>
    public static GitHubStubHandler Handler()
        => new GitHubStubHandler()
            .On(
                HttpMethod.Post,
                "/access_tokens",
                GitHubTestFixtures.TokenResponse("ghs_test", new DateTimeOffset(2026, 8, 10, 13, 0, 0, TimeSpan.Zero)));

    /// <summary>A GitHub provider over a stubbed API. No network, no GitHub App.</summary>
    public static GitHubVersionControlProvider GitHubProvider(GitHubStubHandler? handler = null)
    {
        var stub = handler ?? Handler();

        var tokens = GitHubTestFixtures.Provider(
            stub,
            new ModelFakeTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero)));

        return new GitHubVersionControlProvider(
            GitHubTestFixtures.Client(stub, tokens),
            tokens,
            NullLogger<GitHubVersionControlProvider>.Instance);
    }
}

/// <summary>
/// A version control provider backed by dictionaries.
/// </summary>
/// <remarks>
/// Deliberately not GitHub-shaped: it has no installation, its "revisions" are whatever a test says,
/// and its capabilities are settable, so a test can assert what Charter does against a provider that
/// cannot label, cannot comment, or cannot enforce a merge gate.
/// </remarks>
internal sealed class FakeVersionControlProvider : IVersionControlProvider
{
    public string Id { get; init; } = "github";

    public string DisplayName { get; init; } = "Fake";

    public VersionControlCapabilities DeclaredCapabilities { get; set; } = new()
    {
        ChangeRequests = true,
        Webhooks = true,
        AppStyleAuth = true,
        BranchProtection = true,
        CodeOwners = true,
        RepoCreation = false,
        RepoTransfer = false,
        CiDispatch = false,
        MergeGateEnforcement = MergeGateEnforcement.ProviderEnforced,
        ChangeRequestComments = true,
        ChangeRequestLabels = true,
        ChangedFileListing = true,
    };

    public VersionControlCapabilities Capabilities => DeclaredCapabilities;

    public VersionControlTerms Terms { get; init; } = VersionControlTerms.PullRequest;

    /// <summary>Branch to head revision.</summary>
    public Dictionary<string, string> BranchHeads { get; } = new(StringComparer.Ordinal);

    /// <summary>(base, head) to the comparison the provider should answer.</summary>
    public Dictionary<(string Base, string Head), RevisionComparison> Comparisons { get; } = [];

    public Dictionary<string, BranchProtectionStatus> Protection { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, reading protection throws it — a 500, a timeout, a revoked installation.</summary>
    public Exception? ProtectionFailure { get; set; }

    public List<OpenChangeRequestCommand> Opened { get; } = [];

    public List<(int Number, IReadOnlyList<string> Labels)> Labelled { get; } = [];

    public List<(int Number, string Body)> Comments { get; } = [];

    public List<PushResult> Pushes { get; } = [];

    public List<string> BranchesCreated { get; } = [];

    /// <summary>The number the next opened change request gets.</summary>
    public int NextNumber { get; set; } = 142;

    /// <summary>Who the provider says opened it. Section 18 records this at open.</summary>
    public string? AuthorLogin { get; set; }

    public Task<VersionControlCredential> AuthenticateRepoAsync(
        RepoRef repo,
        VersionControlAccess access = VersionControlAccess.Read,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new VersionControlCredential
        {
            Repository = repo.Path,
            Token = new Secret("fake-token"),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            Access = access,
        });

    public async Task<WorkspaceCheckout> PrepareWorkspaceAsync(
        RepoRef repo,
        string revision,
        string? workingBranch = null,
        CancellationToken cancellationToken = default)
        => new()
        {
            RemoteUrl = new Uri($"https://example.test/{repo.Path}.git"),
            Credential = await AuthenticateRepoAsync(repo, VersionControlAccess.Contribute, cancellationToken),
            Revision = revision,
            WorkingBranch = workingBranch,
        };

    public Task<string?> GetBranchHeadAsync(
        RepoRef repo,
        string branch,
        CancellationToken cancellationToken = default)
        => Task.FromResult(BranchHeads.TryGetValue(branch, out var head) ? head : null);

    public Task CreateBranchAsync(
        RepoRef repo,
        string branch,
        string fromRevision,
        CancellationToken cancellationToken = default)
    {
        BranchesCreated.Add(branch);
        BranchHeads[branch] = fromRevision;

        return Task.CompletedTask;
    }

    public Task<PushResult> PushAsync(
        RepoRef repo,
        string branch,
        string revision,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var created = !BranchHeads.ContainsKey(branch);
        BranchHeads[branch] = revision;

        var result = new PushResult(branch, revision, created);
        Pushes.Add(result);

        return Task.FromResult(result);
    }

    public Task<ChangeRequestSnapshot> OpenChangeRequestAsync(
        OpenChangeRequestCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!Capabilities.ChangeRequests)
        {
            throw new VersionControlCapabilityException(Id, "opening a change request");
        }

        Opened.Add(command);
        var number = NextNumber++;

        var labels = Capabilities.ChangeRequestLabels ? command.Labels ?? [] : [];

        if (labels.Count > 0)
        {
            Labelled.Add((number, labels));
        }

        return Task.FromResult(new ChangeRequestSnapshot
        {
            Number = number,
            Url = $"https://example.test/{command.Repo.Path}/changes/{number}",
            State = ChangeRequestState.Open,
            HeadRevision = BranchHeads.TryGetValue(command.Source, out var head) ? head : "headsha",
            SourceBranch = command.Source,
            TargetBranch = command.Target,
            AuthorLogin = AuthorLogin,
            Labels = labels,
        });
    }

    public Task<bool> CommentOnChangeRequestAsync(
        ChangeRequestRef changeRequest,
        string bodyMarkdown,
        CancellationToken cancellationToken = default)
    {
        if (!Capabilities.ChangeRequestComments)
        {
            return Task.FromResult(false);
        }

        Comments.Add((changeRequest.Number, bodyMarkdown));

        return Task.FromResult(true);
    }

    public Task<ChangeRequestSnapshot?> GetChangeRequestStateAsync(
        ChangeRequestRef changeRequest,
        CancellationToken cancellationToken = default)
        => Task.FromResult<ChangeRequestSnapshot?>(null);

    public Task<bool> LabelChangeRequestAsync(
        ChangeRequestRef changeRequest,
        IReadOnlyList<string> labels,
        CancellationToken cancellationToken = default)
    {
        if (!Capabilities.ChangeRequestLabels)
        {
            return Task.FromResult(false);
        }

        Labelled.Add((changeRequest.Number, labels));

        return Task.FromResult(true);
    }

    public Task<RevisionComparison> CompareAsync(
        RepoRef repo,
        string baseRevision,
        string headRevision,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Comparisons.TryGetValue((baseRevision, headRevision), out var comparison)
            ? comparison
            : new RevisionComparison(0, 0, []));

    public Task<WebhookRegistration> RegisterWebhookAsync(
        RepoRef repo,
        WebhookSubscription subscription,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new WebhookRegistration("1", subscription.CallbackUrl, true));

    public Task<BranchProtectionStatus> GetBranchProtectionAsync(
        RepoRef repo,
        string branch,
        CancellationToken cancellationToken = default)
    {
        if (!Capabilities.BranchProtection)
        {
            return Task.FromResult(BranchProtectionStatus.Unsupported);
        }

        if (ProtectionFailure is not null)
        {
            throw ProtectionFailure;
        }

        return Task.FromResult(Protection.TryGetValue(branch, out var status)
            ? status
            : BranchProtectionStatus.None);
    }

    public Task<RepoRef> CreateRepositoryAsync(
        NewRepositoryRequest request,
        CancellationToken cancellationToken = default)
        => Capabilities.RepoCreation
            ? Task.FromResult(new RepoRef { ProviderId = Id, Path = $"{request.Owner}/{request.Name}" })
            : throw new VersionControlCapabilityException(Id, "creating a repository");

    public Task<RepoRef> TransferRepositoryAsync(
        RepoRef repo,
        string newOwner,
        CancellationToken cancellationToken = default)
        => Capabilities.RepoTransfer
            ? Task.FromResult(repo with { Path = $"{newOwner}/{repo.Path.Split('/')[^1]}" })
            : throw new VersionControlCapabilityException(Id, "transferring a repository");

    public Task ApplyBranchProtectionAsync(
        RepoRef repo,
        BranchProtectionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!Capabilities.BranchProtection)
        {
            throw new VersionControlCapabilityException(Id, "applying branch protection");
        }

        Protection[request.Branch] = new BranchProtectionStatus(
            true,
            request.RequiredApprovals > 0 || request.RequireCodeOwnerReview,
            request.RequiredApprovals,
            request.RequireCodeOwnerReview,
            request.DismissStaleReviews,
            request.EnforceForAdministrators);

        return Task.CompletedTask;
    }
}
