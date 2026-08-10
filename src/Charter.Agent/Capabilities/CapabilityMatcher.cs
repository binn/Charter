namespace Charter.Agent.Capabilities;

/// <summary>
/// Capability matching (section 27.3), evaluated locally before a granted job is accepted.
/// </summary>
/// <remarks>
/// The control plane filters claims by capability, so in the ordinary case every granted job already
/// matches. The agent checks again anyway: its capability set can change between the claim and the
/// grant — a probe ran, a board was unplugged — and running a job the host can no longer support
/// burns a lease and produces a failure that looks like an agent error.
/// <para>
/// A requirement matches when the names are equal and the required version is a prefix of the
/// advertised version on a dot boundary. Advertised <c>dotnet:10.0.100</c> satisfies required
/// <c>dotnet:10</c> and <c>dotnet</c>; it does not satisfy <c>dotnet:10.0.200</c> or <c>dotnet:9</c>.
/// </para>
/// </remarks>
public static class CapabilityMatcher
{
    /// <summary>True when <paramref name="advertised"/> covers every entry in <paramref name="required"/>.</summary>
    public static bool Satisfies(IReadOnlyCollection<string> advertised, IReadOnlyCollection<string> required)
    {
        ArgumentNullException.ThrowIfNull(advertised);
        ArgumentNullException.ThrowIfNull(required);

        return Missing(advertised, required).Count == 0;
    }

    /// <summary>The requirements this host cannot meet. Empty means the job can run here.</summary>
    public static IReadOnlyList<string> Missing(
        IReadOnlyCollection<string> advertised,
        IReadOnlyCollection<string> required)
    {
        ArgumentNullException.ThrowIfNull(advertised);
        ArgumentNullException.ThrowIfNull(required);

        return [.. required
            .Select(Normalize)
            .Where(r => r.Length > 0)
            .Where(r => !advertised.Select(Normalize).Any(a => Covers(a, r)))
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>True when one advertised capability satisfies one requirement.</summary>
    public static bool Covers(string advertised, string required)
    {
        ArgumentNullException.ThrowIfNull(advertised);
        ArgumentNullException.ThrowIfNull(required);

        var (advertisedName, advertisedVersion) = Split(Normalize(advertised));
        var (requiredName, requiredVersion) = Split(Normalize(required));

        if (!string.Equals(advertisedName, requiredName, StringComparison.Ordinal))
        {
            return false;
        }

        if (requiredVersion.Length == 0)
        {
            return true;
        }

        if (advertisedVersion.Length == 0)
        {
            return false;
        }

        return string.Equals(advertisedVersion, requiredVersion, StringComparison.Ordinal) ||
            advertisedVersion.StartsWith(requiredVersion + ".", StringComparison.Ordinal);
    }

    private static (string Name, string Version) Split(string capability)
    {
        var colon = capability.IndexOf(':', StringComparison.Ordinal);
        return colon < 0
            ? (capability, string.Empty)
            : (capability[..colon], capability[(colon + 1)..]);
    }

    private static string Normalize(string capability) => capability.Trim().ToLowerInvariant();
}
