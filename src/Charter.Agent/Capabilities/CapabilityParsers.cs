using System.Globalization;
using System.Text;

namespace Charter.Agent.Capabilities;

/// <summary>
/// Turns probe output into capability strings (section 32.2).
/// </summary>
/// <remarks>
/// Pure and static on purpose: parsing is the part that breaks when a tool changes its output
/// format, and it is only testable in isolation if it never touches a process. Every parser here
/// takes the captured result and returns what it could establish — an absent tool yields nothing,
/// which is a fact about the host and not an error.
/// <para>
/// Capability strings are <c>name</c> or <c>name:version</c>, lowercase. The matcher
/// (<see cref="CapabilityMatcher"/>) treats the version as dot-separated segments so an advertised
/// <c>dotnet:10.0.100</c> satisfies a required <c>dotnet:10</c>.
/// </para>
/// </remarks>
public static class CapabilityParsers
{
    /// <summary>
    /// <c>dotnet --list-sdks</c>:
    /// <code>
    /// 8.0.404 [/usr/local/share/dotnet/sdk]
    /// 10.0.100 [/usr/local/share/dotnet/sdk]
    /// </code>
    /// </summary>
    public static IReadOnlyList<string> DotnetSdks(ProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded)
        {
            return [];
        }

        var capabilities = new List<string>();
        foreach (var line in Lines(result.StandardOutput))
        {
            var version = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (IsVersion(version))
            {
                capabilities.Add($"dotnet:{version}");
            }
        }

        return capabilities.Count == 0 ? [] : Finish(capabilities, "dotnet");
    }

    /// <summary><c>node --version</c> prints <c>v22.11.0</c>.</summary>
    public static IReadOnlyList<string> NodeVersion(ProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded)
        {
            return [];
        }

        var raw = FirstLine(result.StandardOutput)?.TrimStart('v', 'V');
        return IsVersion(raw) ? Finish([$"node:{raw}"], "node") : [];
    }

    /// <summary>
    /// <c>xcodebuild -version</c>:
    /// <code>
    /// Xcode 16.2
    /// Build version 16C5032a
    /// </code>
    /// A present-but-unlicensed Xcode exits non-zero; that is deliberately not a capability.
    /// </summary>
    public static IReadOnlyList<string> XcodeVersion(ProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded)
        {
            return [];
        }

        foreach (var line in Lines(result.StandardOutput))
        {
            if (!line.StartsWith("Xcode ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var version = line["Xcode ".Length..].Trim();
            if (IsVersion(version))
            {
                return Finish([$"xcode:{version}"], "xcode");
            }
        }

        return [];
    }

    /// <summary>
    /// <c>docker version --format {{.Server.Version}}</c> prints <c>27.3.1</c>, and fails when the
    /// CLI is installed but the daemon is not reachable. No daemon means no docker capability.
    /// </summary>
    public static IReadOnlyList<string> DockerVersion(ProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded)
        {
            return [];
        }

        var version = FirstLine(result.StandardOutput);
        return IsVersion(version) ? Finish([$"docker:{version}"], "docker") : [];
    }

    /// <summary><c>git --version</c> prints <c>git version 2.39.5 (Apple Git-154)</c>.</summary>
    public static IReadOnlyList<string> GitVersion(ProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded)
        {
            return [];
        }

        var line = FirstLine(result.StandardOutput);
        if (line is null)
        {
            return [];
        }

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var version = parts.FirstOrDefault(IsVersion);
        return version is null ? [] : Finish([$"git:{version}"], "git");
    }

    /// <summary><c>python3 --version</c> prints <c>Python 3.12.1</c>, on stdout or stderr by age.</summary>
    public static IReadOnlyList<string> PythonVersion(ProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded)
        {
            return [];
        }

        var line = FirstLine(result.StandardOutput) ?? FirstLine(result.StandardError);
        if (line is null || !line.StartsWith("Python ", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var version = line["Python ".Length..].Trim();
        return IsVersion(version) ? Finish([$"python:{version}"], "python") : [];
    }

    /// <summary>
    /// <c>probe-rs list</c>:
    /// <code>
    /// The following debug probes were found:
    /// [0]: STLink V2 (VID: 0483, PID: 3748, Serial: 0672FF, StLink)
    /// </code>
    /// Yields <c>usb_device:stlink-v2</c>. A host with no probe attached yields nothing, which is
    /// exactly why re-probing matters: unplugging the board must withdraw the capability.
    /// </summary>
    public static IReadOnlyList<string> ProbeRsList(ProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded)
        {
            return [];
        }

        var capabilities = new List<string>();
        foreach (var line in Lines(result.StandardOutput))
        {
            if (!line.StartsWith('['))
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            var description = line[(colon + 1)..].Trim();
            var parenthesis = description.IndexOf('(', StringComparison.Ordinal);
            if (parenthesis > 0)
            {
                description = description[..parenthesis].Trim();
            }

            var slug = Slug(description);
            if (slug.Length > 0)
            {
                capabilities.Add($"usb_device:{slug}");
            }
        }

        return capabilities.Count == 0 ? [] : Finish([.. capabilities, "probe_rs"], "usb_device");
    }

    /// <summary>
    /// <c>lsusb</c>:
    /// <code>
    /// Bus 001 Device 004: ID 0483:3748 STMicroelectronics ST-LINK/V2
    /// </code>
    /// Root hubs are dropped — every machine has them and nothing can be built against one.
    /// </summary>
    public static IReadOnlyList<string> LsUsb(ProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded)
        {
            return [];
        }

        var capabilities = new List<string>();
        foreach (var line in Lines(result.StandardOutput))
        {
            var marker = line.IndexOf(" ID ", StringComparison.Ordinal);
            if (marker < 0)
            {
                continue;
            }

            var rest = line[(marker + 4)..].Trim();
            var space = rest.IndexOf(' ', StringComparison.Ordinal);
            if (space < 0)
            {
                continue;
            }

            var description = rest[(space + 1)..].Trim();
            if (description.Contains("root hub", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var slug = Slug(description);
            if (slug.Length > 0)
            {
                capabilities.Add($"usb_device:{slug}");
            }
        }

        return capabilities.Count == 0 ? [] : Finish(capabilities, "usb_device");
    }

    /// <summary>Normalises, de-duplicates and orders a probe's output, adding the bare family name.</summary>
    private static IReadOnlyList<string> Finish(IEnumerable<string> capabilities, string family) =>
        [.. capabilities.Append(family)
            .Select(c => c.Trim().ToLowerInvariant())
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? FirstLine(string text) => Lines(text).FirstOrDefault();

    private static bool IsVersion(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        var segments = candidate.Split('.');
        return segments.Length >= 2 &&
            int.TryParse(segments[0], NumberStyles.None, CultureInfo.InvariantCulture, out _) &&
            segments.All(s => s.Length > 0 && s.All(char.IsLetterOrDigit));
    }

    /// <summary>Lowercase, alphanumeric, hyphen-separated. Capability strings must stay comparable.</summary>
    internal static string Slug(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length > 48 ? slug[..48].TrimEnd('-') : slug;
    }
}
