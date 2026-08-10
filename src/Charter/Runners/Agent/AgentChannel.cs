using System.Buffers;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;

namespace Charter.Runners.Agent;

/// <summary>
/// One agent's link, as the frames on it rather than the socket under it.
/// </summary>
/// <remarks>
/// The seam exists for the same reason the daemon has <c>IAgentTransport</c>: every interesting
/// property of section 33 — lease renewal, capability-filtered granting, protocol refusal, instant
/// revocation — is a property of the frame exchange, and none of them should need a listening port
/// and a real WebSocket handshake to test.
/// </remarks>
public interface IAgentChannel : IAsyncDisposable
{
    /// <summary>Writes one frame. Implementations serialise concurrent callers.</summary>
    Task SendAsync(Envelope envelope, CancellationToken cancellationToken = default);

    /// <summary>Reads the next frame, or null once the agent has gone away.</summary>
    Task<Envelope?> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the connection with one of the close codes of
    /// <see cref="AgentProtocol"/> — 4001 mismatch, 4003 revoked, 4008 replaced.
    /// </summary>
    Task CloseAsync(int closeCode, string reason, CancellationToken cancellationToken = default);
}

/// <summary>Raised when the link is gone. Never fatal to the process; always fatal to the loop.</summary>
public sealed class AgentChannelClosedException : Exception
{
    public AgentChannelClosedException(string message)
        : base(message)
    {
    }

    public AgentChannelClosedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AgentChannelClosedException()
        : base("The agent connection closed.")
    {
    }
}

/// <summary>The real link: the WebSocket the agent dialled out on (section 33.1).</summary>
public sealed class WebSocketAgentChannel : IAgentChannel
{
    /// <summary>
    /// Frames are small — the largest is a grant carrying a handful of jobs. An agent sending more
    /// than this is either broken or hostile, and reading it into memory first is how a single
    /// connection turns into an out-of-memory kill on a PaaS container.
    /// </summary>
    public const int MaxFrameBytes = 4 * 1024 * 1024;

    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public WebSocketAgentChannel(WebSocket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        _socket = socket;
    }

    /// <inheritdoc />
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
            throw new AgentChannelClosedException("Sending to the agent failed.", exception);
        }
        catch (ObjectDisposedException exception)
        {
            throw new AgentChannelClosedException("The agent connection was already disposed.", exception);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <inheritdoc />
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
                    throw new AgentChannelClosedException("The agent connection dropped.", exception);
                }
                catch (ObjectDisposedException exception)
                {
                    throw new AgentChannelClosedException("The agent connection was disposed.", exception);
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                message.Write(buffer.AsSpan(0, result.Count));
                if (message.WrittenCount > MaxFrameBytes)
                {
                    throw new AgentChannelClosedException(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"The agent sent a frame larger than {MaxFrameBytes} bytes."));
                }

                if (!result.EndOfMessage)
                {
                    continue;
                }

                var text = Encoding.UTF8.GetString(message.WrittenSpan);
                message.Clear();

                try
                {
                    if (Envelope.FromJson(text) is { } envelope)
                    {
                        return envelope;
                    }
                }
                catch (System.Text.Json.JsonException exception)
                {
                    throw new AgentChannelClosedException(
                        "The agent sent a frame the control plane could not parse.", exception);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc />
    public async Task CloseAsync(int closeCode, string reason, CancellationToken cancellationToken = default)
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));

                await _socket.CloseOutputAsync(
                    (WebSocketCloseStatus)closeCode,
                    Truncate(reason),
                    timeout.Token);
            }
        }
        catch (WebSocketException)
        {
            // Already gone. There is nothing to close politely.
        }
        catch (ObjectDisposedException)
        {
            // Same.
        }
        catch (OperationCanceledException)
        {
            // The closing handshake did not complete in time; the socket is disposed below anyway.
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        _sendGate.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>RFC 6455 caps the close reason at 123 bytes; a longer one fails the whole close.</summary>
    private static string Truncate(string reason)
    {
        if (string.IsNullOrEmpty(reason))
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetByteCount(reason);
        if (bytes <= 123)
        {
            return reason;
        }

        var trimmed = reason;
        while (Encoding.UTF8.GetByteCount(trimmed) > 120 && trimmed.Length > 1)
        {
            trimmed = trimmed[..(trimmed.Length - 1)];
        }

        return trimmed + "...";
    }
}
