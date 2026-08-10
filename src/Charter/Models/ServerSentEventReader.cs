using System.Runtime.CompilerServices;

namespace Charter.Models;

/// <summary>
/// Reads <c>text/event-stream</c> frames off a response body. Every provider Charter talks to streams
/// over SSE, so the framing is parsed once here rather than three times.
/// </summary>
internal static class ServerSentEventReader
{
    /// <summary>
    /// Yields the <c>data:</c> payload of each frame, in order, skipping comments and blank frames.
    /// Multi-line <c>data:</c> fields are joined with newlines per the SSE specification.
    /// </summary>
    public static async IAsyncEnumerable<string> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new StreamReader(stream);
        var data = new List<string>();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (data.Count > 0)
                {
                    yield return string.Join('\n', data);
                    data.Clear();
                }

                continue;
            }

            if (line[0] == ':')
            {
                // A comment, used by several providers as a keep-alive.
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var payload = line[5..];
                if (payload.Length > 0 && payload[0] == ' ')
                {
                    payload = payload[1..];
                }

                data.Add(payload);
            }

            // Other fields (event, id, retry) carry nothing Charter needs.
        }

        if (data.Count > 0)
        {
            yield return string.Join('\n', data);
        }
    }
}
