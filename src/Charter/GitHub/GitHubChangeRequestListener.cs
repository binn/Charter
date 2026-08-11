using Charter.Domain;
using Charter.VersionControl;
using Microsoft.Extensions.Logging;

namespace Charter.GitHub;

/// <summary>
/// Keeps change request state current from GitHub's webhook, and runs section 17's staleness rule.
/// </summary>
/// <remarks>
/// <para>
/// One of several listeners on the existing receiver rather than a second webhook endpoint: the
/// signature check, the size bound and the delivery parsing are already done by the time this runs,
/// and duplicating them would mean two places to get admission control wrong.
/// </para>
/// <para>
/// Translation happens here and nowhere else. Everything downstream sees
/// <see cref="ChangeRequestStateReport"/> and <see cref="BranchPushReport"/>, which is what lets the
/// same tracker serve a GitLab or Gitea listener later without being touched.
/// </para>
/// </remarks>
public sealed class GitHubChangeRequestListener : IGitHubWebhookListener
{
    private readonly ChangeRequestStateTracker _tracker;
    private readonly ILogger<GitHubChangeRequestListener> _logger;

    public GitHubChangeRequestListener(
        ChangeRequestStateTracker tracker,
        ILogger<GitHubChangeRequestListener> logger)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(logger);

        _tracker = tracker;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnDeliveryAsync(
        GitHubWebhookDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        switch (delivery.Type)
        {
            case GitHubWebhookEventType.PullRequest when delivery.RepositoryFullName is { Length: > 0 } repo
                                                         && delivery.PullRequestNumber is { } number:
                await _tracker.ApplyAsync(
                    new ChangeRequestStateReport(
                        repo,
                        number,
                        StateFor(delivery),
                        delivery.HeadSha,
                        delivery.PullRequestHeadBranch),
                    cancellationToken);

                // A review request arrives as a pull_request action rather than as a review, and on a
                // repository with CODEOWNERS it is the first thing that happens. Applied after the
                // state report so the two writes cannot race each other over the same row.
                if (delivery.IsReviewSignal)
                {
                    await ReviewAsync(repo, number, ChangeRequestReviewKind.Requested, delivery, cancellationToken);
                }

                break;

            case GitHubWebhookEventType.PullRequestReview
                when delivery.IsReviewSignal
                     && delivery.RepositoryFullName is { Length: > 0 } reviewed
                     && delivery.PullRequestNumber is { } reviewedNumber:
                await ReviewAsync(
                    reviewed,
                    reviewedNumber,
                    ChangeRequestReviewKind.Submitted,
                    delivery,
                    cancellationToken);
                break;

            case GitHubWebhookEventType.Push when delivery.RepositoryFullName is { Length: > 0 } pushed
                                                  && delivery.Branch is { Length: > 0 } branch
                                                  && delivery.HeadSha is { Length: > 0 } head:
                var stale = await _tracker.MarkStaleAsync(
                    new BranchPushReport(pushed, branch, head, delivery.BeforeSha),
                    cancellationToken);

                if (stale.Count > 0)
                {
                    _logger.LogInformation(
                        "A push to {Repository}/{Branch} made {Count} change request(s) stale",
                        pushed,
                        branch,
                        stale.Count);
                }

                break;
        }
    }

    private async Task ReviewAsync(
        string repo,
        int number,
        ChangeRequestReviewKind kind,
        GitHubWebhookDelivery delivery,
        CancellationToken cancellationToken)
    {
        if (await _tracker.ReviewAsync(
                new ChangeRequestReviewReport(repo, number, kind, delivery.ReviewState),
                cancellationToken))
        {
            _logger.LogInformation(
                "{Repository}#{Number} is being reviewed on GitHub ({Action})",
                repo,
                number,
                delivery.Action);
        }
    }

    /// <summary>
    /// Maps a <c>pull_request</c> delivery onto Charter's four states.
    /// </summary>
    /// <remarks>
    /// GitHub's <c>action</c> is the reliable signal here rather than any state field, because a
    /// merged pull request arrives as <c>closed</c> with <c>merged: true</c> and the difference
    /// between "merged" and "abandoned" is the whole of section 6's last transition.
    /// </remarks>
    internal static ChangeRequestState StateFor(GitHubWebhookDelivery delivery) => delivery.Action switch
    {
        "closed" when delivery.PullRequestMerged == true => ChangeRequestState.Merged,
        "closed" => ChangeRequestState.Closed,
        "converted_to_draft" => ChangeRequestState.Draft,
        _ => ChangeRequestState.Open,
    };
}
