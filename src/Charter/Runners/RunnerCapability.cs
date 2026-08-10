using System.Text;

namespace Charter.Runners;

/// <summary>
/// One capability token — <c>linux</c>, <c>dotnet:10</c>, <c>usb_device:stm32f4</c> (sections 27.3,
/// 32.2).
/// </summary>
/// <remarks>
/// <para>
/// Runners <em>probe and report</em> what they have rather than being told (section 32.2), so what a
/// runner advertises is precise — <c>dotnet:10.0.100</c> — while what a session requires is coarse —
/// <c>dotnet:10</c>, as <c>standards.yml</c> writes it in section 27.2. Matching therefore cannot be
/// string equality.
/// </para>
/// <para>
/// The resolution is to <see cref="Expand(string)"/> an advertised token into every coarser token it
/// implies, once, at registration. A runner advertising <c>dotnet:10.0.100</c> also advertises
/// <c>dotnet:10.0</c>, <c>dotnet:10</c> and <c>dotnet</c>. Matching is then plain set containment,
/// which matters for a reason beyond tidiness: <see cref="Charter.Data.JobQueue"/> filters claims
/// with the Postgres array containment operator <c>&lt;@</c>, which is exact. Expanding at
/// registration is what keeps the SQL filter and the C# matcher from ever disagreeing about whether
/// a job is claimable.
/// </para>
/// </remarks>
public static class RunnerCapability
{
    /// <summary>Separates a capability's name from its version or model.</summary>
    public const char VersionSeparator = ':';

    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.Ordinal)
    {
        ["linux"] = "Linux",
        ["macos"] = "macOS",
        ["windows"] = "Windows",
        ["docker"] = "Docker",
        ["gpu"] = "a GPU",
        ["signing"] = "code signing",
        ["xcode"] = "Xcode",
        ["unity_license"] = "a Unity licence",
        ["usb_device"] = "a USB device",
        ["toolchain"] = "a cross toolchain",
        ["dotnet"] = ".NET",
        ["node"] = "Node",
        ["python"] = "Python",
    };

    /// <summary>Lower-cases and trims a token so two spellings of one capability compare equal.</summary>
    public static string Normalize(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return token.Trim().ToLowerInvariant();
    }

    /// <summary>The part before the separator: <c>dotnet</c> for <c>dotnet:10.0.100</c>.</summary>
    public static string NameOf(string token)
    {
        var normalized = Normalize(token);
        var separator = normalized.IndexOf(VersionSeparator, StringComparison.Ordinal);
        return separator < 0 ? normalized : normalized[..separator];
    }

    /// <summary>The part after the separator, or <see langword="null"/> when the token is bare.</summary>
    public static string? VersionOf(string token)
    {
        var normalized = Normalize(token);
        var separator = normalized.IndexOf(VersionSeparator, StringComparison.Ordinal);
        return separator < 0 || separator == normalized.Length - 1 ? null : normalized[(separator + 1)..];
    }

    /// <summary>
    /// Every token an advertised capability implies, coarsest first.
    /// </summary>
    /// <remarks>
    /// <c>dotnet:10.0.100</c> yields <c>dotnet</c>, <c>dotnet:10</c>, <c>dotnet:10.0</c> and
    /// <c>dotnet:10.0.100</c>. A non-numeric version has no meaningful prefixes, so
    /// <c>usb_device:stm32f4</c> yields only <c>usb_device</c> and <c>usb_device:stm32f4</c>.
    /// </remarks>
    public static IReadOnlyList<string> Expand(string token)
    {
        var normalized = Normalize(token);
        if (normalized.Length == 0)
        {
            return [];
        }

        var name = NameOf(normalized);
        var version = VersionOf(normalized);

        if (version is null)
        {
            return [name];
        }

        var expanded = new List<string>(4) { name };
        var builder = new StringBuilder(name);
        var segments = version.Split('.', StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < segments.Length; index++)
        {
            builder.Append(index == 0 ? VersionSeparator : '.').Append(segments[index]);
            expanded.Add(builder.ToString());
        }

        return expanded;
    }

    /// <summary>Expands and de-duplicates a whole advertisement.</summary>
    public static IReadOnlyList<string> ExpandAll(IEnumerable<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var expanded = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            foreach (var implied in Expand(token))
            {
                expanded.Add(implied);
            }
        }

        return [.. expanded];
    }

    /// <summary>
    /// True when <paramref name="advertised"/> — already expanded — covers <paramref name="required"/>.
    /// </summary>
    public static bool Satisfies(IReadOnlyCollection<string> advertised, string required)
    {
        ArgumentNullException.ThrowIfNull(advertised);
        return advertised.Contains(Normalize(required));
    }

    /// <summary>Requirements <paramref name="advertised"/> does not cover, in the order given.</summary>
    public static IReadOnlyList<string> Missing(
        IReadOnlyCollection<string> advertised,
        IEnumerable<string> required)
    {
        ArgumentNullException.ThrowIfNull(advertised);
        ArgumentNullException.ThrowIfNull(required);

        return [.. required
            .Select(Normalize)
            .Where(token => token.Length > 0 && !advertised.Contains(token))
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// A capability in the words section 27.3 uses in its example message — <c>macos</c> becomes
    /// <em>macOS</em> and <c>xcode:16</c> becomes <em>Xcode 16</em>.
    /// </summary>
    public static string Describe(string token)
    {
        var name = NameOf(token);
        var version = VersionOf(token);
        var described = Descriptions.TryGetValue(name, out var friendly) ? friendly : name;

        return version is null ? described : $"{described} {version}";
    }

    /// <summary>Joins several descriptions the way a sentence does: <c>a, b and c</c>.</summary>
    public static string DescribeAll(IReadOnlyList<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var described = tokens.Select(Describe).ToArray();

        return described.Length switch
        {
            0 => string.Empty,
            1 => described[0],
            _ => $"{string.Join(", ", described[..^1])} and {described[^1]}",
        };
    }
}
