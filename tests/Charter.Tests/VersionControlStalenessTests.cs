using Charter.Domain;
using Charter.GitHub;
using Charter.VersionControl;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

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
    public async Task AReviewMovesTheRequestToInReview()
    {
        await using var world = await ChangeRequestWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await OpenAsync(world, "headsha");
        await world.SetRequestStatusAsync(RequestStatus.PreviewReady);

        var applied = await world.Tracker.ReviewAsync(
            new ChangeRequestReviewReport(
                world.Repo.FullName,
                142,
                ChangeRequestReviewKind.Submitted,
                "approved"),
            TestContext.Current.CancellationToken);

        Assert.True(applied);

        // Section 6: an engineer is checking it. Both halves move — the session an engineer reads and
        // the thread the requester reads.
        Assert.Equal(SessionStatus.InReview, await world.StatusAsync());
        Assert.Equal(RequestStatus.InReview, await world.RequestStatusAsync());
    }

    [Fact]
    public async Task AReviewRequestIsEnoughToReachInReview()
    {
        await using var world = await ChangeRequestWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await OpenAsync(world, "headsha");

        // What the thread says while the session is building: "Building this now" (section 6).
        await world.SetRequestStatusAsync(RequestStatus.Running);

        // On a repository with CODEOWNERS this is what happens first, and it can be the only thing
        // that happens for a day.
        Assert.True(await world.Tracker.ReviewAsync(
            new ChangeRequestReviewReport(world.Repo.FullName, 142, ChangeRequestReviewKind.Requested),
            TestContext.Current.CancellationToken));

        Assert.Equal(SessionStatus.InReview, await world.StatusAsync());
        Assert.Equal(RequestStatus.InReview, await world.RequestStatusAsync());
    }

    [Fact]
    public async Task AReviewOfSomebodyElsesPullRequestChangesNothing()
    {
        await using var world = await ChangeRequestWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await OpenAsync(world, "headsha");

        Assert.False(await world.Tracker.ReviewAsync(
            new ChangeRequestReviewReport(world.Repo.FullName, 999, ChangeRequestReviewKind.Submitted),
            TestContext.Current.CancellationToken));

        Assert.Equal(SessionStatus.PrOpen, await world.StatusAsync());
    }

    [Fact]
    public async Task AReviewAfterAMergeDoesNotDragTheThreadBackwards()
    {
        await using var world = await ChangeRequestWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await OpenAsync(world, "headsha");

        await world.Tracker.ApplyAsync(
            new ChangeRequestStateReport(world.Repo.FullName, 142, ChangeRequestState.Merged),
            TestContext.Current.CancellationToken);

        world.Db.ChangeTracker.Clear();

        // A comment on a merged pull request is normal, and section 6 has no edge back from Merged.
        Assert.False(await world.Tracker.ReviewAsync(
            new ChangeRequestReviewReport(world.Repo.FullName, 142, ChangeRequestReviewKind.Submitted),
            TestContext.Current.CancellationToken));

        Assert.Equal(SessionStatus.Merged, await world.StatusAsync());
        Assert.Equal(RequestStatus.Merged, await world.RequestStatusAsync());
    }

    [Fact]
    public async Task AMergeTellsTheRequesterItIsLive()
    {
        await using var world = await ChangeRequestWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await OpenAsync(world, "headsha");
        await world.SetRequestStatusAsync(RequestStatus.InReview);
        await world.SetSessionStatusAsync(SessionStatus.InReview);

        await world.Tracker.ApplyAsync(
            new ChangeRequestStateReport(world.Repo.FullName, 142, ChangeRequestState.Merged),
            TestContext.Current.CancellationToken);

        // The payoff for the whole pipeline: "This is live" (section 6). Nothing else sets it.
        Assert.Equal(RequestStatus.Merged, await world.RequestStatusAsync());
        Assert.Equal(SessionStatus.Merged, await world.StatusAsync());
    }

    [Fact]
    public async Task StalenessIsRecordedOnTheTranscriptRatherThanOnlyOnARow()
    {
        await using var world = await ChangeRequestWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await OpenAsync(world, "headsha");

        world.Provider.Comparisons[("basesha", "newbase")] = new RevisionComparison(0, 2, ["src/Total.cs"]);
        world.Provider.Comparisons[("newbase", "headsha")] =
            new RevisionComparison(2, 1, ["src/Total.cs", "src/Widget.cs"]);

        await world.Tracker.MarkStaleAsync(
            new BranchPushReport(world.Repo.FullName, "main", "newbase", "basesha"),
            TestContext.Current.CancellationToken);

        var payload = Assert.Single(await world.EventsAsync(ChangeRequestEventTypes.MarkedStale));

        Assert.Contains("base_branch_moved", payload, StringComparison.Ordinal);
        Assert.Contains("newbase", payload, StringComparison.Ordinal);

        // The session is not retired for it: section 17's remedy is a rebase, not an ending, and
        // Stale is terminal.
        Assert.Equal(SessionStatus.PrOpen, await world.StatusAsync());
    }

    [Fact]
    public void AReviewDeliveryIsRecognisedInBothOfTheShapesGitHubSendsIt()
    {
        var submitted = GitHubWebhookDelivery.Parse(
            "pull_request_review",
            """
            {
              "action": "submitted",
              "review": { "state": "approved" },
              "pull_request": { "number": 142, "head": { "sha": "headsha", "ref": "charter/session-1" } },
              "repository": { "full_name": "acme/widgets" }
            }
            """);

        Assert.Equal(GitHubWebhookEventType.PullRequestReview, submitted.Type);
        Assert.Equal(142, submitted.PullRequestNumber);
        Assert.Equal("approved", submitted.ReviewState);
        Assert.Equal("headsha", submitted.HeadSha);
        Assert.True(submitted.IsReviewSignal);

        var requested = GitHubWebhookDelivery.Parse(
            "pull_request",
            """
            {
              "action": "review_requested",
              "pull_request": { "number": 142, "head": { "sha": "headsha" } },
              "repository": { "full_name": "acme/widgets" }
            }
            """);

        Assert.True(requested.IsReviewSignal);

        // A review that was dismissed or edited is not somebody picking the work up.
        var dismissed = GitHubWebhookDelivery.Parse(
            "pull_request_review",
            """{"action":"dismissed","review":{"state":"dismissed"},"pull_request":{"number":142}}""");

        Assert.False(dismissed.IsReviewSignal);

        // And neither is an ordinary synchronize.
        var pushed = GitHubWebhookDelivery.Parse(
            "pull_request",
            """{"action":"synchronize","pull_request":{"number":142}}""");

        Assert.False(pushed.IsReviewSignal);
    }

    [Fact]
    public async Task TheListenerTurnsAReviewDeliveryIntoInReview()
    {
        await using var world = await ChangeRequestWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await OpenAsync(world, "headsha");

        var listener = new GitHubChangeRequestListener(
            world.Tracker,
            NullLogger<GitHubChangeRequestListener>.Instance);

        await listener.OnDeliveryAsync(
            GitHubWebhookDelivery.Parse(
                "pull_request_review",
                $$"""
                  {
                    "action": "submitted",
                    "review": { "state": "changes_requested" },
                    "pull_request": { "number": 142, "head": { "sha": "headsha" } },
                    "repository": { "full_name": "{{world.Repo.FullName}}" }
                  }
                  """),
            TestContext.Current.CancellationToken);

        // Changes requested is still "an engineer is checking it". Charter never reports a verdict on
        // the code: that is decided on the provider (section 7.4).
        Assert.Equal(SessionStatus.InReview, await world.StatusAsync());
    }

    [Fact]
    public async Task TheListenerTurnsAMergeDeliveryIntoMerged()
    {
        await using var world = await ChangeRequestWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await OpenAsync(world, "headsha");

        var listener = new GitHubChangeRequestListener(
            world.Tracker,
            NullLogger<GitHubChangeRequestListener>.Instance);

        await listener.OnDeliveryAsync(
            GitHubWebhookDelivery.Parse(
                "pull_request",
                $$"""
                  {
                    "action": "closed",
                    "pull_request": { "number": 142, "merged": true, "head": { "sha": "headsha" } },
                    "repository": { "full_name": "{{world.Repo.FullName}}" }
                  }
                  """),
            TestContext.Current.CancellationToken);

        Assert.Equal(ChangeRequestState.Merged, Assert.Single(await world.ChangeRequestsAsync()).State);
        Assert.Equal(SessionStatus.Merged, await world.StatusAsync());
        Assert.Equal(RequestStatus.Merged, await world.RequestStatusAsync());
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
