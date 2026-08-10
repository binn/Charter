using System.Reflection;

namespace Charter.Diagnostics;

/// <summary>
/// Build identity compiled into the assembly by <c>Directory.Build.props</c>.
/// </summary>
/// <remarks>
/// <para>
/// Section 24: Charter is network-interactive AGPL software, so the running instance must offer
/// users a way to obtain its Corresponding Source - including any operator modifications. The
/// commit SHA and source URL are therefore built in at compile time rather than configured.
/// </para>
/// <para>
/// Section 28 compares <see cref="Version"/> against the latest GitHub release tag.
/// </para>
/// </remarks>
public static class BuildInfo
{
    static BuildInfo()
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            metadata[attribute.Key] = attribute.Value ?? string.Empty;
        }

        Version = Read(metadata, "Charter.Version", "0.0.0-unknown");
        CommitSha = Read(metadata, "Charter.CommitSha", "unknown");
        SourceUrl = Read(metadata, "Charter.SourceUrl", "https://github.com/binn/Charter");
        BuildDate = Read(metadata, "Charter.BuildDate", "unknown");
    }

    /// <summary>Semantic version of this build, e.g. <c>1.4.0</c>.</summary>
    public static string Version { get; }

    /// <summary>Full commit SHA this build was produced from.</summary>
    public static string CommitSha { get; }

    /// <summary>Repository the Corresponding Source can be obtained from.</summary>
    public static string SourceUrl { get; }

    /// <summary>ISO-8601 timestamp of the build.</summary>
    public static string BuildDate { get; }

    /// <summary>
    /// Link an operator's users can follow to this instance's exact source (AGPL section 13).
    /// </summary>
    public static string SourceLink =>
        CommitSha is "unknown" or ""
            ? SourceUrl
            : $"{SourceUrl.TrimEnd('/')}/tree/{CommitSha}";

    private static string Read(IReadOnlyDictionary<string, string> metadata, string key, string fallback)
        => metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
}
