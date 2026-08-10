using Charter.Data;
using Charter.Domain;
using Charter.GitHub;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Charter.Deployments;

/// <summary>
/// Takes the preview away when the change request closes.
/// </summary>
/// <remarks>
/// <para>
/// Railway reclaims a PR environment on its own when the change request closes, and so does every
/// comparable platform. Charter still acts, for two reasons. The row has to stop saying <c>ready</c>,
/// or a requester who opens a week-old notification gets a dead host and reports the product as
/// broken — which is precisely the confusion section 27.7 exists to prevent. And an instance whose
/// provider does <em>not</em> reclaim automatically needs somebody to ask, or section 27.5's warning
/// about costs running away comes true.
/// </para>
/// <para>
/// Merged and abandoned are treated identically. The change is either in the base branch or it is
/// gone; either way the ephemeral copy is not the thing to look at any more.
/// </para>
/// </remarks>
public sealed class DeploymentChangeRequestListener : IGitHubWebhookListener
{
    private readonly CharterDbContext _database;
    private readonly DeploymentIngestor _ingestor;
    private readonly PreviewExpiry _expiry;
    private readonly ILogger<DeploymentChangeRequestListener> _logger;

    public DeploymentChangeRequestListener(
        CharterDbContext database,
        DeploymentIngestor ingestor,
        PreviewExpiry expiry,
        ILogger<DeploymentChangeRequestListener> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(ingestor);
        ArgumentNullException.ThrowIfNull(expiry);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _ingestor = ingestor;
        _expiry = expiry;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnDeliveryAsync(
        GitHubWebhookDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        if (delivery.Type != GitHubWebhookEventType.PullRequest
            || !string.Equals(delivery.Action, "closed", StringComparison.Ordinal)
            || delivery.RepositoryFullName is not { Length: > 0 } repository
            || delivery.PullRequestNumber is not { } number)
        {
            return;
        }

        var context = await _ingestor.FindAsync(repository, number, cancellationToken);

        if (context is null)
        {
            return;
        }

        await ExpireAsync(context.SessionId, cancellationToken);
        await _expiry.TearDownAsync(context.SessionId, cancellationToken);

        _logger.LogInformation(
            "Cleaned up the preview for {Repo}#{Number}, which is now closed",
            repository,
            number);
    }

    private async Task ExpireAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var artifacts = await _database.VerificationArtifacts
            .Where(row => row.SessionId == sessionId
                          && row.Kind == VerificationArtifactKind.HostedPreview
                          && row.State == VerificationArtifactState.Ready)
            .ToListAsync(cancellationToken);

        if (artifacts.Count == 0)
        {
            return;
        }

        foreach (var artifact in artifacts)
        {
            artifact.MarkExpired();
        }

        await _database.SaveChangesAsync(cancellationToken);
    }
}
