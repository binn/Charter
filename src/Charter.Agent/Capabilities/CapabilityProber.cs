using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Charter.Agent.Protocol;

namespace Charter.Agent.Capabilities;

/// <summary>What a host can do, as established by running the probes rather than by being told.</summary>
/// <param name="Capabilities">Sorted, de-duplicated capability strings.</param>
/// <param name="ProbedAt">When the probe ran. Re-probing replaces the whole set.</param>
/// <param name="Reports">Per-probe detail, for the runners page and for diagnosing a missing tool.</param>
public sealed record CapabilitySet(
    IReadOnlyList<string> Capabilities,
    DateTimeOffset ProbedAt,
    IReadOnlyList<ProbeReport> Reports)
{
    public static CapabilitySet Empty { get; } = new([], DateTimeOffset.MinValue, []);

    /// <summary>Stable hash of the set, so drift is cheap for the plane to detect on a heartbeat.</summary>
    public string Hash => Fingerprint(Capabilities);

    public static string Fingerprint(IReadOnlyList<string> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        var joined = string.Join('\n', capabilities.Order(StringComparer.Ordinal));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}

/// <summary>One command the agent runs to find out what it has.</summary>
/// <param name="Name">Short identifier, reported back to the plane.</param>
/// <param name="Executable">The command. Absent commands are an expected outcome.</param>
/// <param name="Arguments">Arguments, never shell-interpolated.</param>
/// <param name="Parse">Turns the captured result into capability strings.</param>
/// <param name="AppliesTo">The platforms worth trying it on. <c>null</c> means all of them.</param>
public sealed record ProbeDefinition(
    string Name,
    string Executable,
    IReadOnlyList<string> Arguments,
    Func<ProcessResult, IReadOnlyList<string>> Parse,
    IReadOnlyList<OSPlatform>? AppliesTo = null);

/// <summary>
/// Runs every probe and assembles the advertised capability set (section 32.2).
/// </summary>
/// <remarks>
/// Re-run on restart and on a daily interval, because a Mac mini that took an Xcode update overnight
/// must not keep advertising the old version. The daily tick is driven by
/// <see cref="Session.AgentSession"/>; this type only knows how to produce a set.
/// </remarks>
public sealed class CapabilityProber(IProcessRunner processRunner, IReadOnlyList<ProbeDefinition>? probes = null)
{
    /// <summary>A probe that hangs must not hang registration.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    private readonly IProcessRunner _processRunner = processRunner;
    private readonly IReadOnlyList<ProbeDefinition> _probes = probes ?? Default;

    /// <summary>The probes named in section 32.2, plus the ones the runner images depend on.</summary>
    public static IReadOnlyList<ProbeDefinition> Default { get; } =
    [
        new("dotnet", "dotnet", ["--list-sdks"], CapabilityParsers.DotnetSdks),
        new("node", "node", ["--version"], CapabilityParsers.NodeVersion),
        new("python", "python3", ["--version"], CapabilityParsers.PythonVersion),
        new("git", "git", ["--version"], CapabilityParsers.GitVersion),
        new("docker", "docker", ["version", "--format", "{{.Server.Version}}"], CapabilityParsers.DockerVersion),
        new("xcode", "xcodebuild", ["-version"], CapabilityParsers.XcodeVersion, [OSPlatform.OSX]),
        new("probe-rs", "probe-rs", ["list"], CapabilityParsers.ProbeRsList),
        new("lsusb", "lsusb", [], CapabilityParsers.LsUsb, [OSPlatform.Linux]),
    ];

    /// <summary>
    /// Facts about the host that need no command: operating system, architecture, execution mode.
    /// These are what section 27.3's <c>linux</c> / <c>macos</c> requirements match against.
    /// </summary>
    public static IReadOnlyList<string> PlatformCapabilities(AgentExecutionMode mode) =>
    [
        HostOs(),
        $"arch:{RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}",
        mode == AgentExecutionMode.Docker ? "runner:docker" : "runner:native",
    ];

    public static string HostOs() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos"
        : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
        : RuntimeInformation.RuntimeIdentifier;

    public async Task<CapabilitySet> ProbeAsync(
        AgentExecutionMode mode,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var capabilities = new List<string>(PlatformCapabilities(mode));
        var reports = new List<ProbeReport>();

        foreach (var probe in _probes)
        {
            if (probe.AppliesTo is { Count: > 0 } platforms &&
                !platforms.Any(RuntimeInformation.IsOSPlatform))
            {
                continue;
            }

            var result = await _processRunner
                .RunAsync(probe.Executable, probe.Arguments, ProbeTimeout, cancellationToken);

            var found = probe.Parse(result);
            capabilities.AddRange(found);
            reports.Add(new ProbeReport
            {
                Name = probe.Name,
                ToolPresent = result.Started,
                Capabilities = found,
                Note = Note(result, found),
            });
        }

        return new CapabilitySet(
            [.. capabilities
                .Select(c => c.Trim().ToLowerInvariant())
                .Where(c => c.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)],
            now,
            reports);
    }

    /// <summary>
    /// Why a probe produced nothing, in words an operator can act on. Never the raw output — a
    /// probe's stderr is the one place a host secret could leak into a capability report.
    /// </summary>
    private static string? Note(ProcessResult result, IReadOnlyList<string> found)
    {
        if (found.Count > 0)
        {
            return null;
        }

        if (!result.Started)
        {
            return "not installed on this host";
        }

        return result.ExitCode == 0
            ? "ran, but reported nothing usable"
            : $"exited {result.ExitCode}";
    }
}
