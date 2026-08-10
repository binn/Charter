namespace Charter.Runners.Shim;

/// <summary>The verdict on whether this image can run this session (section 32.1).</summary>
/// <param name="Satisfied">True when every declared requirement is present.</param>
/// <param name="Missing">Requirements the image does not provide, as capability tokens.</param>
/// <param name="Message">
/// What to tell the engineer. Actionable by construction: it names the missing toolchain, the image
/// that was used, and the image that has it.
/// </param>
public sealed record ShimToolchainVerdict(bool Satisfied, IReadOnlyList<string> Missing, string? Message);

/// <summary>
/// The rule section 32.1 states without qualification: <strong>a session never installs a language
/// runtime.</strong>
/// </summary>
/// <remarks>
/// <para>
/// This is a security control, not a speed optimisation (section 16.1). A session permitted to
/// install its own tooling can fetch a typosquatted package that reads the workspace, read whatever
/// credential material is in the process, and — on a detached runner — reveal the operator's own
/// network position to an attacker-controlled endpoint. Prebuilt images plus the egress allowlist are
/// what close that vector, and they only close it if the session cannot apt-get its way around them.
/// </para>
/// <para>
/// So when the image lacks a declared requirement the session fails, immediately, with a message
/// naming the image that does have it. It does not degrade, it does not install, and it does not
/// carry on and fail later in a way that reads as an agent error.
/// </para>
/// </remarks>
public static class ShimToolchain
{
    /// <summary>Which shipped image carries which toolchain (section 32.1).</summary>
    private static readonly Dictionary<string, string> ImagesByCapability = new(StringComparer.Ordinal)
    {
        ["dotnet"] = "ghcr.io/binn/charter-runner-dotnet",
        ["node"] = "ghcr.io/binn/charter-runner-node",
        ["python"] = "ghcr.io/binn/charter-runner-python",
        ["uv"] = "ghcr.io/binn/charter-runner-python",
        ["toolchain"] = "ghcr.io/binn/charter-runner-embedded",
        ["unity_license"] = "ghcr.io/binn/charter-runner-unity",
    };

    /// <summary>Compares what the session declared it needs against what the image actually has.</summary>
    /// <param name="required">Capability tokens from the dispatch (section 27.3).</param>
    /// <param name="probed">
    /// What this host reports, probed rather than assumed (section 32.2). Expanded here, so a probe
    /// reporting <c>dotnet:10.0.100</c> satisfies a requirement of <c>dotnet:10</c>.
    /// </param>
    /// <param name="image">The runner image in use, for the message.</param>
    public static ShimToolchainVerdict Verify(
        IReadOnlyList<string> required,
        IReadOnlyList<string> probed,
        string? image = null)
    {
        ArgumentNullException.ThrowIfNull(required);
        ArgumentNullException.ThrowIfNull(probed);

        var advertised = new HashSet<string>(RunnerCapability.ExpandAll(probed), StringComparer.Ordinal);
        var missing = RunnerCapability.Missing(advertised, required);

        if (missing.Count == 0)
        {
            return new ShimToolchainVerdict(true, [], null);
        }

        var described = RunnerCapability.DescribeAll(missing);
        var used = string.IsNullOrWhiteSpace(image) ? "this runner image" : $"the image '{image}'";
        var suggestions = missing
            .Select(RunnerCapability.NameOf)
            .Distinct(StringComparer.Ordinal)
            .Where(ImagesByCapability.ContainsKey)
            .Select(name => ImagesByCapability[name])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var remedy = suggestions.Length > 0
            ? $"Set runner_image in .charter/config.yml to an image that has it — {string.Join(" or ", suggestions)} "
              + "— or build your own from the documented Dockerfiles in runners/."
            : "Set runner_image in .charter/config.yml to an image that has it, or build one from the "
              + "documented Dockerfiles in runners/.";

        return new ShimToolchainVerdict(
            false,
            missing,
            $"This session needs {described}, and {used} does not provide it. A session never installs a "
            + $"language runtime, so it stops here rather than changing the image underneath itself. {remedy}");
    }
}
