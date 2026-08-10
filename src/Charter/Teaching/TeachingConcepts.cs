using System.Collections.Concurrent;
using System.Text;
using Charter.Domain;

namespace Charter.Teaching;

/// <summary>
/// The capped window of the per-user concept ledger that goes into a prompt.
/// </summary>
/// <param name="Concepts">Most recently referenced first, already capped.</param>
/// <param name="TotalKnown">How many concepts the ledger holds in total, capped or not.</param>
public sealed record ConceptLedgerSnapshot(IReadOnlyList<string> Concepts, int TotalKnown)
{
    /// <summary>A reader Charter has never explained anything to.</summary>
    public static ConceptLedgerSnapshot Empty { get; } = new([], 0);

    /// <summary>Whether this concept has been explained to this person before.</summary>
    public bool Knows(string concept)
        => !string.IsNullOrWhiteSpace(concept)
        && Concepts.Contains(Normalise(concept), StringComparer.Ordinal);

    /// <summary>
    /// The injection, as section 13 words it: <em>already knows: X, Y, Z</em>.
    /// </summary>
    /// <remarks>
    /// This one paragraph is the mechanism that lets an <c>explain_everything</c> requester graduate
    /// over fifteen sessions without ever opening a settings page. Their calibration never changes;
    /// the list of things that no longer need defining just gets longer.
    /// </remarks>
    public string ToPromptText()
    {
        if (Concepts.Count == 0)
        {
            return "This is the first thing Charter has explained to this person. Nothing can be "
                + "assumed as already known.";
        }

        var builder = new StringBuilder("Already knows: ")
            .AppendJoin(", ", Concepts)
            .Append('.');

        builder
            .Append(' ')
            .Append(
                "Reference these by name and build on them. Do not re-teach or re-define any of "
                + "them; a reader who is told twice what a migration is stops reading.");

        if (TotalKnown > Concepts.Count)
        {
            builder
                .Append(' ')
                .Append("(This is the ")
                .Append(Concepts.Count)
                .Append(" most recent of ")
                .Append(TotalKnown)
                .Append(
                    "; anything older is not listed, so re-introducing an older concept briefly is "
                    + "acceptable.)");
        }

        return builder.ToString();
    }

    /// <summary>Projects ledger rows into a capped, most-recent-first snapshot.</summary>
    public static ConceptLedgerSnapshot From(IEnumerable<ConceptLedger> entries, int limit)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        var all = entries.Where(static entry => entry is not null).ToList();

        return new ConceptLedgerSnapshot(
            [
                .. all
                    .OrderByDescending(static entry => entry.LastReferencedAt)
                    .ThenByDescending(static entry => entry.TimesReferenced)
                    .Take(limit)
                    .Select(static entry => entry.Concept),
            ],
            all.Count);
    }

    internal static string Normalise(string concept) => concept.Trim().ToLowerInvariant();
}

/// <summary>
/// Where the per-user concept ledger lives (section 5, <c>ConceptLedger</c>).
/// </summary>
/// <remarks>
/// The teaching package reads and appends; persistence belongs to the data layer. Reset is a
/// first-class operation rather than an admin script, because section 13 asks for it in as many
/// words: <em>let them reset the ledger (people forget)</em>.
/// </remarks>
public interface IConceptLedgerStore
{
    /// <summary>Every concept already explained to this person.</summary>
    Task<IReadOnlyList<ConceptLedger>> GetAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records concepts explained in this pass. Concepts already present are referenced rather than
    /// duplicated, which is what keeps <see cref="ConceptLedger.TimesReferenced"/> meaningful.
    /// </summary>
    Task RecordAsync(
        Guid userId,
        IEnumerable<string> concepts,
        CancellationToken cancellationToken = default);

    /// <summary>Clears the ledger for one person. Section 13: people forget.</summary>
    Task ResetAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// A process-local concept ledger, so teaching works on a freshly wired instance.
/// </summary>
/// <remarks>
/// Registered with <c>TryAdd</c>, so the data layer's Postgres-backed implementation replaces it the
/// moment one is registered. It is deliberately not a no-op: a ledger that silently forgot
/// everything would make the graduation behaviour of section 13 look broken rather than absent.
/// </remarks>
public sealed class InMemoryConceptLedgerStore : IConceptLedgerStore
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, ConceptLedger>> _ledgers = new();
    private readonly TimeProvider _clock;

    /// <summary>Creates the store.</summary>
    public InMemoryConceptLedgerStore(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ConceptLedger>> GetAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ConceptLedger>>(
            _ledgers.TryGetValue(userId, out var ledger) ? [.. ledger.Values] : []);

    /// <inheritdoc />
    public Task RecordAsync(
        Guid userId,
        IEnumerable<string> concepts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(concepts);

        var ledger = _ledgers.GetOrAdd(userId, static _ => new ConcurrentDictionary<string, ConceptLedger>(StringComparer.Ordinal));
        var now = _clock.GetUtcNow();

        foreach (var raw in concepts)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var concept = ConceptLedgerSnapshot.Normalise(raw);
            if (ledger.TryGetValue(concept, out var existing))
            {
                existing.Reference(now);
                continue;
            }

            ledger[concept] = ConceptLedger.Record(userId, concept, now);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ResetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        _ledgers.TryRemove(userId, out _);
        return Task.CompletedTask;
    }
}
