using Charter.Agent;
using Charter.Agent.Capabilities;

namespace Charter.Tests;

/// <summary>
/// Capability probing (section 32.2) and matching (section 27.3). No process is ever started: the
/// runner is stubbed, so every parser is exercised against captured output including a missing tool.
/// </summary>
public class AgentCapabilityTests
{
    [Fact]
    public void ParsesEverySdkFromDotnetListSdks()
    {
        var result = CapabilityParsers.DotnetSdks(ProcessResult.Ok(
            """
            8.0.404 [/usr/local/share/dotnet/sdk]
            10.0.100 [/usr/local/share/dotnet/sdk]
            """));

        Assert.Contains("dotnet:8.0.404", result);
        Assert.Contains("dotnet:10.0.100", result);
        Assert.Contains("dotnet", result);
    }

    [Fact]
    public void ParsesNodeVersion()
    {
        var result = CapabilityParsers.NodeVersion(ProcessResult.Ok("v22.11.0\n"));

        Assert.Contains("node:22.11.0", result);
        Assert.Contains("node", result);
    }

    [Fact]
    public void ParsesXcodeVersion()
    {
        var result = CapabilityParsers.XcodeVersion(ProcessResult.Ok(
            """
            Xcode 16.2
            Build version 16C5032a
            """));

        Assert.Contains("xcode:16.2", result);
    }

    [Fact]
    public void AnUnlicensedXcodeAdvertisesNothing()
    {
        // xcodebuild is installed but exits non-zero until the licence is accepted. Advertising it
        // would send iOS sessions to a runner that cannot build them.
        var result = CapabilityParsers.XcodeVersion(
            new ProcessResult(true, 69, string.Empty, "You have not agreed to the Xcode license agreements."));

        Assert.Empty(result);
    }

    [Fact]
    public void ParsesDockerServerVersion()
    {
        Assert.Contains("docker:27.3.1", CapabilityParsers.DockerVersion(ProcessResult.Ok("27.3.1\n")));
    }

    [Fact]
    public void DockerWithNoDaemonAdvertisesNothing()
    {
        var result = CapabilityParsers.DockerVersion(
            new ProcessResult(true, 1, string.Empty, "Cannot connect to the Docker daemon at unix:///var/run/docker.sock."));

        Assert.Empty(result);
    }

    [Fact]
    public void ParsesGitAndPythonVersions()
    {
        Assert.Contains("git:2.39.5", CapabilityParsers.GitVersion(ProcessResult.Ok("git version 2.39.5 (Apple Git-154)")));
        Assert.Contains("python:3.12.1", CapabilityParsers.PythonVersion(ProcessResult.Ok("Python 3.12.1")));
    }

    [Fact]
    public void ParsesAttachedDebugProbes()
    {
        var result = CapabilityParsers.ProbeRsList(ProcessResult.Ok(
            """
            The following debug probes were found:
            [0]: STLink V2 (VID: 0483, PID: 3748, Serial: 0672FF, StLink)
            """));

        Assert.Contains("usb_device:stlink-v2", result);
        Assert.Contains("usb_device", result);
        Assert.Contains("probe_rs", result);
    }

    [Fact]
    public void AnUnpluggedBoardAdvertisesNothing()
    {
        var result = CapabilityParsers.ProbeRsList(ProcessResult.Ok("No debug probes were found.\n"));

        Assert.Empty(result);
    }

    [Fact]
    public void ParsesLsUsbAndDropsRootHubs()
    {
        var result = CapabilityParsers.LsUsb(ProcessResult.Ok(
            """
            Bus 002 Device 001: ID 1d6b:0003 Linux Foundation 3.0 root hub
            Bus 001 Device 004: ID 0483:3748 STMicroelectronics ST-LINK/V2
            """));

        Assert.Contains("usb_device:stmicroelectronics-st-link-v2", result);
        Assert.DoesNotContain(result, c => c.Contains("root-hub", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("dotnet")]
    [InlineData("node")]
    [InlineData("xcode")]
    [InlineData("docker")]
    [InlineData("git")]
    [InlineData("python")]
    [InlineData("probe-rs")]
    [InlineData("lsusb")]
    public void AMissingToolIsNotAFailure(string probe)
    {
        // Most hosts lack most of these. An absent tool is a fact about the host, not an error.
        var parse = probe switch
        {
            "dotnet" => CapabilityParsers.DotnetSdks,
            "node" => CapabilityParsers.NodeVersion,
            "xcode" => CapabilityParsers.XcodeVersion,
            "docker" => CapabilityParsers.DockerVersion,
            "git" => CapabilityParsers.GitVersion,
            "python" => CapabilityParsers.PythonVersion,
            "probe-rs" => CapabilityParsers.ProbeRsList,
            _ => (Func<ProcessResult, IReadOnlyList<string>>)CapabilityParsers.LsUsb,
        };

        Assert.Empty(parse(ProcessResult.NotFound));
    }

    [Fact]
    public async Task ProbingReportsWhatRanAndWhatWasMissing()
    {
        var runner = new StubProcessRunner
        {
            Results =
            {
                ["dotnet"] = ProcessResult.Ok("10.0.100 [/usr/share/dotnet/sdk]"),
                ["node"] = ProcessResult.Ok("v22.11.0"),
                ["git"] = ProcessResult.Ok("git version 2.43.0"),
            },
        };

        var probed = await new CapabilityProber(runner).ProbeAsync(AgentExecutionMode.Docker, DateTimeOffset.UnixEpoch, TestContext.Current.CancellationToken);

        Assert.Contains("dotnet:10.0.100", probed.Capabilities);
        Assert.Contains("node:22.11.0", probed.Capabilities);
        Assert.Contains("runner:docker", probed.Capabilities);
        Assert.Contains(probed.Capabilities, c => c.StartsWith("arch:", StringComparison.Ordinal));

        var missing = probed.Reports.Single(r => r.Name == "python");
        Assert.False(missing.ToolPresent);
        Assert.Equal("not installed on this host", missing.Note);
        Assert.Empty(missing.Capabilities);
    }

    [Fact]
    public async Task ReprobingAfterAnXcodeUpdateReplacesTheAdvertisedVersion()
    {
        // The Mac mini of section 32.2: it must not keep advertising the version it had yesterday.
        var runner = new StubProcessRunner
        {
            Results = { ["xcodebuild"] = ProcessResult.Ok("Xcode 16.1\nBuild version 16B40") },
        };

        var prober = new CapabilityProber(
            runner,
            [new ProbeDefinition("xcode", "xcodebuild", ["-version"], CapabilityParsers.XcodeVersion)]);

        var before = await prober.ProbeAsync(AgentExecutionMode.Native, DateTimeOffset.UnixEpoch, TestContext.Current.CancellationToken);
        runner.Results["xcodebuild"] = ProcessResult.Ok("Xcode 16.2\nBuild version 16C5032a");
        var after = await prober.ProbeAsync(AgentExecutionMode.Native, DateTimeOffset.UnixEpoch.AddDays(1), TestContext.Current.CancellationToken);

        Assert.Contains("xcode:16.1", before.Capabilities);
        Assert.Contains("xcode:16.2", after.Capabilities);
        Assert.DoesNotContain("xcode:16.1", after.Capabilities);
        Assert.NotEqual(before.Hash, after.Hash);
    }

    [Fact]
    public void TheCapabilityHashIsStableAndOrderIndependent()
    {
        Assert.Equal(
            CapabilitySet.Fingerprint(["linux", "dotnet:10.0.100"]),
            CapabilitySet.Fingerprint(["dotnet:10.0.100", "linux"]));

        Assert.NotEqual(
            CapabilitySet.Fingerprint(["linux", "dotnet:10.0.100"]),
            CapabilitySet.Fingerprint(["linux", "dotnet:9.0.100"]));
    }

    [Theory]
    [InlineData("dotnet:10.0.100", "dotnet:10", true)]
    [InlineData("dotnet:10.0.100", "dotnet:10.0", true)]
    [InlineData("dotnet:10.0.100", "dotnet", true)]
    [InlineData("dotnet:10.0.100", "dotnet:10.0.100", true)]
    [InlineData("dotnet:10.0.100", "dotnet:9", false)]
    [InlineData("dotnet:10.0.100", "dotnet:100", false)]
    [InlineData("dotnet:10", "dotnet:10.0.100", false)]
    [InlineData("dotnet", "dotnet:10", false)]
    [InlineData("linux", "macos", false)]
    [InlineData("MacOS", "macos", true)]
    public void MatchesCapabilityVersionsOnSegmentBoundaries(string advertised, string required, bool expected)
    {
        Assert.Equal(expected, CapabilityMatcher.Covers(advertised, required));
    }

    [Fact]
    public void ReportsExactlyWhatAHostIsMissing()
    {
        string[] advertised = ["linux", "runner:docker", "dotnet:10.0.100", "node:22.11.0"];
        string[] required = ["macos", "xcode:16", "dotnet:10"];

        var missing = CapabilityMatcher.Missing(advertised, required);

        Assert.Equal(["macos", "xcode:16"], missing);
        Assert.False(CapabilityMatcher.Satisfies(advertised, required));
        Assert.True(CapabilityMatcher.Satisfies(advertised, ["linux", "dotnet:10"]));
    }
}
