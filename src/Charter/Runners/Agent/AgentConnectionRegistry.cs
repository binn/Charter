using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Charter.Runners.Agent;

/// <summary>
/// Every agent socket this replica currently holds.
/// </summary>
/// <remarks>
/// <para>
/// Process-local by construction, and that is correct rather than a limitation: a WebSocket lives in
/// exactly one process, so the replica holding it is the only one that can push a
/// <c>job.cancel</c> or a <c>revoked</c> frame down it. Everything durable — the credential verifier,
/// the capability set, the leases — lives in Postgres, so a replica that never saw the socket still
/// makes the right decisions (section 2.3). Revocation on another replica invalidates the credential
/// immediately and the agent is disconnected on its next frame rather than on this one.
/// </para>
/// <para>
/// One connection per agent. A second one wins and the first is closed with 4008, which is the only
/// safe resolution: two sockets for one agent would both claim under the same worker identity and
/// each would renew the other's leases.
/// </para>
/// </remarks>
public sealed class AgentConnectionRegistry
{
    private readonly ConcurrentDictionary<Guid, AgentConnection> _connections = new();
    private readonly ILogger<AgentConnectionRegistry> _logger;

    public AgentConnectionRegistry(ILogger<AgentConnectionRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>Agents connected to this replica right now.</summary>
    public IReadOnlyCollection<Guid> ConnectedAgentIds => [.. _connections.Keys];

    public bool IsConnected(Guid agentId) => _connections.ContainsKey(agentId);

    /// <summary>Takes the slot for an agent, displacing whatever held it (close code 4008).</summary>
    public void Register(AgentConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (_connections.TryGetValue(connection.AgentId, out var existing) && !ReferenceEquals(existing, connection))
        {
            _logger.LogInformation(
                "A second connection for agent {AgentId} took over; closing the first",
                connection.AgentId);

            existing.Replace();
        }

        _connections[connection.AgentId] = connection;
    }

    /// <summary>Releases the slot, but only if this connection still owns it.</summary>
    public void Unregister(AgentConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // The overload that compares the value is what stops a slow-closing predecessor from
        // evicting the connection that replaced it.
        _ = ((ICollection<KeyValuePair<Guid, AgentConnection>>)_connections)
            .Remove(new KeyValuePair<Guid, AgentConnection>(connection.AgentId, connection));
    }

    public AgentConnection? Find(Guid agentId) =>
        _connections.TryGetValue(agentId, out var connection) ? connection : null;

    /// <summary>Section 11: pushes a cancel to whichever agent holds the job, if it is connected here.</summary>
    public async Task<bool> CancelJobAsync(
        Guid agentId,
        Guid jobId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (Find(agentId) is not { } connection)
        {
            return false;
        }

        return await connection.CancelJobAsync(jobId, reason, cancellationToken);
    }

    /// <summary>
    /// Section 33.3: pushes <c>revoked</c> and closes with 4003.
    /// </summary>
    /// <returns>False when the agent is not connected to this replica — not a failure.</returns>
    public async Task<bool> RevokeAsync(Guid agentId, string reason, CancellationToken cancellationToken = default)
    {
        if (Find(agentId) is not { } connection)
        {
            return false;
        }

        await connection.RevokeAsync(reason, cancellationToken);
        return true;
    }
}
