using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Charter.Agent.Execution;

/// <summary>Container creation request, in the Docker Engine API's own casing.</summary>
public sealed record DockerCreateContainer
{
    [JsonPropertyName("Image")]
    public required string Image { get; init; }

    [JsonPropertyName("Cmd")]
    public IReadOnlyList<string>? Cmd { get; init; }

    /// <summary><c>KEY=value</c> pairs. Per-job secrets live here and nowhere else.</summary>
    [JsonPropertyName("Env")]
    public IReadOnlyList<string>? Env { get; init; }

    [JsonPropertyName("WorkingDir")]
    public string? WorkingDir { get; init; }

    [JsonPropertyName("Labels")]
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    [JsonPropertyName("HostConfig")]
    public DockerHostConfig? HostConfig { get; init; }
}

public sealed record DockerHostConfig
{
    /// <summary>False: the agent removes the container itself, after it has read the exit code.</summary>
    [JsonPropertyName("AutoRemove")]
    public bool AutoRemove { get; init; }

    /// <summary>Named volumes for the per-repository caches and git mirror (section 32.3).</summary>
    [JsonPropertyName("Binds")]
    public IReadOnlyList<string>? Binds { get; init; }

    /// <summary>An init process, so a runaway toolchain does not leave zombies behind.</summary>
    [JsonPropertyName("Init")]
    public bool Init { get; init; } = true;
}

public sealed record DockerCreateResponse
{
    [JsonPropertyName("Id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("Warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record DockerWaitResponse
{
    [JsonPropertyName("StatusCode")]
    public int StatusCode { get; init; }
}

/// <summary>
/// The Docker Engine API over the <b>local</b> socket (section 33.1).
/// </summary>
/// <remarks>
/// The socket never leaves this host. Compare exposing the Docker API over TCP: even with mTLS, a
/// network-reachable Docker daemon is root-equivalent access to the machine and a permanent target.
/// The agent's outbound-only design exists precisely so nobody has to do that.
/// </remarks>
public sealed class DockerSocketClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public DockerSocketClient(string socketPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);

        SocketPath = socketPath.StartsWith("unix://", StringComparison.Ordinal)
            ? socketPath["unix://".Length..]
            : socketPath;

        var path = SocketPath;
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };

        _httpClient = new HttpClient(handler)
        {
            // The host name is meaningless over a unix socket; the daemon ignores it.
            BaseAddress = new Uri("http://localhost/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public string SocketPath { get; }

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync(new Uri("/_ping", UriKind.Relative), cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public async Task<string> CreateContainerAsync(
        string name,
        DockerCreateContainer request,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            new Uri("/containers/create?name=" + Uri.EscapeDataString(name), UriKind.Relative),
            request,
            cancellationToken);

        await ThrowIfFailedAsync(response, "create the container", cancellationToken);

        var created = await response.Content.ReadFromJsonAsync<DockerCreateResponse>(cancellationToken);
        return created?.Id ?? throw new DockerApiException("The Docker daemon created a container with no id.");
    }

    public async Task StartContainerAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync(
            new Uri($"/containers/{id}/start", UriKind.Relative), content: null, cancellationToken);

        await ThrowIfFailedAsync(response, "start the container", cancellationToken);
    }

    public async Task<int> WaitAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync(
            new Uri($"/containers/{id}/wait", UriKind.Relative), content: null, cancellationToken);

        await ThrowIfFailedAsync(response, "wait for the container", cancellationToken);

        var result = await response.Content.ReadFromJsonAsync<DockerWaitResponse>(cancellationToken);
        return result?.StatusCode ?? -1;
    }

    /// <summary>Follows the container's output, de-multiplexing the daemon's stdout/stderr framing.</summary>
    public async Task FollowLogsAsync(
        string id,
        Action<string, string> onLine,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onLine);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"/containers/{id}/logs?follow=1&stdout=1&stderr=1&timestamps=0", UriKind.Relative));

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        await ThrowIfFailedAsync(response, "read the container's logs", cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await ReadFramedAsync(stream, onLine, cancellationToken);
    }

    /// <summary>
    /// Docker frames non-TTY output as an 8-byte header - stream type, three zero bytes, then a
    /// big-endian length - followed by that many payload bytes.
    /// </summary>
    internal static async Task ReadFramedAsync(
        Stream stream,
        Action<string, string> onLine,
        CancellationToken cancellationToken)
    {
        var header = new byte[8];
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!await ReadExactlyAsync(stream, header, cancellationToken))
            {
                return;
            }

            var kind = header[0] == 2 ? "stderr" : "stdout";
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4));
            if (length <= 0)
            {
                continue;
            }

            var payload = new byte[length];
            if (!await ReadExactlyAsync(stream, payload, cancellationToken))
            {
                return;
            }

            foreach (var line in Encoding.UTF8.GetString(payload).Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                if (trimmed.Length > 0)
                {
                    onLine(kind, trimmed);
                }
            }
        }
    }

    public async Task RemoveContainerAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync(
            new Uri($"/containers/{id}?force=1&v=1", UriKind.Relative), cancellationToken);

        // A container that is already gone is the outcome we wanted.
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            await ThrowIfFailedAsync(response, "remove the container", cancellationToken);
        }
    }

    public async Task StopContainerAsync(string id, int timeoutSeconds, CancellationToken cancellationToken = default)
    {
        var path = string.Create(CultureInfo.InvariantCulture, $"/containers/{id}/stop?t={timeoutSeconds}");
        using var response = await _httpClient.PostAsync(new Uri(path, UriKind.Relative), null, cancellationToken);

        if (!response.IsSuccessStatusCode &&
            response.StatusCode is not (System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.NotModified))
        {
            await ThrowIfFailedAsync(response, "stop the container", cancellationToken);
        }
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (chunk == 0)
            {
                return false;
            }

            read += chunk;
        }

        return true;
    }

    private static async Task ThrowIfFailedAsync(
        HttpResponseMessage response,
        string what,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        string? message = null;
        try
        {
            message = JsonDocument.Parse(body).RootElement.TryGetProperty("message", out var property)
                ? property.GetString()
                : null;
        }
        catch (JsonException)
        {
            // Not JSON. The status code alone will have to do.
        }

        throw new DockerApiException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Could not {what}: Docker returned {(int)response.StatusCode}. {message}").TrimEnd());
    }

    public void Dispose() => _httpClient.Dispose();
}

public sealed class DockerApiException : Exception
{
    public DockerApiException()
    {
    }

    public DockerApiException(string message)
        : base(message)
    {
    }

    public DockerApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
