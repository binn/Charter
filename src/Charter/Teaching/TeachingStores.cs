using System.Collections.Concurrent;
using Charter.Domain;

namespace Charter.Teaching;

/// <summary>
/// Where generated walkthroughs live, so opening the tab twice costs once.
/// </summary>
/// <remarks>
/// This interface is what makes section 13's laziness real rather than aspirational. Teaching is
/// generated <em>only</em> when the reader opens the tab, and the second open must not spend
/// anything at all — so the generator asks here first, and only calls a model when the answer is no.
/// </remarks>
public interface IWalkthroughStore
{
    /// <summary>The walkthrough already generated for this session at this calibration, if any.</summary>
    Task<Walkthrough?> FindAsync(
        Guid sessionId,
        TeachingLevel level,
        CancellationToken cancellationToken = default);

    /// <summary>Stores a freshly generated walkthrough.</summary>
    Task SaveAsync(Walkthrough walkthrough, CancellationToken cancellationToken = default);
}

/// <summary>Whether one more <em>explain this</em> is allowed, and what to say if not.</summary>
/// <param name="Allowed">Whether the call may proceed.</param>
/// <param name="Used">How many the person has spent in the current window, including this one.</param>
/// <param name="Limit">The cap.</param>
/// <param name="ResetsAt">When the window rolls over.</param>
public sealed record ExplainThisAllowance(bool Allowed, int Used, int Limit, DateTimeOffset ResetsAt);

/// <summary>
/// The per-user cap on <em>explain this</em> (section 13).
/// </summary>
/// <remarks>
/// The other two surfaces are naturally bounded — one milestone list, one walkthrough per session.
/// This one is a button next to every event, every file and every hunk, and a curious reader can
/// click it a hundred times in an afternoon. Section 13 singles it out as the surface that needs a
/// cap, and this is it.
/// </remarks>
public interface IExplainThisQuota
{
    /// <summary>Consumes one unit of the reader's daily allowance.</summary>
    Task<ExplainThisAllowance> TryConsumeAsync(
        Guid userId,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>A process-local walkthrough cache, replaced by the data layer's implementation.</summary>
public sealed class InMemoryWalkthroughStore : IWalkthroughStore
{
    private readonly ConcurrentDictionary<(Guid SessionId, TeachingLevel Level), Walkthrough> _walkthroughs = new();

    /// <inheritdoc />
    public Task<Walkthrough?> FindAsync(
        Guid sessionId,
        TeachingLevel level,
        CancellationToken cancellationToken = default)
        => Task.FromResult(_walkthroughs.GetValueOrDefault((sessionId, level)));

    /// <inheritdoc />
    public Task SaveAsync(Walkthrough walkthrough, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(walkthrough);

        _walkthroughs[(walkthrough.SessionId, walkthrough.Level)] = walkthrough;
        return Task.CompletedTask;
    }
}

/// <summary>A process-local daily counter, replaced by the data layer's implementation.</summary>
public sealed class InMemoryExplainThisQuota : IExplainThisQuota
{
    private readonly ConcurrentDictionary<(Guid UserId, DateOnly Day), int> _counts = new();
    private readonly TimeProvider _clock;

    /// <summary>Creates the quota.</summary>
    public InMemoryExplainThisQuota(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <inheritdoc />
    public Task<ExplainThisAllowance> TryConsumeAsync(
        Guid userId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        var resets = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        if (limit <= 0)
        {
            return Task.FromResult(new ExplainThisAllowance(false, 0, limit, resets));
        }

        var used = _counts.AddOrUpdate((userId, day), 1, static (_, current) => current + 1);

        return Task.FromResult(
            new ExplainThisAllowance(used <= limit, Math.Min(used, limit), limit, resets));
    }
}
