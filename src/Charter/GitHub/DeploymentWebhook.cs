using Charter.Data;
using Charter.Deployments;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Charter.GitHub;

/// <summary>The body of the section 18 generic deployment webhook.</summary>
/// <param name="Url">The preview URL. Required when <paramref name="State"/> is ready.</param>
/// <param name="State">
/// <c>pending</c>, <c>building</c>, <c>ready</c>, <c>failed</c>, <c>cancelled</c> or <c>expired</c>.
/// A handful of provider spellings are accepted as synonyms.
/// </param>
/// <param name="Provider">Free text: <c>railway</c>, <c>render</c>, <c>fly</c>, <c>coolify</c>.</param>
public sealed record DeploymentWebhookRequest(string? Url, string? State, string? Provider);

/// <summary>What the binder made of a report.</summary>
public enum DeploymentBindingOutcome
{
    /// <summary>Bound to a pull request and recorded.</summary>
    Recorded,

    /// <summary>No pull request in this instance has that head SHA.</summary>
    UnknownCommit,

    /// <summary>The body did not name a state Charter understands.</summary>
    Invalid,

    /// <summary>
    /// The URL is not one Charter will store, fetch, or put in front of a requester
    /// (<see cref="Charter.Deployments.PreviewUrlPolicy"/>).
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Invalid"/> because the two have different consequences. An
    /// unrecognised state is a caller that did not say anything; a refused URL is a preview that will
    /// not be arriving, and the requester's card has to say so rather than spin.
    /// </remarks>
    UnsafeUrl,
}

/// <summary>The result of one deployment report.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Detail">A one-line explanation, safe to return to the caller.</param>
public sealed record DeploymentBindingResult(DeploymentBindingOutcome Outcome, string Detail);

/// <summary>
/// Binds a preview environment to a pull request (section 18).
/// </summary>
/// <remarks>
/// <para>
/// Provider-agnostic by design. The key is the head commit SHA rather than any Charter identifier,
/// because that is the one value every hosting provider already knows about a preview build without
/// being told — which is what makes a Render, Fly or Coolify self-hoster first-class rather than a
/// port of the Railway path.
/// </para>
/// <para>
/// The commit SHA is <em>not</em> the authorisation, and used to be. It is authored by the execution
/// plane and legitimately known to anybody who can see the pull request, so admission is a per-instance
/// secret checked at the endpoint (<see cref="Charter.Deployments.DeploymentWebhookAuthentication"/>).
/// What the SHA still does here is bind: it says which change request a report is about, and a report
/// naming a commit no change request carries is refused.
/// </para>
/// <para>
/// <strong>This is the one gate every preview URL passes.</strong> Both ingestion paths of section 18
/// reach it, so validating here — before a URL is written rather than at each place one is read —
/// means a third consumer added later cannot be the one that forgot. See
/// <see cref="Charter.Deployments.PreviewUrlPolicy"/> for what is refused and why.
/// </para>
/// </remarks>
public sealed class DeploymentBinder
{
    private readonly CharterDbContext _database;
    private readonly TimeProvider _clock;
    private readonly ILogger<DeploymentBinder> _logger;
    private readonly PreviewUrlPolicy _urls;

    public DeploymentBinder(
        CharterDbContext database,
        TimeProvider clock,
        ILogger<DeploymentBinder> logger,
        PreviewUrlPolicy? urls = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _clock = clock;
        _logger = logger;
        _urls = urls ?? PreviewUrlPolicy.Default;
    }

    /// <summary>Records a deployment report against the pull request with this head SHA.</summary>
    public async Task<DeploymentBindingResult> ReportAsync(
        string headSha,
        DeploymentWebhookRequest report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (string.IsNullOrWhiteSpace(headSha))
        {
            return new DeploymentBindingResult(DeploymentBindingOutcome.Invalid, "no commit SHA was named");
        }

        if (!TryParseState(report.State, out var state))
        {
            return new DeploymentBindingResult(
                DeploymentBindingOutcome.Invalid,
                "state must be one of pending, building, ready, failed, cancelled or expired");
        }

        if (state == DeploymentState.Ready && string.IsNullOrWhiteSpace(report.Url))
        {
            return new DeploymentBindingResult(
                DeploymentBindingOutcome.Invalid,
                "a ready deployment must carry a url");
        }

        var provider = string.IsNullOrWhiteSpace(report.Provider) ? "unknown" : report.Provider.Trim();
        var sha = headSha.Trim();

        var changeRequest = await _database.ChangeRequests
            .Where(candidate => candidate.HeadSha == sha)
            .OrderByDescending(candidate => candidate.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (changeRequest is null)
        {
            _logger.LogWarning(
                "A deployment report from {Provider} named commit {HeadSha}, which no pull request carries",
                provider,
                sha);

            return new DeploymentBindingResult(
                DeploymentBindingOutcome.UnknownCommit,
                "no pull request in this instance has that head commit");
        }

        var now = _clock.GetUtcNow();

        // The gate. A report with no URL has nothing to check — a platform saying "building" names no
        // link — but every URL that arrives, in any state, is checked before it is written.
        var url = report.Url;
        var refusal = (string?)null;

        if (!string.IsNullOrWhiteSpace(url))
        {
            var verdict = await _urls.ValidateAsync(url, cancellationToken);

            if (!verdict.Allowed)
            {
                // Refuse, do not sanitise (section 16.3). The URL is dropped and the deployment is
                // recorded as failed, so the requester's card settles on section 27.7's designed
                // failure rather than waiting forever for a preview that is never coming.
                _logger.LogWarning(
                    "Refused the preview url '{Url}' reported by {Provider} for pull request {Number}: {Reason}",
                    url,
                    provider,
                    changeRequest.Number,
                    verdict.Reason);

                refusal = verdict.Reason;
                url = null;
                state = DeploymentState.Failed;
            }
        }

        // Deployment.Report lower-cases the provider, so match on the same spelling rather than
        // asking the database to fold case for us.
        var providerKey = provider.ToLowerInvariant();

        var existing = await _database.Deployments
            .Where(candidate => candidate.ChangeRequestId == changeRequest.Id && candidate.Provider == providerKey)
            .OrderByDescending(candidate => candidate.ReportedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is null)
        {
            _database.Deployments.Add(Deployment.Report(changeRequest.Id, provider, state, url, now));
        }
        else
        {
            existing.Update(state, url, now);
        }

        await _database.SaveChangesAsync(cancellationToken);

        if (refusal is not null)
        {
            return new DeploymentBindingResult(DeploymentBindingOutcome.UnsafeUrl, refusal);
        }

        _logger.LogInformation(
            "Preview for pull request {Number} is {State} (provider {Provider})",
            changeRequest.Number,
            state,
            provider);

        return new DeploymentBindingResult(DeploymentBindingOutcome.Recorded, "recorded");
    }

    /// <summary>
    /// Maps provider vocabulary onto <see cref="DeploymentState"/>.
    /// </summary>
    /// <remarks>
    /// Section 18 keeps Charter's states the common denominator rather than any one platform's, so
    /// the synonyms live here — one small translation table beats a provider-shaped enum.
    /// </remarks>
    public static bool TryParseState(string? value, out DeploymentState state)
    {
        state = DeploymentState.Pending;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "pending":
            case "queued":
            case "waiting":
                state = DeploymentState.Pending;
                return true;

            case "building":
            case "deploying":
            case "in_progress":
            case "initializing":
                state = DeploymentState.Building;
                return true;

            case "ready":
            case "success":
            case "succeeded":
            case "active":
            case "deployed":
                state = DeploymentState.Ready;
                return true;

            case "failed":
            case "failure":
            case "error":
            case "crashed":
                state = DeploymentState.Failed;
                return true;

            case "cancelled":
            case "canceled":
            case "skipped":
                state = DeploymentState.Cancelled;
                return true;

            case "expired":
            case "removed":
            case "destroyed":
                state = DeploymentState.Expired;
                return true;

            default:
                return false;
        }
    }
}
