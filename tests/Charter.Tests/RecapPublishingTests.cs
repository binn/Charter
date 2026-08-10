using Charter.Domain;
using Charter.Models;
using Charter.Recaps;
using Charter.VersionControl;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>
/// Section 14, rule one: the recap is posted where engineers actually review, and where it cannot
/// be, it falls back to the session view <em>and says so</em> rather than being dropped.
/// </summary>
public class RecapPublishingTests
{
    [Fact]
    public async Task ItPostsTheRecapAsAChangeRequestComment()
    {
        var provider = new FakeVersionControlProvider();
        var publisher = Publisher(provider);
        var recap = Recap();

        var publication = await publisher.PublishAsync(
            recap,
            Repository(),
            changeRequestNumber: 41,
            TestContext.Current.CancellationToken);

        Assert.Equal(RecapSurface.ProviderComment, publication.Surface);
        Assert.True(publication.PostedToProvider);
        Assert.Null(publication.Reason);

        var comment = Assert.Single(provider.Comments);
        Assert.Equal(41, comment.Number);
        Assert.Equal(recap.BodyMarkdown, comment.Body);
        Assert.Equal(recap.BodyMarkdown, publication.BodyMarkdown);
    }

    [Fact]
    public async Task WhenTheProviderCannotCommentItFallsBackAndSaysSo()
    {
        var provider = new FakeVersionControlProvider();
        provider.DeclaredCapabilities = provider.DeclaredCapabilities with { ChangeRequestComments = false };

        var publisher = Publisher(provider);
        var recap = Recap();

        var publication = await publisher.PublishAsync(
            recap,
            Repository(),
            changeRequestNumber: 41,
            TestContext.Current.CancellationToken);

        Assert.Equal(RecapSurface.SessionView, publication.Surface);
        Assert.False(publication.PostedToProvider);
        Assert.Empty(provider.Comments);

        // The recap still exists in full, with a line at the top explaining where it is not.
        Assert.Contains("could not be posted", publication.BodyMarkdown, StringComparison.Ordinal);
        Assert.Contains("no comment surface", publication.BodyMarkdown, StringComparison.Ordinal);
        Assert.Contains("pull request", publication.BodyMarkdown, StringComparison.Ordinal);
        Assert.Contains("### 1. What changed, and why", publication.BodyMarkdown, StringComparison.Ordinal);
        Assert.NotNull(publication.Reason);
    }

    [Fact]
    public async Task AProviderWithNoChangeRequestSurfaceAtAllFallsBackToo()
    {
        var provider = new FakeVersionControlProvider
        {
            Terms = VersionControlTerms.ChangeRequestDefault,
        };
        provider.DeclaredCapabilities = provider.DeclaredCapabilities with
        {
            ChangeRequests = false,
            ChangeRequestComments = false,
        };

        var publication = await Publisher(provider).PublishAsync(
            Recap(),
            Repository(),
            changeRequestNumber: 7,
            TestContext.Current.CancellationToken);

        Assert.Equal(RecapSurface.SessionView, publication.Surface);
        Assert.Contains("change request", publication.BodyMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASessionWithNoChangeRequestKeepsTheRecapInCharter()
    {
        var provider = new FakeVersionControlProvider();

        var publication = await Publisher(provider).PublishAsync(
            Recap(),
            Repository(),
            changeRequestNumber: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(RecapSurface.SessionView, publication.Surface);
        Assert.Contains("no open pull request", publication.BodyMarkdown, StringComparison.Ordinal);
        Assert.Empty(provider.Comments);
    }

    [Fact]
    public async Task AProviderThatThrowsDoesNotLoseTheRecap()
    {
        var provider = new ThrowingCommentProvider();

        var publication = await Publisher(provider).PublishAsync(
            Recap(),
            Repository(),
            changeRequestNumber: 41,
            TestContext.Current.CancellationToken);

        Assert.Equal(RecapSurface.SessionView, publication.Surface);
        Assert.Contains("### 3. Files, ranked by risk", publication.BodyMarkdown, StringComparison.Ordinal);
        Assert.Contains("could not be reached", publication.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInstanceWithNoProviderRegisteredStillRendersTheRecap()
    {
        var publisher = new RecapPublisher(
            new VersionControlProviderRegistry([]),
            NullLogger<RecapPublisher>.Instance);

        var publication = await publisher.PublishAsync(
            Recap(),
            Repository(),
            changeRequestNumber: 41,
            TestContext.Current.CancellationToken);

        Assert.Equal(RecapSurface.SessionView, publication.Surface);
        Assert.Contains("no version control provider", publication.BodyMarkdown, StringComparison.Ordinal);
    }

    private static RecapPublisher Publisher(IVersionControlProvider provider) => new(
        new VersionControlProviderRegistry([provider]),
        NullLogger<RecapPublisher>.Instance);

    private static Repo Repository() => Repo.Connect(Guid.CreateVersion7(), 4242, "acme/spectra");

    private static RecapResult Recap()
    {
        var ranked = RecapFileRiskRanker.Rank(
        [
            new RecapFileChange("src/Auth/TokenIssuer.cs"),
            new RecapFileChange("src/Features/Quotes/QuoteLine.razor"),
        ]);

        var (body, riskItems, _) = RecapComposer.Compose(
            RecapStubs.Evidence(),
            ranked,
            new RecapPayload { WhatAndWhy = "Added a derate column to quote lines." },
            new RecapOptions());

        return new RecapResult
        {
            SessionId = RecapStubs.SessionId,
            BodyMarkdown = body,
            RankedFiles = ranked,
            RiskItemsJson = riskItems,
            Usage = ModelUsage.Empty,
            Charge = ModelCharge.None,
        };
    }
}

/// <summary>
/// A provider whose comment call fails the way a revoked installation does. Only the members the
/// publisher touches are implemented; everything else would be a lie about what this test covers.
/// </summary>
internal sealed class ThrowingCommentProvider : IVersionControlProvider
{
    public string Id => "github";

    public string DisplayName => "Fake";

    public VersionControlCapabilities Capabilities { get; } = new()
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

    public VersionControlTerms Terms => VersionControlTerms.PullRequest;

    public Task<bool> CommentOnChangeRequestAsync(
        ChangeRequestRef changeRequest,
        string bodyMarkdown,
        CancellationToken cancellationToken = default)
        => throw new HttpRequestException("401 Unauthorized");

    public Task<VersionControlCredential> AuthenticateRepoAsync(
        RepoRef repo,
        VersionControlAccess access = VersionControlAccess.Read,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<WorkspaceCheckout> PrepareWorkspaceAsync(
        RepoRef repo,
        string revision,
        string? workingBranch = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<string?> GetBranchHeadAsync(
        RepoRef repo,
        string branch,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task CreateBranchAsync(
        RepoRef repo,
        string branch,
        string fromRevision,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<PushResult> PushAsync(
        RepoRef repo,
        string branch,
        string revision,
        bool force = false,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<ChangeRequestSnapshot> OpenChangeRequestAsync(
        OpenChangeRequestCommand command,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<ChangeRequestSnapshot?> GetChangeRequestStateAsync(
        ChangeRequestRef changeRequest,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<bool> LabelChangeRequestAsync(
        ChangeRequestRef changeRequest,
        IReadOnlyList<string> labels,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<RevisionComparison> CompareAsync(
        RepoRef repo,
        string baseRevision,
        string headRevision,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<WebhookRegistration> RegisterWebhookAsync(
        RepoRef repo,
        WebhookSubscription subscription,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<BranchProtectionStatus> GetBranchProtectionAsync(
        RepoRef repo,
        string branch,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<RepoRef> CreateRepositoryAsync(
        NewRepositoryRequest request,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<RepoRef> TransferRepositoryAsync(
        RepoRef repo,
        string newOwner,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task ApplyBranchProtectionAsync(
        RepoRef repo,
        BranchProtectionRequest request,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
