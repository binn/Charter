using Charter.Domain;
using Charter.Teaching;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Charter.Data.Teaching;

/// <summary>
/// The generated-walkthrough cache, in Postgres (sections 5, 13).
/// </summary>
/// <remarks>
/// <para>
/// Section 13 makes teaching lazy — generated only when the reader opens the tab — and the second
/// open must cost nothing at all. So the read path is what this class is for: one indexed lookup on
/// <c>ux_walkthroughs_session_id_level</c>, untracked, projecting the row and nothing around it. A
/// miss is the only thing that reaches a model.
/// </para>
/// <para>
/// In memory that cache was per container, which meant every restart re-billed the teaching budget
/// line for narratives that had already been written — the one budget line section 13 warns is
/// first to be cut for having no visible output.
/// </para>
/// </remarks>
public sealed class EfWalkthroughStore : IWalkthroughStore
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<EfWalkthroughStore> _logger;

    /// <summary>Creates the store.</summary>
    public EfWalkthroughStore(IServiceScopeFactory scopes, ILogger<EfWalkthroughStore> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Walkthrough?> FindAsync(
        Guid sessionId,
        TeachingLevel level,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharterDbContext>();

        return await db.Walkthroughs
            .AsNoTracking()
            .FirstOrDefaultAsync(
                walkthrough => walkthrough.SessionId == sessionId && walkthrough.Level == level,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveAsync(Walkthrough walkthrough, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(walkthrough);

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharterDbContext>();

        // A regeneration at the same level replaces the rendering rather than failing on the unique
        // index: section 13's always-visible "more detail" / "less detail" writes at another level,
        // but a plain regenerate writes over this one, and the reader must get the new text.
        await db.Walkthroughs
            .Where(existing => existing.SessionId == walkthrough.SessionId && existing.Level == walkthrough.Level)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        db.Walkthroughs.Add(walkthrough);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            // Two readers opened the tab at the same instant and both generated. The other one won
            // and its rendering is every bit as good as this one, so the cache is already warm and
            // there is nothing to repair - but a silent catch would hide a real mapping fault.
            _logger.LogWarning(
                ex,
                "A walkthrough for session {SessionId} at {Level} was stored by another writer first; " +
                "keeping theirs.",
                walkthrough.SessionId,
                walkthrough.Level);
        }
    }
}
