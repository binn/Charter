using Charter.Agent;

namespace Charter.Tests;

/// <summary>Covers <c>charter-agent</c>'s argument surface (sections 33.2, 33.3, 33.7).</summary>
public class AgentCommandLineTests
{
    private static readonly string[] Minimal = ["--server", "https://charter.example.com", "--token", "pair_abc"];

    [Fact]
    public void ParsesAMinimalInvocation()
    {
        var result = CommandLine.Parse(Minimal);

        Assert.True(result.Ok);
        Assert.NotNull(result.Options);
        Assert.Equal(new Uri("https://charter.example.com"), result.Options.Server);
        Assert.Equal("pair_abc", result.Options.Token);
        Assert.Equal(AgentExecutionMode.Docker, result.Options.Mode);
        Assert.Equal(1, result.Options.Concurrency);
        Assert.False(result.Options.AutoUpdate);
        Assert.False(result.Options.Verbose);
        Assert.Equal(TimeSpan.FromHours(24), result.Options.ReprobeInterval);
        Assert.Equal("charter-runner", result.Options.NativeUser);
        Assert.StartsWith(result.Options.StateDirectory, result.Options.WorkDirectory, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("docker", AgentExecutionMode.Docker)]
    [InlineData("native", AgentExecutionMode.Native)]
    [InlineData("NATIVE", AgentExecutionMode.Native)]
    public void ParsesExecutionMode(string raw, AgentExecutionMode expected)
    {
        var result = CommandLine.Parse([.. Minimal, "--mode", raw]);

        Assert.True(result.Ok);
        Assert.Equal(expected, result.Options!.Mode);
    }

    [Fact]
    public void RejectsAnUnknownMode()
    {
        var result = CommandLine.Parse([.. Minimal, "--mode", "kubernetes"]);

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, problem => problem.Contains("--mode", StringComparison.Ordinal));
    }

    [Fact]
    public void RequiresServerAndToken()
    {
        var result = CommandLine.Parse([]);

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, problem => problem.Contains("--server", StringComparison.Ordinal));
        Assert.Contains(result.Problems, problem => problem.Contains("--token", StringComparison.Ordinal));
    }

    [Fact]
    public void DoesNotRequireAPairingTokenOnceTheAgentHasPaired()
    {
        // A pairing token is single-use and short-TTL (section 33.3). Requiring one on every restart
        // would mean generating a fresh token every time the machine reboots.
        var result = CommandLine.Parse(
            ["--server", "https://charter.example.com"], credentialAlreadyStored: true);

        Assert.True(result.Ok);
        Assert.Null(result.Options!.Token);
    }

    [Theory]
    [InlineData("charter.example.com")]
    [InlineData("wss://charter.example.com")]
    public void RequiresAnAbsoluteHttpServerUrl(string server)
    {
        var result = CommandLine.Parse(["--server", server, "--token", "pair_abc"]);

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, problem => problem.Contains("--server", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsAFlagWithNoValue()
    {
        var result = CommandLine.Parse(["--server"]);

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, problem => problem.Contains("expects a value", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsAFlagFollowedByAnotherFlagInsteadOfAValue()
    {
        var result = CommandLine.Parse(["--server", "--token", "pair_abc"]);

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, problem => problem.Contains("--server expects a value", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsEveryProblemAtOnce()
    {
        // An operator editing a systemd unit should not have to restart once per mistake.
        var result = CommandLine.Parse(
            ["--server", "charter.example.com", "--mode", "podman", "--concurrency", "nine", "--frobnicate"]);

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Contains("--server", StringComparison.Ordinal));
        Assert.Contains(result.Problems, p => p.Contains("--token", StringComparison.Ordinal));
        Assert.Contains(result.Problems, p => p.Contains("--mode", StringComparison.Ordinal));
        Assert.Contains(result.Problems, p => p.Contains("--concurrency", StringComparison.Ordinal));
        Assert.Contains(result.Problems, p => p.Contains("--frobnicate", StringComparison.Ordinal));
        Assert.Equal(5, result.Problems.Count);
    }

    [Fact]
    public void RejectsARepeatedFlag()
    {
        var result = CommandLine.Parse([.. Minimal, "--mode", "docker", "--mode", "native"]);

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Contains("more than once", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("65")]
    [InlineData("many")]
    public void RejectsAnUnusableConcurrency(string raw)
    {
        var result = CommandLine.Parse([.. Minimal, "--concurrency", raw]);

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Contains("--concurrency", StringComparison.Ordinal));
    }

    [Fact]
    public void ParsesTheFullSurface()
    {
        var result = CommandLine.Parse(
        [
            "--server", "https://charter.example.com/charter",
            "--token", "pair_abc",
            "--mode", "native",
            "--name", "mac-mini-xcode",
            "--concurrency", "3",
            "--state-dir", Path.Combine(Path.GetTempPath(), "charter-agent-test"),
            "--work-dir", Path.Combine(Path.GetTempPath(), "charter-agent-work"),
            "--native-user", "builder",
            "--reprobe-hours", "6",
            "--auto-update",
            "--verbose",
        ]);

        Assert.True(result.Ok, string.Join("; ", result.Problems));
        var options = result.Options!;
        Assert.Equal(AgentExecutionMode.Native, options.Mode);
        Assert.Equal("mac-mini-xcode", options.Name);
        Assert.Equal(3, options.Concurrency);
        Assert.Equal("builder", options.NativeUser);
        Assert.Equal(TimeSpan.FromHours(6), options.ReprobeInterval);
        Assert.True(options.AutoUpdate);
        Assert.True(options.Verbose);
        Assert.False(options.RunsJobsAsAgentUser);
    }

    [Fact]
    public void NativeUserOnlyAppliesToNativeMode()
    {
        var result = CommandLine.Parse([.. Minimal, "--native-user", "builder"]);

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Contains("--native-user", StringComparison.Ordinal));
    }

    [Fact]
    public void RunningJobsAsTheAgentUserIsSomethingSomeoneHadToType()
    {
        // Section 33.2: the dedicated unprivileged account is the supported setup, and opting out of
        // it is spelled out rather than implied.
        var result = CommandLine.Parse([.. Minimal, "--mode", "native", "--native-user", AgentOptions.RunAsSelf]);

        Assert.True(result.Ok);
        Assert.True(result.Options!.RunsJobsAsAgentUser);
    }

    [Fact]
    public void UsageMentionsTheOutboundOnlyDesignAndBothModes()
    {
        Assert.Contains("dials out", CommandLine.Usage, StringComparison.Ordinal);
        Assert.Contains("--mode", CommandLine.Usage, StringComparison.Ordinal);
        Assert.Contains("native", CommandLine.Usage, StringComparison.Ordinal);
        Assert.Contains("docker", CommandLine.Usage, StringComparison.Ordinal);
    }
}
