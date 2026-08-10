using Charter.Data;
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
/// The commit SHA <em>is</em> the authorisation. A report for a SHA that no pull request in this
/// instance carries is refused, so the endpoint cannot be used to enumerate anything or to attach a
/// URL to work it does not name. An operator who wants more than that can put the deployment webhook
/// behind their own gateway; Charter does not invent a second secret for it, because a shared secret
/// pasted into four hosting providers is not obviously better than an unguessable 40-character key
/// that already exists.
/// </para>
/// </remarks>
public sealed class DeploymentBinder
{
    private readonly CharterDbContext _database;
    private readonly TimeProvider _clock;
    private readonly ILogger<DeploymentBinder> _logger;

    public DeploymentBinder(CharterDbContext database, TimeProvider clock, ILogger<DeploymentBinder> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _clock = clock;
        _logger = logger;
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

        // Deployment.Report lower-cases the provider, so match on the same spelling rather than
        // asking the database to fold case for us.
        var providerKey = provider.ToLowerInvariant();

        var existing = await _database.Deployments
            .Where(candidate => candidate.ChangeRequestId == changeRequest.Id && candidate.Provider == providerKey)
            .OrderByDescending(candidate => candidate.ReportedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is null)
        {
            _database.Deployments.Add(Deployment.Report(changeRequest.Id, provider, state, report.Url, now));
        }
        else
        {
            existing.Update(state, report.Url, now);
        }

        await _database.SaveChangesAsync(cancellationToken);

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
