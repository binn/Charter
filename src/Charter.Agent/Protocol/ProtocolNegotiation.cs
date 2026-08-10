using System.Globalization;

namespace Charter.Agent.Protocol;

/// <summary>
/// The outcome of agreeing a protocol version on connect (section 33.6).
/// </summary>
/// <param name="Ok">True when work may be claimed.</param>
/// <param name="AgreedVersion">The version both sides will use, when there is one.</param>
/// <param name="Message">
/// A plain-language explanation, present whenever <paramref name="Ok"/> is false. It names both
/// versions and says which side to upgrade — the whole point of section 33.6 is that a mismatch is
/// obvious now rather than a subtle failure three sessions later.
/// </param>
public sealed record ProtocolNegotiation(bool Ok, int AgreedVersion, string? Message)
{
    /// <summary>The agent's answer to a <see cref="WelcomePayload"/>.</summary>
    public static ProtocolNegotiation Evaluate(WelcomePayload welcome)
    {
        ArgumentNullException.ThrowIfNull(welcome);
        return Evaluate(welcome.ProtocolVersion, welcome.SupportedProtocolVersions);
    }

    /// <summary>The agent's answer to an explicit <see cref="ProtocolMismatchPayload"/>.</summary>
    public static ProtocolNegotiation Evaluate(ProtocolMismatchPayload mismatch)
    {
        ArgumentNullException.ThrowIfNull(mismatch);

        var evaluated = Evaluate(mismatch.ServerProtocolVersion, mismatch.SupportedProtocolVersions);
        return evaluated.Ok
            ? evaluated with
            {
                Ok = false,
                Message = mismatch.Message ??
                    "The control plane refused this protocol version. Upgrade charter-agent.",
            }
            : evaluated;
    }

    public static ProtocolNegotiation Evaluate(int serverVersion, IReadOnlyList<int>? serverSupported)
    {
        if (AgentProtocol.Supports(serverVersion))
        {
            return new ProtocolNegotiation(true, serverVersion, null);
        }

        var mine = string.Join(", ", AgentProtocol.SupportedVersions);
        var theirs = serverSupported is { Count: > 0 }
            ? string.Join(", ", serverSupported)
            : serverVersion.ToString(CultureInfo.InvariantCulture);

        var advice = serverVersion > AgentProtocol.Version
            ? "The control plane is newer than this agent. Upgrade charter-agent to a build that speaks " +
              $"protocol {serverVersion}."
            : "This agent is newer than the control plane. Upgrade the control plane, or run an agent " +
              $"build that still speaks protocol {serverVersion}.";

        return new ProtocolNegotiation(
            false,
            0,
            $"Protocol mismatch: this agent speaks {mine}, the control plane speaks {theirs}. {advice} " +
            "No work will be claimed until the versions agree.");
    }
}
