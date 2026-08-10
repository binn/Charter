using Charter.Domain;
using Charter.Teaching;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace Charter.Data.Teaching;

/// <summary>
/// The per-user concept ledger, in Postgres (sections 5, 13).
/// </summary>
/// <remarks>
/// <para>
/// Replaces <c>InMemoryConceptLedgerStore</c>. The in-memory one is not wrong, it is temporary: a
/// ledger that empties on restart makes section 13's graduation behaviour look broken rather than
/// absent, because the requester who was told what a migration is fifteen sessions ago gets told
/// again the first time the container moves.
/// </para>
/// <para>
/// Reads come back ordered by <c>LastReferencedAt</c> descending, which is the order the capped
/// injection window is taken in and the order the index is built in. Writes are a single
/// <c>INSERT … ON CONFLICT DO UPDATE</c> over an unnested array, so recording a pass of concepts is
/// one round trip and two sessions teaching the same concept at the same moment increment the same
/// row instead of colliding on the unique index.
/// </para>
/// </remarks>
public sealed class EfConceptLedgerStore : IConceptLedgerStore
{
    /// <summary>
    /// Upsert: reference what is already known, record what is not, in one statement.
    /// </summary>
    /// <remarks>
    /// <c>first_explained_at</c> is only ever written by the insert arm, so it keeps meaning "the
    /// first time this person was told", which is what an operator reading the row expects.
    /// </remarks>
    internal const string RecordSql = """
        INSERT INTO concept_ledger (id, user_id, concept, first_explained_at, last_referenced_at, times_referenced)
        SELECT incoming.id, @user_id, incoming.concept, @now, @now, 1
        FROM unnest(@ids, @concepts) AS incoming(id, concept)
        ON CONFLICT (user_id, concept) DO UPDATE
        SET times_referenced = concept_ledger.times_referenced + 1,
            last_referenced_at = EXCLUDED.last_referenced_at
        """;

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;

    /// <summary>Creates the store.</summary>
    public EfConceptLedgerStore(IServiceScopeFactory scopes, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(clock);

        _scopes = scopes;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConceptLedger>> GetAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharterDbContext>();

        return await db.ConceptLedger
            .AsNoTracking()
            .Where(concept => concept.UserId == userId)
            // Section 13 caps the injection at the most-recent few dozen, so most recent first is
            // the useful order and the one ix_concept_ledger_user_id_last_referenced_at serves.
            .OrderByDescending(concept => concept.LastReferencedAt)
            .ThenByDescending(concept => concept.TimesReferenced)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RecordAsync(
        Guid userId,
        IEnumerable<string> concepts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(concepts);

        // Normalised and de-duplicated before the statement: Postgres refuses an ON CONFLICT update
        // that would touch the same row twice in one command, and "migration" twice in one pass is
        // one concept explained once, not two references.
        var normalized = concepts
            .Where(static concept => !string.IsNullOrWhiteSpace(concept))
            .Select(ConceptLedgerSnapshot.Normalise)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (normalized.Length == 0)
        {
            return;
        }

        var ids = new Guid[normalized.Length];
        for (var index = 0; index < ids.Length; index++)
        {
            ids[index] = Guid.CreateVersion7();
        }

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharterDbContext>();

        await using var command = await RawSqlCommand
            .CreateAsync(db, RecordSql, cancellationToken)
            .ConfigureAwait(false);

        command.AddParameter("user_id", NpgsqlDbType.Uuid, userId);
        command.AddParameter("now", NpgsqlDbType.TimestampTz, _clock.GetUtcNow());
        command.AddParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids);
        command.AddParameter("concepts", NpgsqlDbType.Array | NpgsqlDbType.Text, normalized);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ResetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // Section 13, in as many words: let them reset the ledger, because people forget. A reset
        // deletes rather than flags - a "forgotten" concept that still occupies the capped injection
        // window would be the same bug with extra columns.
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharterDbContext>();

        await db.ConceptLedger
            .Where(concept => concept.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
