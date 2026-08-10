using Charter.Domain;
using Charter.GitHub;
using Charter.VersionControl;
using Microsoft.EntityFrameworkCore;

namespace Charter.Tests;

/// <summary>
/// Change request state from the webhook, and section 17's staleness rule.
/// </summary>
/// <remarks>
/// The rule has two halves and both are load-bearing: <em>behind</em> and <em>overlapping on
/// changed files</em>. Most open change requests are behind most of the time, so a flag that fired
/// on behind-ness alone is one everybody learns to ignore — which is worse than not having one.
/// </remarks>
public class VersionControlStalenessTests
{
    [Fact]
    public async Task AMergedChangeRequestMovesItsSession()
    {
        await using var world = await ChangeRequestWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await OpenAsync(world, "headsha");

        var applied = await world.Tracker.ApplyAsync(
            new ChangeRequestStateReport(world.Repo.FullName, 142, ChangeRequestState.Merged),
            TestContext.Current.CancellationToken);

        Assert.True(applied);

        var row = Assert.Single(await world.ChangeRequestsAsync());
        Assert.Equal(ChangeRequestState.Merged, row.State);
        Assert.Equal(SessionStatus.Merged, await world.StatusAsync());
    }

    [Fact]
    public async Task AClosedChangeRequestIsRecordedWithoutEndingTheSession()
    {
        await using var world = await ChangeRequestWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await OpenAsync(world, "headsha");

        await world.Tracker.ApplyAsync(
            new ChangeRequestStateReport(world.Repo.FullName, 142, ChangeRequestState.Closed),
            TestContext.Current.CancellationToken);

        // Section 7.5's "revise and rebuild" opens another one, so closing is not terminal.
        Assert.Equal(ChangeRequestState.Closed, Assert.Single(await world.ChangeRequestsAsync()).State);
        Assert.Equal(SessionStatus.PrOpen, await world.StatusAsync());
    }

    [Fact]
    public async Task AStateReportForSomebodyElsesChangeRequestIsIgnored()
    {
        await using var world = await ChangeRequestWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await OpenAsync(world, "headsha");

        // Repositories have human contributors, and every one of their pull requests arrives here.
        var applied = await world.Tracker.ApplyAsync(
            new ChangeRequestStateReport(world.Repo.FullName, 999, ChangeRequestState.Merged),
            TestContext.Current.CancellationToken);

        Assert.False(applied);
        Assert.Equal(ChangeRequestState.Open, Assert.Single(await world.ChangeRequestsAsync()).State);
    }

    [Fact]
    public async Task BehindAndOverlappingIsStale()
    {
        await using var world = await ChangeRequestWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await OpenAsync(world, "headsha");

        // What landed on main, and what this change request touches. They share a file.
        world.Provider.Comparisons[("basesha", "newbase")] = new RevisionComparison(0, 2, ["src/Total.cs"]);
        world.Provider.Comparisons[("newbase", "headsha")] =
            new RevisionComparison(2, 1, ["src/Total.cs", "src/Widget.cs"]);

        var stale = await world.Tracker.MarkStaleAsync(
            new BranchPushReport(world.Repo.FullName, "main", "newbase", "basesha"),
            TestContext.Current.CancellationToken);

        Assert.Single(stale);
        Assert.True(Assert.Single(await world.ChangeRequestsAsync()).IsStale);
    }

    [Fact]
    public async Task BehindButDisjointIsNotStale()
    {
        await using var world = await ChangeRequestWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await OpenAsync(world, "headsha");

        world.Provider.Comparisons[("basesha", "newbase")] = new RevisionComparison(0, 2, ["docs/index.md"]);
        world.Provider.Comparisons[("newbase", "headsha")] = new RevisionComparison(2, 1, ["src/Widget.cs"]);

        var stale = await world.Tracker.MarkStaleAsync(
            new BranchPushReport(world.Repo.FullName, "main", "newbase", "basesha"),
            TestContext.Current.CancellationToken);

        Assert.Empty(stale);
        Assert.False(Assert.Single(await world.ChangeRequestsAsync()).IsStale);
    }

    [Fact]
    public async Task OverlappingButNotBehindIsNotStale()
    {
        await using var world = await ChangeRequestWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await OpenAsync(world, "headsha");

        // The change request already carries what landed: same files, but nothing to catch up on.
        world.Provider.Comparisons[("basesha", "newbase")] = new RevisionComparison(0, 1, ["src/Total.cs"]);
        world.Provider.Comparisons[("newbase", "headsha")] = new RevisionComparison(0, 1, ["src/Total.cs"]);

        var stale = await world.Tracker.MarkStaleAsync(
            new BranchPushReport(world.Repo.FullName, "main", "newbase", "basesha"),
            TestContext.Current.CancellationToken);

        Assert.Empty(stale);
    }

    [Fact]
    public async Task APushToABranchOtherThanTheBaseMarksNothing()
    {
        await using var world = await ChangeRequestWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await OpenAsync(world, "headsha");

        var stale = await world.Tracker.MarkStaleAsync(
            new BranchPushReport(world.Repo.FullName, "feature/other", "newbase", "basesha"),
            TestContext.Current.CancellationToken);

        Assert.Empty(stale);
    }

    [Fact]
    public async Task APushWithNoPreviousRevisionMarksNothing()
    {
        await using var world = await ChangeRequestWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await OpenAsync(world, "headsha");
        world.Provider.Comparisons[("newbase", "headsha")] = new RevisionComparison(2, 1, ["src/Total.cs"]);

        // Without the previous head there is no way to ask which files landed, and half of the rule
        // is the half that produces false positives.
        var stale = await world.Tracker.MarkStaleAsync(
            new BranchPushReport(world.Repo.FullName, "main", "newbase"),
            TestContext.Current.CancellationToken);

        Assert.Empty(stale);
    }

    [Fact]
    public async Task ANewCommitOnTheChangeRequestClearsStaleness()
    {
        await using var world = await ChangeRequestWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await OpenAsync(world, "headsha");

        world.Provider.Comparisons[("basesha", "newbase")] = new RevisionComparison(0, 1, ["src/Total.cs"]);
        world.Provider.Comparisons[("newbase", "headsha")] = new RevisionComparison(1, 1, ["src/Total.cs"]);

        await world.Tracker.MarkStaleAsync(
            new BranchPushReport(world.Repo.FullName, "main", "newbase", "basesha"),
            TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(await world.ChangeRequestsAsync()).IsStale);

        await world.Tracker.ApplyAsync(
            new ChangeRequestStateReport(world.Repo.FullName, 142, ChangeRequestState.Open, "rebasedsha"),
            TestContext.Current.CancellationToken);

        var row = Assert.Single(await world.ChangeRequestsAsync());
        Assert.False(row.IsStale);
        Assert.Equal("rebasedsha", row.HeadSha);
    }

    [Fact]
    public void AWebhookDeliveryTranslatesToAProviderNeutralState()
    {
        Assert.Equal(ChangeRequestState.Merged, Translate("closed", merged: true));
        Assert.Equal(ChangeRequestState.Closed, Translate("closed", merged: false));
        Assert.Equal(ChangeRequestState.Open, Translate("opened", merged: null));
        Assert.Equal(ChangeRequestState.Open, Translate("synchronize", merged: null));
        Assert.Equal(ChangeRequestState.Draft, Translate("converted_to_draft", merged: null));
    }

    [Fact]
    public void APushDeliveryCarriesThePreviousHeadStalenessNeeds()
    {
        var delivery = GitHubWebhookDelivery.Parse(
            "push",
            """
            {
              "ref": "refs/heads/main",
              "before": "basesha",
              "after": "newbase",
              "repository": { "full_name": "acme/widgets" }
            }
            """);

        Assert.Equal("main", delivery.Branch);
        Assert.Equal("basesha", delivery.BeforeSha);
        Assert.Equal("newbase", delivery.HeadSha);
    }

    private static ChangeRequestState Translate(string action, bool? merged)
        => GitHubChangeRequestListener.StateFor(new GitHubWebhookDelivery
        {
            Type = GitHubWebhookEventType.PullRequest,
            EventName = "pull_request",
            Action = action,
            PullRequestMerged = merged,
        });

    private static async Task OpenAsync(ChangeRequestWorld world, string headSha)
    {
        world.Provider.BranchHeads[world.Branch] = headSha;
        world.Provider.Comparisons[("basesha", headSha)] = new RevisionComparison(0, 1, ["src/Widget.cs"]);

        var result = await world.PublishAsync();

        Assert.Equal(ChangeRequestPublication.Opened, result.Outcome);

        world.Db.ChangeTracker.Clear();
    }
}
