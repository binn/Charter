using Charter.Api.Contracts;
using Charter.Runners;

namespace Charter.Api.Runners;

/// <summary>
/// Turns a runner's advertised capability tokens into the rows Settings → Runners shows.
/// </summary>
/// <remarks>
/// <para>
/// A registered agent stores its advertisement <em>expanded</em> — <c>dotnet:10.0.100</c> is kept
/// alongside <c>dotnet:10.0</c>, <c>dotnet:10</c> and <c>dotnet</c>, so that matching a session's
/// coarse requirement is plain set containment and the Postgres <c>&lt;@</c> filter in the job queue
/// cannot disagree with the C# matcher (see <see cref="RunnerCapability"/>). That is exactly the
/// wrong shape for a list a person reads, which would show four rows for one SDK. So the coarser
/// tokens are folded back into the most specific one here, at the display boundary and nowhere else.
/// </para>
/// <para>
/// Section 32.2: a runner <strong>probes and reports</strong>. <c>probedBy</c> is the command that
/// found it — the difference between a claim and a measurement, and the answer to <em>"why does this
/// agent think it has Xcode 16.2"</em>. The agent reports the probe results but not the command
/// lines, so the table below maps a family to the command section 32.2 names for it, and anything
/// outside the table gets no <c>probedBy</c> key at all. A plausible command that was never run
/// would be worse than an absent one: it invites somebody to paste it and get a different answer.
/// </para>
/// </remarks>
public static class AgentCapabilityProbes
{
    private static readonly Dictionary<string, string> Commands = new(StringComparer.Ordinal)
    {
        // The four section 32.2 names outright.
        ["dotnet"] = "dotnet --list-sdks",
        ["node"] = "node --version",
        ["xcode"] = "xcodebuild -version",
        ["usb_device"] = "probe-rs list",

        // The rest are the probes the agent's own capability reporter runs.
        ["python"] = "python3 --version",
        ["docker"] = "docker version --format {{.Server.Version}}",
        ["go"] = "go version",
        ["rust"] = "rustc --version",
        ["java"] = "java -version",
        ["gpu"] = "nvidia-smi --query-gpu=name --format=csv,noheader",
        ["signing"] = "security find-identity -v -p codesigning",
        ["toolchain"] = "arm-none-eabi-gcc --version",
    };

    private static readonly Dictionary<string, string> OperatingSystemCommands = new(StringComparer.Ordinal)
    {
        ["linux"] = "uname -s",
        ["macos"] = "sw_vers -productVersion",
        ["windows"] = "ver",
    };

    /// <summary>The command that found a capability, or <c>null</c> when this build cannot say.</summary>
    public static string? ProbeFor(string token)
    {
        ArgumentNullException.ThrowIfNull(token);

        var name = RunnerCapability.NameOf(token);

        if (OperatingSystemCommands.TryGetValue(name, out var uname))
        {
            return uname;
        }

        return Commands.GetValueOrDefault(name);
    }

    /// <summary>
    /// The family a token groups under, as the client renders it.
    /// </summary>
    /// <remarks>
    /// The three operating systems are their own tokens rather than <c>os:linux</c>, so they are
    /// folded into an <c>os</c> family here — which is what makes "this is a Mac" one row in a list
    /// rather than a bare word beside a version number.
    /// </remarks>
    public static string FamilyOf(string token)
    {
        ArgumentNullException.ThrowIfNull(token);

        var name = RunnerCapability.NameOf(token);
        return OperatingSystemCommands.ContainsKey(name) ? "os" : name;
    }

    /// <summary>
    /// The display rows for one agent's advertisement, most specific token per capability.
    /// </summary>
    /// <param name="advertised">The stored, already-expanded set.</param>
    /// <param name="probedAt">When the agent last probed (section 32.2).</param>
    public static IReadOnlyList<AgentCapabilityResponse> Describe(
        IReadOnlyList<string> advertised,
        DateTimeOffset probedAt)
    {
        ArgumentNullException.ThrowIfNull(advertised);

        var tokens = advertised
            .Select(RunnerCapability.Normalize)
            .Where(token => token.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var specific = tokens
            .Where(token => !tokens.Any(other => IsRefinementOf(other, token)))
            .OrderBy(RunnerCapability.NameOf, StringComparer.Ordinal)
            .ThenBy(token => token, StringComparer.Ordinal)
            .ToList();

        return
        [
            .. specific.Select(token => new AgentCapabilityResponse
            {
                Id = token,
                Family = FamilyOf(token),
                Label = RunnerCapability.Describe(RunnerCapability.NameOf(token)),
                Version = RunnerCapability.VersionOf(token),
                ProbedBy = ProbeFor(token),
                ProbedAt = probedAt,
            }),
        ];
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is a more precise spelling of <paramref name="token"/>.
    /// </summary>
    /// <remarks>
    /// The boundary check matters: <c>dotnet:10</c> refines <c>dotnet</c>, and <c>dotnet:100</c> does
    /// not refine <c>dotnet:10</c>. A plain <c>StartsWith</c> would collapse two genuinely different
    /// SDKs into one row.
    /// </remarks>
    private static bool IsRefinementOf(string candidate, string token)
    {
        if (candidate.Length <= token.Length
            || !candidate.StartsWith(token, StringComparison.Ordinal))
        {
            return false;
        }

        var next = candidate[token.Length];
        return next == RunnerCapability.VersionSeparator || next == '.';
    }
}
