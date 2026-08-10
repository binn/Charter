using Charter.Data;
using Charter.Domain;
using Charter.GitHub;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Charter.Deployments;

/// <summary>A change request, and everything a provider needs to ask about its preview.</summary>
/// <param name="ChangeRequest">The change request row.</param>
/// <param name="SessionId">The session that opened it; the artifact hangs off this.</param>
/// <param name="RepoFullName"><c>owner/name</c>.</param>
public sealed record PreviewContext(ChangeRequest ChangeRequest, Guid SessionId, string RepoFullName)
{
    /// <summary>The provider-facing view of this change request.</summary>
    public DeploymentTarget Target => new(
        RepoFullName,
        ChangeRequest.Number,
        ChangeRequest.HeadSha,
        ChangeRequest.HeadBranch,

        // Nothing records who opened a change request on the provider's side yet, so a provider that
        // wants to name them says "the author" instead of inventing a login.
        AuthorLogin: null,
        HeadSeenAt: ChangeRequest.UpdatedAt);
}

/// <summary>
/// Both ingestion paths of section 18, ending in the artifact a requester clicks.
/// </summary>
/// <remarks>
/// <para>
/// Path one is the generic webhook, <c>POST /api/deployments/{prSha}</c>, and it is deliberately not
/// reimplemented here: <see cref="DeploymentBinder"/> already owns the binding, the state vocabulary
/// and the admission rule, and this type adds the step the binder does not do — producing the
/// verification artifact. Everything a Railway path can do, a Render or Coolify self-hoster gets by
/// calling that endpoint.
/// </para>
/// <para>
/// Path two is change request comment parsing, which is fragile and universal. It runs through the
/// same binder for the same reason, converting the provider's reading back into the webhook's own
/// vocabulary rather than growing a second write path that could disagree with the first.
/// </para>
/// </remarks>
public sealed class DeploymentIngestor
{
    private readonly CharterDbContext _database;
    private readonly DeploymentBinder _binder;
    private readonly PreviewArtifactPublisher _publisher;
    private readonly DeploymentProviderRegistry _providers;
    private readonly ILogger<DeploymentIngestor> _logger;

    public DeploymentIngestor(
        CharterDbContext database,
        DeploymentBinder binder,
        PreviewArtifactPublisher publisher,
        DeploymentProviderRegistry providers,
        ILogger<DeploymentIngestor> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(binder);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _binder = binder;
        _publisher = publisher;
        _providers = providers;
        _logger = logger;
    }

    /// <summary>
    /// Records a generic webhook report and publishes the resulting artifact.
    /// </summary>
    /// <remarks>
    /// The head commit SHA is the authorisation, exactly as it is for the binder: a report naming a
    /// commit no change request in this instance carries is refused with
    /// <see cref="DeploymentBindingOutcome.UnknownCommit"/>, which the endpoint turns into a 404.
    /// </remarks>
    public async Task<DeploymentBindingResult> ReportAsync(
        string headSha,
        DeploymentWebhookRequest report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        var result = await _binder.ReportAsync(headSha, report, cancellationToken);

        if (result.Outcome == DeploymentBindingOutcome.Recorded)
        {
            await PublishAsync(headSha, cancellationToken);
        }

        return result;
    }

    /// <summary>
    /// Reads a change request comment for a preview — the universal fallback of section 18.
    /// </summary>
    /// <remarks>
    /// A comment that does not parse is not an error and never will be. The overwhelming majority of
    /// comments on a change request are people talking to each other, so "this one was about
    /// something else" is the normal outcome and returns
    /// <see cref="DeploymentBindingOutcome.Invalid"/> with an explanation rather than raising
    /// anything.
    /// </remarks>
    public async Task<DeploymentBindingResult> IngestCommentAsync(
        string repoFullName,
        int number,
        DeploymentComment comment,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoFullName);
        ArgumentNullException.ThrowIfNull(comment);

        if (_providers.Configured is not { Capabilities.CommentParsing: true } provider)
        {
            return new DeploymentBindingResult(
                DeploymentBindingOutcome.Invalid,
                "no configured deployment provider reads change request comments");
        }

        var context = await FindAsync(repoFullName, number, cancellationToken);

        if (context is null)
        {
            return new DeploymentBindingResult(
                DeploymentBindingOutcome.UnknownCommit,
                "no change request in this instance matches that repository and number");
        }

        var observation = provider.ReadComment(comment, context.Target);

        if (observation is not { Availability: DeploymentAvailability.Reported, Report: { } report })
        {
            _logger.LogDebug(
                "A comment on {Repo}#{Number} did not name a preview: {Explanation}",
                repoFullName,
                number,
                observation.Explanation ?? "not yet");

            return new DeploymentBindingResult(
                DeploymentBindingOutcome.Invalid,
                observation.Explanation ?? "this comment did not name a preview");
        }

        return await ReportAsync(context.ChangeRequest.HeadSha, ToWebhookRequest(report), cancellationToken);
    }

    /// <summary>Asks the configured provider what it has, and records anything it reports.</summary>
    /// <returns>What the provider said, so a caller can log a blocked preview.</returns>
    public async Task<DeploymentObservation> PollAsync(
        PreviewContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_providers.Configured is not { Capabilities.Poll: true } provider)
        {
            return DeploymentObservation.Unsupported("no configured deployment provider can be polled");
        }

        var observation = await provider.ObserveAsync(context.Target, cancellationToken);

        if (observation is { Availability: DeploymentAvailability.Reported, Report: { } report })
        {
            await ReportAsync(context.ChangeRequest.HeadSha, ToWebhookRequest(report), cancellationToken);
        }

        return observation;
    }

    /// <summary>Re-applies the latest deployment for a commit to the session's artifact.</summary>
    public async Task<PreviewPublication?> PublishAsync(
        string headSha,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headSha);

        var sha = headSha.Trim();

        // The same row the binder chose, chosen the same way, so the artifact cannot end up hanging
        // off a different change request than the deployment was bound to.
        var changeRequest = await _database.ChangeRequests
            .Where(candidate => candidate.HeadSha == sha)
            .OrderByDescending(candidate => candidate.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (changeRequest is null)
        {
            return null;
        }

        var deployment = await _database.Deployments
            .Where(candidate => candidate.ChangeRequestId == changeRequest.Id)
            .OrderByDescending(candidate => candidate.ReportedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (deployment is null)
        {
            return null;
        }

        return await _publisher.ApplyAsync(changeRequest.SessionId, deployment, cancellationToken);
    }

    /// <summary>Finds a change request by repository and number.</summary>
    /// <remarks>
    /// A comment webhook names a repository and an issue number; it does not name a commit. The
    /// repository lives four joins away from a change request — session, spec, request, repo — because
    /// section 5 hangs a change request off the session that opened it rather than off the repository.
    /// </remarks>
    public async Task<PreviewContext?> FindAsync(
        string repoFullName,
        int number,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoFullName);

        var name = repoFullName.Trim();

        return await (
            from changeRequest in _database.ChangeRequests
            join session in _database.Sessions on changeRequest.SessionId equals session.Id
            join spec in _database.Specs on session.SpecId equals spec.Id
            join request in _database.Requests on spec.RequestId equals request.Id
            join repo in _database.Repos on request.RepoId equals repo.Id
            where changeRequest.Number == number && repo.FullName == name
            orderby changeRequest.UpdatedAt descending
            select new PreviewContext(changeRequest, session.Id, repo.FullName))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Every open change request that could still be waiting for a preview.</summary>
    public async Task<IReadOnlyList<PreviewContext>> OpenAsync(CancellationToken cancellationToken = default)
        => await (
            from changeRequest in _database.ChangeRequests
            join session in _database.Sessions on changeRequest.SessionId equals session.Id
            join spec in _database.Specs on session.SpecId equals spec.Id
            join request in _database.Requests on spec.RequestId equals request.Id
            join repo in _database.Repos on request.RepoId equals repo.Id
            where changeRequest.State == ChangeRequestState.Open
                  || changeRequest.State == ChangeRequestState.Draft
            select new PreviewContext(changeRequest, session.Id, repo.FullName))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Turns a provider's report back into the generic webhook's own vocabulary.
    /// </summary>
    /// <remarks>
    /// <see cref="DeploymentState"/> member names lower-case to exactly the tokens
    /// <see cref="DeploymentBinder.TryParseState"/> accepts, which is what lets both ingestion paths
    /// go through one binder instead of two write paths that could drift apart.
    /// </remarks>
    public static DeploymentWebhookRequest ToWebhookRequest(DeploymentReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new DeploymentWebhookRequest(
            report.Url,
            report.State.ToString().ToLowerInvariant(),
            report.Provider);
    }
}
