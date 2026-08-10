using System.Reflection;
using System.Runtime.InteropServices;
using Charter.Agent.Capabilities;
using Charter.Agent.Protocol;

namespace Charter.Agent;

/// <summary>
/// What this build is and what host it is running on.
/// </summary>
/// <remarks>
/// The version and commit are compiled in by <c>Directory.Build.props</c> rather than read from
/// configuration, so an agent in the runners list reports the build that is actually running.
/// </remarks>
public static class AgentBuild
{
    private static readonly Assembly Self = typeof(AgentBuild).Assembly;

    public static string Version { get; } = Metadata("Charter.Version") ?? "0.0.0-unknown";

    public static string CommitSha { get; } = Metadata("Charter.CommitSha") ?? "unknown";

    /// <summary>The <c>User-Agent</c> on the pairing call and the WebSocket upgrade.</summary>
    public static string UserAgent { get; } =
        $"charter-agent/{Version} ({CapabilityProber.HostOs()}; {RuntimeInformation.RuntimeIdentifier})";

    public static HostPlatform Platform() => new()
    {
        Os = CapabilityProber.HostOs(),
        Arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        Rid = RuntimeInformation.RuntimeIdentifier,
        Hostname = Environment.MachineName,
        CpuCount = Environment.ProcessorCount,
        TotalMemoryMb = TotalMemoryMb(),
    };

    private static long? TotalMemoryMb()
    {
        var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return bytes > 0 ? bytes / (1024 * 1024) : null;
    }

    private static string? Metadata(string key) => Self
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal))
        ?.Value;
}
