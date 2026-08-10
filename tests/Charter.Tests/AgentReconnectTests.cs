using Charter.Agent.Protocol;
using Charter.Agent.Transport;

namespace Charter.Tests;

/// <summary>
/// Reconnection (section 33.1): a flaky home connection must not need babysitting, and a control
/// plane restart must not be met by every agent reconnecting on the same millisecond.
/// </summary>
public class AgentReconnectTests
{
    [Fact]
    public void TheDelayCeilingDoublesPerConsecutiveFailure()
    {
        var policy = new ReconnectPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2));

        Assert.Equal(TimeSpan.FromSeconds(1), policy.Ceiling(0));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.Ceiling(1));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.Ceiling(2));
        Assert.Equal(TimeSpan.FromSeconds(64), policy.Ceiling(6));
    }

    [Fact]
    public void TheCeilingIsCapped()
    {
        var policy = new ReconnectPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2));

        Assert.Equal(TimeSpan.FromMinutes(2), policy.Ceiling(20));
        Assert.Equal(TimeSpan.FromMinutes(2), policy.Ceiling(2_000));
    }

    [Fact]
    public void EveryDelayIsAFullJitterDrawBelowItsCeiling()
    {
        // Full jitter, not a fixed backoff: agents that dropped together must not return together.
        var draws = new Queue<double>([0.0, 1.0, 0.5, 0.25]);
        var policy = new ReconnectPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2), jitter: draws.Dequeue);

        Assert.Equal(TimeSpan.Zero, policy.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(2), policy.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(2), policy.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(2), policy.NextDelay());
    }

    [Fact]
    public void AJitterSourceThatMisbehavesCannotProduceANegativeOrOversizedDelay()
    {
        var policy = new ReconnectPolicy(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(2), jitter: () => 7.5);
        Assert.Equal(TimeSpan.FromSeconds(10), policy.NextDelay());

        var negative = new ReconnectPolicy(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(2), jitter: () => -3);
        Assert.Equal(TimeSpan.Zero, negative.NextDelay());
    }

    [Fact]
    public void ASuccessfulConnectionResetsTheBackoff()
    {
        // A link that flaps once an hour must never accumulate a long delay.
        var policy = new ReconnectPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2), jitter: () => 1.0);
        policy.NextDelay();
        policy.NextDelay();
        policy.NextDelay();
        Assert.Equal(3, policy.Attempt);

        policy.Reset();

        Assert.Equal(0, policy.Attempt);
        Assert.Equal(TimeSpan.FromSeconds(1), policy.NextDelay());
    }

    [Fact]
    public void TheDefaultPolicyIsBoundedAndImmediateAtFirst()
    {
        var policy = new ReconnectPolicy();

        Assert.Equal(TimeSpan.FromSeconds(1), policy.Initial);
        Assert.Equal(TimeSpan.FromMinutes(2), policy.Maximum);
        Assert.InRange(policy.NextDelay(), TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData("https://charter.example.com", "wss://charter.example.com/api/agent/connect?protocol=1")]
    [InlineData("https://charter.example.com/", "wss://charter.example.com/api/agent/connect?protocol=1")]
    [InlineData("http://localhost:5080", "ws://localhost:5080/api/agent/connect?protocol=1")]
    [InlineData("https://example.com/charter", "wss://example.com/charter/api/agent/connect?protocol=1")]
    public void TheAgentDialsOutToTheWebSocketEndpointCarryingItsProtocolVersion(string server, string expected)
    {
        Assert.Equal(new Uri(expected), WebSocketTransportFactory.ConnectUri(new Uri(server)));
    }

    [Fact]
    public void TheProtocolVersionTravelsOnTheUpgradeAsWellAsInBand()
    {
        // A control plane that cannot speak this version can then refuse the upgrade outright,
        // rather than accepting a socket it will not be able to use.
        Assert.Equal("Charter-Agent-Protocol", AgentProtocol.VersionHeader);
        Assert.Equal("protocol", AgentProtocol.VersionQueryParameter);
        Assert.Contains(AgentProtocol.Version, AgentProtocol.SupportedVersions);
    }
}
