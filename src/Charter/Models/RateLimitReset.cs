using System.Globalization;
using System.Net.Http.Headers;

namespace Charter.Models;

/// <summary>
/// When a rate-limited provider says its limit resets. Section 20b.4 records this as
/// <c>exhausted_until</c> so an exhausted session can queue as <em>waiting for capacity</em> rather
/// than fail.
/// </summary>
/// <param name="ResetAt">The instant the limit resets, or <see langword="null"/> if unknown.</param>
/// <param name="RetryAfter">The delay the provider asked for, where it gave one.</param>
public readonly record struct RateLimitReset(DateTimeOffset? ResetAt, TimeSpan? RetryAfter)
{
    /// <summary>The provider gave no usable reset information.</summary>
    public static RateLimitReset Unknown => new(null, null);

    /// <summary>Whether the provider told us anything at all.</summary>
    public bool IsKnown => ResetAt.HasValue;
}

/// <summary>
/// Reads the reset instant out of a <c>429</c> response's headers, across the several conventions the
/// supported providers use.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><c>retry-after</c> - RFC 9110; either delay-seconds or an HTTP date.</description></item>
/// <item><description><c>anthropic-ratelimit-*-reset</c> - RFC 3339 timestamps.</description></item>
/// <item><description><c>x-ratelimit-reset-requests</c> / <c>-tokens</c> - OpenAI's duration form, e.g. <c>6m0s</c>, <c>120ms</c>.</description></item>
/// <item><description><c>x-ratelimit-reset</c> - OpenRouter's Unix milliseconds.</description></item>
/// </list>
/// The earliest usable reset wins: waiting the shortest advertised interval is the least the caller
/// can do, and re-hitting the limit merely re-exhausts the grant rather than corrupting anything.
/// </remarks>
public static class RateLimitResetParser
{
    private static readonly string[] AbsoluteResetHeaders =
    [
        "anthropic-ratelimit-requests-reset",
        "anthropic-ratelimit-tokens-reset",
        "anthropic-ratelimit-input-tokens-reset",
        "anthropic-ratelimit-output-tokens-reset",
    ];

    private static readonly string[] DurationResetHeaders =
    [
        "x-ratelimit-reset-requests",
        "x-ratelimit-reset-tokens",
    ];

    /// <summary>Parses the reset from a response's headers.</summary>
    /// <param name="headers">The response headers.</param>
    /// <param name="now">The current instant, for turning delays into absolute times.</param>
    public static RateLimitReset Parse(HttpResponseHeaders? headers, DateTimeOffset now)
    {
        if (headers is null)
        {
            return RateLimitReset.Unknown;
        }

        return Parse(Flatten(headers), now);
    }

    /// <summary>Parses the reset from a flattened header collection.</summary>
    /// <param name="headers">Header name to first value, matched case-insensitively.</param>
    /// <param name="now">The current instant, for turning delays into absolute times.</param>
    public static RateLimitReset Parse(IReadOnlyDictionary<string, string> headers, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            lookup[name] = value;
        }

        DateTimeOffset? earliest = null;
        TimeSpan? retryAfter = null;

        if (TryGet(lookup, "retry-after", out var retryAfterRaw))
        {
            if (long.TryParse(retryAfterRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                && seconds >= 0)
            {
                retryAfter = TimeSpan.FromSeconds(seconds);
                earliest = Earliest(earliest, now + retryAfter.Value);
            }
            else if (DateTimeOffset.TryParse(
                retryAfterRaw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var absolute))
            {
                retryAfter = absolute > now ? absolute - now : TimeSpan.Zero;
                earliest = Earliest(earliest, absolute);
            }
        }

        foreach (var name in AbsoluteResetHeaders)
        {
            if (TryGet(lookup, name, out var raw)
                && DateTimeOffset.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var reset))
            {
                earliest = Earliest(earliest, reset);
            }
        }

        foreach (var name in DurationResetHeaders)
        {
            if (TryGet(lookup, name, out var raw) && TryParseGoDuration(raw, out var duration))
            {
                earliest = Earliest(earliest, now + duration);
                retryAfter ??= duration;
            }
        }

        // OpenRouter reports the reset as Unix epoch milliseconds.
        if (TryGet(lookup, "x-ratelimit-reset", out var epochRaw)
            && long.TryParse(epochRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch)
            && epoch > 0)
        {
            var reset = epoch > 99_999_999_999L
                ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                : DateTimeOffset.FromUnixTimeSeconds(epoch);
            earliest = Earliest(earliest, reset);
        }

        if (earliest is null)
        {
            return RateLimitReset.Unknown;
        }

        retryAfter ??= earliest.Value > now ? earliest.Value - now : TimeSpan.Zero;
        return new RateLimitReset(earliest, retryAfter);
    }

    /// <summary>Flattens response headers to first-value-wins, case-insensitive.</summary>
    public static IReadOnlyDictionary<string, string> Flatten(HttpResponseHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in headers)
        {
            var value = values.FirstOrDefault();
            if (value is not null)
            {
                result[name] = value;
            }
        }

        return result;
    }

    private static bool TryGet(Dictionary<string, string> headers, string name, out string value)
    {
        if (headers.TryGetValue(name, out var candidate) && !string.IsNullOrWhiteSpace(candidate))
        {
            value = candidate;
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>
    /// Parses OpenAI's duration form - a concatenation of value/unit pairs such as <c>1h30m0s</c>,
    /// <c>6m0s</c> or <c>120ms</c>.
    /// </summary>
    internal static bool TryParseGoDuration(string? value, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var span = value.AsSpan().Trim();
        var total = TimeSpan.Zero;
        var index = 0;
        var matched = false;

        while (index < span.Length)
        {
            var numberStart = index;
            while (index < span.Length && (char.IsAsciiDigit(span[index]) || span[index] == '.'))
            {
                index++;
            }

            if (index == numberStart)
            {
                return false;
            }

            if (!double.TryParse(
                span[numberStart..index],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var magnitude))
            {
                return false;
            }

            var unitStart = index;
            while (index < span.Length && char.IsAsciiLetter(span[index]))
            {
                index++;
            }

            var unit = span[unitStart..index];
            if (unit.IsEmpty)
            {
                return false;
            }

            TimeSpan component;
            if (unit.Equals("ms", StringComparison.OrdinalIgnoreCase))
            {
                component = TimeSpan.FromMilliseconds(magnitude);
            }
            else if (unit.Equals("s", StringComparison.OrdinalIgnoreCase))
            {
                component = TimeSpan.FromSeconds(magnitude);
            }
            else if (unit.Equals("m", StringComparison.OrdinalIgnoreCase))
            {
                component = TimeSpan.FromMinutes(magnitude);
            }
            else if (unit.Equals("h", StringComparison.OrdinalIgnoreCase))
            {
                component = TimeSpan.FromHours(magnitude);
            }
            else if (unit.Equals("d", StringComparison.OrdinalIgnoreCase))
            {
                component = TimeSpan.FromDays(magnitude);
            }
            else
            {
                return false;
            }

            total += component;
            matched = true;
        }

        if (!matched)
        {
            return false;
        }

        duration = total;
        return true;
    }

    private static DateTimeOffset? Earliest(DateTimeOffset? current, DateTimeOffset candidate) =>
        current is null || candidate < current ? candidate : current;
}
