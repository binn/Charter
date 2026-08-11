using System.Globalization;

namespace Charter.Updates;

/// <summary>
/// A semantic version, as it appears on a Charter release tag (sections 24, 28).
/// </summary>
/// <remarks>
/// <para>
/// <c>CHANGELOG.md</c> fixes the tag grammar this parses: <c>vMAJOR.MINOR.PATCH</c> for a stable
/// release and <c>vMAJOR.MINOR.PATCH-&lt;prerelease&gt;</c> for a prerelease, with the leading
/// <c>v</c> required on the tag and absent from the compiled-in build version. A tag that does not
/// parse is ignored rather than guessed at, which is what makes a mistyped tag fail quietly on the
/// operator's instance instead of announcing a release that does not exist.
/// </para>
/// <para>
/// Ordering follows semver: a prerelease sorts <em>below</em> the release it leads to, and prerelease
/// identifiers compare numerically when both are numeric and ordinally otherwise. Without that,
/// <c>0.5.0-rc.1</c> would be offered to an instance already running <c>0.5.0</c>.
/// </para>
/// </remarks>
public sealed record ReleaseVersion : IComparable<ReleaseVersion>
{
    private ReleaseVersion(int major, int minor, int patch, string prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    /// <summary>Major component.</summary>
    public int Major { get; }

    /// <summary>Minor component.</summary>
    public int Minor { get; }

    /// <summary>Patch component.</summary>
    public int Patch { get; }

    /// <summary>The prerelease identifiers, or an empty string for a stable version.</summary>
    public string Prerelease { get; }

    /// <summary>True when this version is a prerelease of the three numbers in front of it.</summary>
    public bool IsPrerelease => Prerelease.Length > 0;

    /// <summary>
    /// Parses a tag or a build version, tolerating a leading <c>v</c> and ignoring build metadata.
    /// </summary>
    /// <returns>The version, or <see langword="null"/> when the text is not one.</returns>
    public static ReleaseVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var span = text.Trim();

        if (span.StartsWith('v') || span.StartsWith('V'))
        {
            span = span[1..];
        }

        // Build metadata never affects precedence, so it is dropped rather than compared.
        var plus = span.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            span = span[..plus];
        }

        var prerelease = string.Empty;
        var dash = span.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            prerelease = span[(dash + 1)..];
            span = span[..dash];

            if (prerelease.Length == 0)
            {
                return null;
            }
        }

        var parts = span.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        if (!TryNumber(parts[0], out var major)
            || !TryNumber(parts[1], out var minor)
            || !TryNumber(parts[2], out var patch))
        {
            return null;
        }

        return new ReleaseVersion(major, minor, patch, prerelease);
    }

    /// <inheritdoc />
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        if (Major != other.Major)
        {
            return Major.CompareTo(other.Major);
        }

        if (Minor != other.Minor)
        {
            return Minor.CompareTo(other.Minor);
        }

        if (Patch != other.Patch)
        {
            return Patch.CompareTo(other.Patch);
        }

        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    /// <summary>True when <paramref name="other"/> is a version an instance on this one should hear about.</summary>
    public bool IsOlderThan(ReleaseVersion? other) => other is not null && CompareTo(other) < 0;

    /// <inheritdoc />
    public override string ToString() => IsPrerelease
        ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-{Prerelease}")
        : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    /// <summary>Semver rule 11.3: a version with a prerelease is lower than one without.</summary>
    private static int ComparePrerelease(string left, string right)
    {
        if (left.Length == 0 && right.Length == 0)
        {
            return 0;
        }

        if (left.Length == 0)
        {
            return 1;
        }

        if (right.Length == 0)
        {
            return -1;
        }

        var leftParts = left.Split('.');
        var rightParts = right.Split('.');

        for (var index = 0; index < Math.Max(leftParts.Length, rightParts.Length); index++)
        {
            if (index >= leftParts.Length)
            {
                return -1;
            }

            if (index >= rightParts.Length)
            {
                return 1;
            }

            var leftNumeric = TryNumber(leftParts[index], out var leftValue);
            var rightNumeric = TryNumber(rightParts[index], out var rightValue);

            var comparison = (leftNumeric, rightNumeric) switch
            {
                (true, true) => leftValue.CompareTo(rightValue),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(leftParts[index], rightParts[index]),
            };

            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static bool TryNumber(string text, out int value)
    {
        value = 0;

        if (text.Length == 0 || text.Length > 9)
        {
            return false;
        }

        foreach (var character in text)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
