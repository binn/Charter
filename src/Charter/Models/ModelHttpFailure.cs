using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace Charter.Models;

/// <summary>
/// Turns a non-success provider response into the right exception.
/// </summary>
/// <remarks>
/// <para>
/// Section 20b.4 turns on one distinction: a <c>429</c> carrying a reset is a wait, and a <c>401</c>
/// is a person. Getting them the wrong way round either parks a session forever on a credential that
/// will never work, or hammers a provider that already said no.
/// </para>
/// <para>
/// The response body is never put in the exception message. Several providers echo part of the
/// request - occasionally including the key - back in their error payloads, and an exception message
/// ends up in the log pipeline (section 19).
/// </para>
/// </remarks>
internal static class ModelHttpFailure
{
    public static ModelClientException Create(
        ModelProvider provider,
        HttpStatusCode statusCode,
        HttpResponseHeaders? headers,
        string? body,
        ModelSecret secret,
        DateTimeOffset now,
        ILogger logger,
        Exception? innerException = null) =>
        Create(
            provider,
            statusCode,
            headers is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : RateLimitResetParser.Flatten(headers),
            body,
            secret,
            now,
            logger,
            innerException);

    public static ModelClientException Create(
        ModelProvider provider,
        HttpStatusCode statusCode,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        ModelSecret secret,
        DateTimeOffset now,
        ILogger logger,
        Exception? innerException = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(headers);

        // Logged at debug, redacted, so an operator can diagnose without the key ever being written.
        if (logger.IsEnabled(LogLevel.Debug) && !string.IsNullOrEmpty(body))
        {
            logger.LogDebug(
                "Provider {Provider} returned {StatusCode}: {Body}",
                provider,
                (int)statusCode,
                Truncate(ModelSecret.Redact(body, secret), 2048));
        }

        if (statusCode == HttpStatusCode.TooManyRequests)
        {
            var reset = RateLimitResetParser.Parse(headers, now);
            return new ModelRateLimitException(
                $"The {provider} endpoint rate limited the request.",
                provider,
                reset,
                innerException);
        }

        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired)
        {
            return new ModelAuthenticationException(
                $"The {provider} endpoint rejected the credential ({(int)statusCode}).",
                provider,
                statusCode,
                innerException);
        }

        if (statusCode == HttpStatusCode.Forbidden)
        {
            // A 403 is ambiguous: some providers use it for quota exhaustion. Treat it as a rate
            // limit only when the response actually carries reset information; otherwise it needs a
            // human, and pretending it will heal on its own would strand the session.
            var reset = RateLimitResetParser.Parse(headers, now);
            if (reset.IsKnown)
            {
                return new ModelRateLimitException(
                    $"The {provider} endpoint reported the quota exhausted.",
                    provider,
                    reset,
                    innerException);
            }

            return new ModelAuthenticationException(
                $"The {provider} endpoint rejected the credential (403).",
                provider,
                statusCode,
                innerException);
        }

        return new ModelClientException(
            $"The {provider} endpoint returned {(int)statusCode}.",
            provider,
            statusCode,
            innerException);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";
}
