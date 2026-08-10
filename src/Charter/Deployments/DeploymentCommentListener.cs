using Charter.GitHub;
using Microsoft.Extensions.Logging;

namespace Charter.Deployments;

/// <summary>
/// Section 18's second ingestion path, connected: a comment on a change request becomes a preview.
/// </summary>
/// <remarks>
/// <para>
/// The generic webhook of section 18 is the path a self-hoster wires deliberately. This is the one
/// that works when nobody wired anything, because the hosting platform's bot comments on the change
/// request when the environment comes up and that comment is the only announcement there is. Railway
/// behaves exactly this way, and it is the Phase 1 deployment target.
/// </para>
/// <para>
/// <strong>Nothing here trusts the comment.</strong> The listener does no parsing of its own: it
/// hands the author and the body to <see cref="DeploymentIngestor.IngestCommentAsync"/>, which
/// refuses unless a provider is configured that reads comments, refuses unless the repository and
/// number match a change request this instance opened, and then lets the provider decide whether the
/// author is its own bot. The result binds to that change request's head commit, so the worst a
/// convincing comment can achieve is a wrong URL on a change request its author could already
/// comment on.
/// </para>
/// <para>
/// Every outcome except a recorded one is silent at information level. The overwhelming majority of
/// comments on a change request are people talking to each other, and section 18 is explicit that a
/// comment which does not parse is "not yet", never an error — logging each one would turn the most
/// common event on a change request into a stream of noise.
/// </para>
/// </remarks>
public sealed class DeploymentCommentListener : IGitHubWebhookListener
{
    private readonly DeploymentIngestor _ingestor;
    private readonly ILogger<DeploymentCommentListener> _logger;

    public DeploymentCommentListener(DeploymentIngestor ingestor, ILogger<DeploymentCommentListener> logger)
    {
        ArgumentNullException.ThrowIfNull(ingestor);
        ArgumentNullException.ThrowIfNull(logger);

        _ingestor = ingestor;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnDeliveryAsync(
        GitHubWebhookDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        // `deleted` is the one action to ignore outright: a comment being removed says nothing about
        // whether the environment it announced is still up, and re-reading its text would announce a
        // preview from a comment that no longer exists.
        if (delivery.Type != GitHubWebhookEventType.IssueComment
            || string.Equals(delivery.Action, "deleted", StringComparison.Ordinal)
            || delivery.RepositoryFullName is not { Length: > 0 } repository
            || delivery.IssueNumber is not { } number
            || delivery.CommentBody is not { Length: > 0 } body)
        {
            return;
        }

        var result = await _ingestor.IngestCommentAsync(
            repository,
            number,
            new DeploymentComment(delivery.CommentAuthorLogin, body),
            cancellationToken);

        if (result.Outcome == DeploymentBindingOutcome.Recorded)
        {
            _logger.LogInformation(
                "A comment on {Repo}#{Number} reported a preview",
                repository,
                number);

            return;
        }

        _logger.LogDebug(
            "A comment on {Repo}#{Number} reported no preview: {Detail}",
            repository,
            number,
            result.Detail);
    }
}
