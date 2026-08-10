using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace Charter.Notifications;

/// <summary>
/// A line-oriented connection to a mail server, with the one operation TLS negotiation needs.
/// </summary>
/// <remarks>
/// This exists so the SMTP conversation can be tested without a socket. The protocol - greeting,
/// EHLO, STARTTLS, AUTH, envelope, DATA - is where the bugs live, and it is exactly the part that a
/// test cannot reach if the client owns its own <c>TcpClient</c>. <see cref="SmtpConversation"/>
/// drives this interface and nothing else, so a test drives it with scripted replies and asserts on
/// the commands that came back.
/// </remarks>
internal interface ISmtpChannel : IAsyncDisposable
{
    /// <summary>True once the connection is encrypted, whether implicitly or via STARTTLS.</summary>
    bool IsSecure { get; }

    /// <summary>Reads one CRLF-terminated line, without the terminator.</summary>
    Task<string?> ReadLineAsync(CancellationToken cancellationToken);

    /// <summary>Writes one line, appending CRLF.</summary>
    Task WriteLineAsync(string line, CancellationToken cancellationToken);

    /// <summary>Writes an already-CRLF-terminated block verbatim.</summary>
    Task WriteRawAsync(string text, CancellationToken cancellationToken);

    /// <summary>Performs the TLS handshake in place, after a <c>STARTTLS</c> was accepted.</summary>
    Task UpgradeToTlsAsync(CancellationToken cancellationToken);
}

/// <summary>An <see cref="ISmtpChannel"/> over a real TCP connection.</summary>
internal sealed class NetworkSmtpChannel : ISmtpChannel
{
    private readonly TcpClient client;
    private readonly string host;

    private Stream stream;
    private StreamReader reader;
    private StreamWriter writer;

    private NetworkSmtpChannel(TcpClient client, Stream stream, string host, bool secure)
    {
        this.client = client;
        this.stream = stream;
        this.host = host;
        IsSecure = secure;

        // SMTP is ASCII on the wire until the message body, which we base64 before it gets here.
        reader = NewReader(stream);
        writer = NewWriter(stream);
    }

    public bool IsSecure { get; private set; }

    /// <summary>Connects, and completes the TLS handshake first when the mode is implicit.</summary>
    internal static async Task<NetworkSmtpChannel> ConnectAsync(
        string host,
        int port,
        bool implicitTls,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient();

        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(timeout);

            await client.ConnectAsync(host, port, connectTimeout.Token).ConfigureAwait(false);

            Stream stream = client.GetStream();

            if (implicitTls)
            {
                var secure = new SslStream(stream, leaveInnerStreamOpen: false);
                await secure.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = host,
                        EnabledSslProtocols = SslProtocols.None,
                    },
                    connectTimeout.Token).ConfigureAwait(false);

                stream = secure;
            }

            return new NetworkSmtpChannel(client, stream, host, implicitTls);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        => reader.ReadLineAsync(cancellationToken).AsTask();

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        await writer.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.WriteAsync("\r\n".AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteRawAsync(string text, CancellationToken cancellationToken)
    {
        await writer.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpgradeToTlsAsync(CancellationToken cancellationToken)
    {
        var secure = new SslStream(stream, leaveInnerStreamOpen: false);
        await secure.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.None,
            },
            cancellationToken).ConfigureAwait(false);

        stream = secure;
        reader = NewReader(secure);
        writer = NewWriter(secure);
        IsSecure = true;
    }

    public ValueTask DisposeAsync()
    {
        writer.Dispose();
        reader.Dispose();
        stream.Dispose();
        client.Dispose();

        return ValueTask.CompletedTask;
    }

    private static StreamReader NewReader(Stream stream)
        => new(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

    private static StreamWriter NewWriter(Stream stream)
        => new(stream, Encoding.ASCII, bufferSize: 4096, leaveOpen: true) { AutoFlush = false, NewLine = "\r\n" };
}
