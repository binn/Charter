using System.Buffers;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using Charter.Agent.Protocol;

namespace Charter.Agent.Transport;

/// <summary>
/// The real link: one outbound WebSocket to the control plane.
/// </summary>
/// <remarks>
/// The agent dials out; nothing listens on this host. That is what makes it work behind NAT, CGNAT
/// and a corporate firewall with no port forwarding, and it is why the local Docker socket never
/// needs to be reachable from anywhere (section 33.1).
/// </remarks>
public sealed class WebSocketTransport(ClientWebSocket socket) : IAgentTransport
{
    /// <summary>Frames are small. A control plane sending more than this is misbehaving.</summary>
    public const int MaxFrameBytes = 4 * 1024 * 1024;

    private readonly ClientWebSocket _socket = socket;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public int? CloseStatus => _socket.CloseStatus is null ? null : (int)_socket.CloseStatus.Value;

    public string? CloseDescription => _socket.CloseStatusDescription;

    public async Task SendAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var bytes = Encoding.UTF8.GetBytes(envelope.ToJson());
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        catch (WebSocketException exception)
        {
            throw new TransportClosedException("Sending to the control plane failed.", exception);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task<Envelope?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var message = new ArrayBufferWriter<byte>(16 * 1024);
        try
        {
            while (true)
            {
                ValueWebSocketReceiveResult result;
                try
                {
                    result = await _socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
                }
                catch (WebSocketException exception)
                {
                    throw new TransportClosedException("The control plane connection dropped.", exception);
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                message.Write(buffer.AsSpan(0, result.Count));
                if (message.WrittenCount > MaxFrameBytes)
                {
                    throw new TransportClosedException(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"The control plane sent a frame larger than {MaxFrameBytes} bytes."));
                }

                if (!result.EndOfMessage)
                {
                    continue;
                }

                var text = Encoding.UTF8.GetString(message.WrittenSpan);
                message.Clear();

                try
                {
                    var envelope = Envelope.FromJson(text);
                    if (envelope is not null)
                    {
                        return envelope;
                    }
                }
                catch (System.Text.Json.JsonException exception)
                {
                    throw new TransportClosedException(
                        "The control plane sent a frame this agent could not parse.", exception);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure, "charter-agent shutting down", timeout.Token);
            }
        }
        catch (WebSocketException)
        {
            // The link is already gone; nothing to close politely.
        }
        catch (OperationCanceledException)
        {
            // Closing handshake did not complete in time. Dispose below regardless.
        }

        _socket.Dispose();
        _sendGate.Dispose();
    }
}

/// <summary>
/// Dials out to <c>wss://…/api/agent/connect</c>, carrying the long-lived agent credential.
/// </summary>
/// <remarks>
/// The protocol version travels on the upgrade request as both a header and a query parameter, so a
/// control plane that cannot speak it can refuse the upgrade outright rather than accepting a socket
/// it will not be able to use. The in-band <c>hello</c>/<c>welcome</c> exchange then confirms it
/// (section 33.6).
/// </remarks>
public sealed class WebSocketTransportFactory(Uri server, string userAgent) : IAgentTransportFactory
{
    private readonly Uri _server = server;
    private readonly string _userAgent = userAgent;

    /// <summary>Turns the <c>--server</c> URL into the wss endpoint. http maps to ws for local dev.</summary>
    public static Uri ConnectUri(Uri server)
    {
        ArgumentNullException.ThrowIfNull(server);

        var builder = new UriBuilder(server)
        {
            Scheme = server.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = server.AbsolutePath.TrimEnd('/') + AgentProtocol.ConnectPath,
            Query = string.Create(
                CultureInfo.InvariantCulture,
                $"{AgentProtocol.VersionQueryParameter}={AgentProtocol.Version}"),
        };

        return builder.Uri;
    }

    public async Task<IAgentTransport> ConnectAsync(
        string agentToken,
        CancellationToken cancellationToken = default)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + agentToken);
        socket.Options.SetRequestHeader(AgentProtocol.VersionHeader, AgentProtocol.Version.ToString(CultureInfo.InvariantCulture));
        socket.Options.SetRequestHeader("User-Agent", _userAgent);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        try
        {
            await socket.ConnectAsync(ConnectUri(_server), cancellationToken);
        }
        catch (WebSocketException exception)
        {
            socket.Dispose();
            throw new TransportClosedException(
                $"Could not connect to {_server}. {exception.Message}", exception);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new WebSocketTransport(socket);
    }
}
