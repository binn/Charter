namespace Charter.Auth.Authorization;

/// <summary>
/// The little bit of glob reasoning the section 7.5 composition rule needs: does one path pattern
/// cover another?
/// </summary>
/// <remarks>
/// <para>
/// Deliberately prefix-only. <c>src/Features/**</c> covers <c>src/Features/Billing/**</c> and
/// <c>src/Features/Billing/Invoice.cs</c>; it does not cover <c>src/**</c>. Anything more expressive
/// would be a second pattern language to keep in step with the one the runner enforces, and section
/// 8 is explicit that the enforcing copy of a path scope lives in the runner, not here. This is the
/// composition arithmetic for the <em>policy</em> only.
/// </para>
/// <para>
/// When two patterns are unrelated neither covers the other, and
/// <see cref="Intersect"/> drops the pair. Dropping is always the tightening direction, which is the
/// property the composition rule depends on.
/// </para>
/// </remarks>
public static class PathScope
{
    /// <summary>True when everything matching <paramref name="candidate"/> also matches <paramref name="pattern"/>.</summary>
    public static bool Covers(string pattern, string candidate)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(candidate);

        var coveringPrefix = Normalize(pattern);
        var coveredPrefix = Normalize(candidate);

        if (coveringPrefix.Length == 0)
        {
            // "**" - the whole repository.
            return true;
        }

        return coveredPrefix.Equals(coveringPrefix, StringComparison.Ordinal)
            || coveredPrefix.StartsWith(coveringPrefix + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// The tightest set of patterns allowed by both lists.
    /// </summary>
    /// <remarks>
    /// An empty list means "unrestricted", so an empty side yields the other side unchanged. When
    /// both sides are populated the result keeps, for each pair, whichever pattern is the narrower
    /// of the two, and drops pairs that do not overlap. The result is therefore never wider than
    /// either input, which is what makes the repository side unable to loosen the organisation side.
    /// An empty result from two populated inputs means the two disagree completely, and the caller
    /// treats that as "nothing is auto-dispatchable" rather than as "unrestricted".
    /// </remarks>
    public static IReadOnlyList<string> Intersect(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.Count == 0)
        {
            return right;
        }

        if (right.Count == 0)
        {
            return left;
        }

        var narrowed = new List<string>();

        foreach (var outer in left)
        {
            foreach (var inner in right)
            {
                if (Covers(inner, outer))
                {
                    narrowed.Add(outer);
                }
                else if (Covers(outer, inner))
                {
                    narrowed.Add(inner);
                }
            }
        }

        return [.. narrowed.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>True when <paramref name="patterns"/> permits <paramref name="path"/>; an empty list permits everything.</summary>
    public static bool Permits(IReadOnlyList<string> patterns, string path)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        return patterns.Count == 0 || patterns.Any(pattern => Covers(pattern, path));
    }

    private static string Normalize(string pattern)
    {
        var trimmed = pattern.Trim().Replace('\\', '/').Trim('/');

        while (trimmed.EndsWith("/**", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^3];
        }

        return trimmed is "**" or "*" ? string.Empty : trimmed.TrimEnd('/');
    }
}
