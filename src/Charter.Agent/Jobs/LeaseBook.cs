namespace Charter.Agent.Jobs;

/// <summary>A job the agent holds, and when its claim lapses.</summary>
public sealed record Lease(string JobId, DateTimeOffset ExpiresAt);

/// <summary>
/// The leases this agent holds (section 33.4).
/// </summary>
/// <remarks>
/// A claim carries a TTL renewed by heartbeat. The control plane's half of that is returning an
/// unrenewed job to the queue; this half is the agent noticing it has lost a lease and stopping the
/// work. Both are needed — if only the plane re-queued, a partitioned agent would keep running a job
/// another agent had already picked up, and two runners would push to the same branch.
/// </remarks>
public sealed class LeaseBook
{
    private readonly Dictionary<string, DateTimeOffset> _leases = new(StringComparer.Ordinal);

    public int Count => _leases.Count;

    public IReadOnlyList<string> HeldJobIds => [.. _leases.Keys.Order(StringComparer.Ordinal)];

    public bool Holds(string jobId) => _leases.ContainsKey(jobId);

    public DateTimeOffset? EarliestExpiry =>
        _leases.Count == 0 ? null : _leases.Values.Min();

    public void Add(string jobId, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        _leases[jobId] = expiresAt;
    }

    /// <summary>Extends a lease. False when the agent does not hold this job — a stale renewal.</summary>
    public bool Renew(string jobId, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        if (!_leases.ContainsKey(jobId))
        {
            return false;
        }

        _leases[jobId] = expiresAt;
        return true;
    }

    public bool Release(string jobId) => _leases.Remove(jobId);

    /// <summary>Removes and returns every lapsed lease. The work behind them must stop.</summary>
    public IReadOnlyList<string> TakeExpired(DateTimeOffset now)
    {
        var expired = _leases
            .Where(pair => pair.Value <= now)
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        foreach (var jobId in expired)
        {
            _leases.Remove(jobId);
        }

        return expired;
    }

    public IReadOnlyList<string> TakeAll()
    {
        var all = HeldJobIds;
        _leases.Clear();
        return all;
    }
}
