using Charter.Data;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Charter.Deployments;

/// <summary>A preview and how long it has left.</summary>
/// <param name="ArtifactId">The verification artifact.</param>
/// <param name="SessionId">The session it belongs to.</param>
/// <param name="Url">Where the preview is, while it still is.</param>
/// <param name="ExpiresAt">When it stops working.</param>
/// <param name="State">What section 27.7's card renders right now, expiry included.</param>
public sealed record ExpiringPreview(
    Guid ArtifactId,
    Guid SessionId,
    string? Url,
    DateTimeOffset ExpiresAt,
    VerificationArtifactState State);

/// <summary>
/// Expiry as a designed state rather than a 404 (section 27.7).
/// </summary>
/// <remarks>
/// <para>
/// Section 27.7 is blunt about why this exists: expired previews are the number one source of
/// confusion in tools like this, because somebody opens a link from a three-day-old notification,
/// gets a dead host, and reports the product as broken. Three things follow, and all three are here.
/// Expiry is <em>queryable</em>, so a notification, a digest or a dashboard can ask what is about to
/// lapse. Expiry is <em>swept</em>, so a lapsed preview reads as cleaned up rather than as broken.
/// And the environment behind it is <em>torn down</em>, so section 27.5's warning about storage and
/// hosting costs running away does not come true.
/// </para>
/// <para>
/// The rendered state comes from <see cref="VerificationArtifact.DisplayStateAt"/>, not from this
/// sweep. A card must show <c>expiring</c> the moment there is under an hour left and <c>expired</c>
/// the moment the clock passes, whether or not a background loop has run — the sweep settles the
/// stored row and stops paying for the environment, it does not decide what a requester sees.
/// </para>
/// </remarks>
public sealed class PreviewExpiry
{
    private readonly CharterDbContext _database;
    private readonly DeploymentProviderRegistry _providers;
    private readonly TimeProvider _clock;
    private readonly ILogger<PreviewExpiry> _logger;

    public PreviewExpiry(
        CharterDbContext database,
        DeploymentProviderRegistry providers,
        TimeProvider clock,
        ILogger<PreviewExpiry> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _providers = providers;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Previews that lapse within <paramref name="window"/>, soonest first.</summary>
    /// <remarks>
    /// The filtered index on <c>expires_at</c> serves this directly. A window of one hour returns
    /// exactly the set section 27.7 renders with an amber countdown.
    /// </remarks>
    public async Task<IReadOnlyList<ExpiringPreview>> ExpiringWithinAsync(
        TimeSpan window,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        var limit = now + window;

        var rows = await _database.VerificationArtifacts
            .AsNoTracking()
            .Where(row => row.State == VerificationArtifactState.Ready
                          && row.ExpiresAt != null
                          && row.ExpiresAt > now
                          && row.ExpiresAt <= limit)
            .OrderBy(row => row.ExpiresAt)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => Describe(row, now))];
    }

    /// <summary>Previews whose clock has already passed but whose row still says ready.</summary>
    public async Task<IReadOnlyList<ExpiringPreview>> LapsedAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        var rows = await _database.VerificationArtifacts
            .AsNoTracking()
            .Where(row => row.State == VerificationArtifactState.Ready
                          && row.ExpiresAt != null
                          && row.ExpiresAt <= now)
            .OrderBy(row => row.ExpiresAt)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => Describe(row, now))];
    }

    /// <summary>
    /// Settles every lapsed preview: marks the artifact expired and asks the provider to take the
    /// environment away.
    /// </summary>
    /// <returns>How many artifacts were marked expired.</returns>
    public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        var lapsed = await _database.VerificationArtifacts
            .Where(row => row.State == VerificationArtifactState.Ready
                          && row.ExpiresAt != null
                          && row.ExpiresAt <= now)
            .ToListAsync(cancellationToken);

        if (lapsed.Count == 0)
        {
            return 0;
        }

        foreach (var artifact in lapsed)
        {
            artifact.MarkExpired();
        }

        await _database.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Marked {Count} preview(s) expired", lapsed.Count);

        foreach (var artifact in lapsed)
        {
            await TearDownAsync(artifact.SessionId, cancellationToken);
        }

        return lapsed.Count;
    }

    /// <summary>
    /// Asks the configured provider to destroy the environment behind a session's preview.
    /// </summary>
    /// <remarks>
    /// Idempotent, and quiet when there is no provider or nothing to tear down: an instance binding
    /// previews through the generic webhook has no way to destroy anything, and the platform's own
    /// retention is what reclaims it. Charter says so in the log rather than pretending it acted.
    /// </remarks>
    public async Task<DeploymentTeardownResult> TearDownAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (_providers.Configured is not { Capabilities.Teardown: true } provider)
        {
            return DeploymentTeardownResult.NothingToDo("no configured deployment provider can tear environments down");
        }

        var context = await (
            from changeRequest in _database.ChangeRequests
            join session in _database.Sessions on changeRequest.SessionId equals session.Id
            join spec in _database.Specs on session.SpecId equals spec.Id
            join request in _database.Requests on spec.RequestId equals request.Id
            join repo in _database.Repos on request.RepoId equals repo.Id
            where changeRequest.SessionId == sessionId
            orderby changeRequest.UpdatedAt descending
            select new PreviewContext(changeRequest, session.Id, repo.FullName))
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

        if (context is null)
        {
            return DeploymentTeardownResult.NothingToDo("that session has no change request to tear a preview down for");
        }

        var result = await provider.TeardownAsync(context.Target, cancellationToken);

        if (!result.TornDown)
        {
            _logger.LogDebug(
                "Nothing to tear down for change request {Number}: {Explanation}",
                context.ChangeRequest.Number,
                result.Explanation ?? "no reason given");
        }

        return result;
    }

    private static ExpiringPreview Describe(VerificationArtifact artifact, DateTimeOffset now)
        => new(
            artifact.Id,
            artifact.SessionId,
            artifact.Url,
            artifact.ExpiresAt ?? now,
            artifact.DisplayStateAt(now));
}
