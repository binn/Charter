using Charter.Agent;

namespace Charter.Tests;

/// <summary>Covers <c>charter-agent</c>'s argument surface (section 33.2, 33.3).</summary>
public class AgentCommandLineTests
{
    [Fact]
    public void ParsesAMinimalInvocation()
    {
        var result = CommandLine.Parse(["--server", "https://charter.example.com", "--token", "pair_abc"]);

        Assert.True(result.Ok);
        Assert.NotNull(result.Options);
        Assert.Equal(new Uri("https://charter.example.com"), result.Options.Server);
        Assert.Equal("pair_abc", result.Options.Token);
        Assert.Equal(AgentExecutionMode.Docker, result.Options.Mode);
        Assert.Equal(1, result.Options.Concurrency);
    }

    [Theory]
    [InlineData("docker", AgentExecutionMode.Docker)]
    [InlineData("native", AgentExecutionMode.Native)]
    [InlineData("NATIVE", AgentExecutionMode.Native)]
    public void ParsesExecutionMode(string raw, AgentExecutionMode expected)
    {
        var result = CommandLine.Parse(
            ["--server", "https://charter.example.com", "--token", "pair_abc", "--mode", raw]);

        Assert.True(result.Ok);
        Assert.Equal(expected, result.Options!.Mode);
    }

    [Fact]
    public void RejectsAnUnknownMode()
    {
        var result = CommandLine.Parse(
            ["--server", "https://charter.example.com", "--token", "pair_abc", "--mode", "kubernetes"]);

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
}
