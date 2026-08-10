using Charter.Agent.Protocol;

namespace Charter.Agent.Transport;

/// <summary>
/// The outbound link to the control plane (section 33.1).
/// </summary>
/// <remarks>
/// Deliberately narrow: send a frame, receive a frame, close. The daemon is written against this
/// interface so the whole connection loop — handshake, claiming, leases, reconnect — is testable
/// without a socket, a control plane, or a network.
/// </remarks>
public interface IAgentTransport : IAsyncDisposable
{
    Task SendAsync(Envelope envelope, CancellationToken cancellationToken = default);

    /// <summary>The next frame, or <c>null</c> once the peer has closed the connection.</summary>
    Task<Envelope?> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Close code the peer sent, once the connection has ended.</summary>
    int? CloseStatus { get; }

    string? CloseDescription { get; }
}

/// <summary>Opens a connection. A new transport per attempt, so reconnection is a fresh dial-out.</summary>
public interface IAgentTransportFactory
{
    Task<IAgentTransport> ConnectAsync(string agentToken, CancellationToken cancellationToken = default);
}

/// <summary>The peer went away. Always recoverable by dialling out again.</summary>
public sealed class TransportClosedException : Exception
{
    public TransportClosedException()
        : base("The connection to the control plane closed.")
    {
    }

    public TransportClosedException(string message)
        : base(message)
    {
    }

    public TransportClosedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
