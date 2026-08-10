namespace Charter.Agent.Transport;

/// <summary>
/// Exponential backoff with full jitter for the outbound connection (section 33.1).
/// </summary>
/// <remarks>
/// A flaky home connection must not need babysitting, and a control plane restart must not be met by
/// every agent in an organisation reconnecting on the same millisecond. Full jitter — a uniform draw
/// from <c>[0, ceiling]</c> rather than a fixed delay — is what breaks that synchronisation, and it
/// also means an agent whose link drops for a second is usually back almost immediately.
/// <para>
/// The delay ceiling doubles per consecutive failure up to <see cref="Maximum"/>. A successful
/// connection resets it, so a link that flaps once an hour never accumulates a long delay.
/// </para>
/// </remarks>
public sealed class ReconnectPolicy(
    TimeSpan? initial = null,
    TimeSpan? maximum = null,
    double multiplier = 2.0,
    Func<double>? jitter = null)
{
    private readonly Func<double> _jitter = jitter ?? Random.Shared.NextDouble;

    public TimeSpan Initial { get; } = initial ?? TimeSpan.FromSeconds(1);

    public TimeSpan Maximum { get; } = maximum ?? TimeSpan.FromMinutes(2);

    public double Multiplier { get; } = multiplier;

    /// <summary>Consecutive failed attempts since the last successful connection.</summary>
    public int Attempt { get; private set; }

    /// <summary>Call after a connection is established and the handshake has completed.</summary>
    public void Reset() => Attempt = 0;

    /// <summary>The ceiling for the next delay, before jitter. Deterministic and testable.</summary>
    public TimeSpan Ceiling(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);

        var scaled = Initial.TotalMilliseconds * Math.Pow(Multiplier, attempt);
        return scaled >= Maximum.TotalMilliseconds || double.IsInfinity(scaled)
            ? Maximum
            : TimeSpan.FromMilliseconds(scaled);
    }

    /// <summary>Records a failure and returns how long to wait before dialling out again.</summary>
    public TimeSpan NextDelay()
    {
        var ceiling = Ceiling(Attempt);
        Attempt = Attempt == int.MaxValue ? Attempt : Attempt + 1;

        var draw = Math.Clamp(_jitter(), 0.0, 1.0);
        return TimeSpan.FromMilliseconds(ceiling.TotalMilliseconds * draw);
    }
}
