using System.Net;

namespace Charter.Models;

/// <summary>
/// A control-plane model call failed. Carries only redacted detail - section 20b.2 forbids a token
/// appearing in a log line, and an exception message is a log line waiting to happen.
/// </summary>
public class ModelClientException : Exception
{
    /// <summary>Creates an exception.</summary>
    public ModelClientException()
    {
    }

    /// <summary>Creates an exception.</summary>
    public ModelClientException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception.</summary>
    public ModelClientException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates an exception with provider context.</summary>
    /// <param name="message">A message that must not contain credential material.</param>
    /// <param name="provider">The provider that failed.</param>
    /// <param name="statusCode">The HTTP status, when there was one.</param>
    /// <param name="innerException">The underlying failure.</param>
    public ModelClientException(
        string message,
        ModelProvider provider,
        HttpStatusCode? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Provider = provider;
        StatusCode = statusCode;
    }

    /// <summary>The provider that failed.</summary>
    public ModelProvider Provider { get; init; }

    /// <summary>The HTTP status, when there was one.</summary>
    public HttpStatusCode? StatusCode { get; init; }
}

/// <summary>
/// The provider returned <c>429</c>. Section 20b.4: this exhausts the grant and records
/// <c>exhausted_until</c> from the reset header. It is explicitly not a signal to retry.
/// </summary>
public sealed class ModelRateLimitException : ModelClientException
{
    /// <summary>Creates an exception.</summary>
    public ModelRateLimitException()
        : base("The model provider rate limited the request.")
    {
    }

    /// <summary>Creates an exception.</summary>
    public ModelRateLimitException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception.</summary>
    public ModelRateLimitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates a rate-limit failure carrying the parsed reset.</summary>
    /// <param name="message">A message that must not contain credential material.</param>
    /// <param name="provider">The provider that rate limited.</param>
    /// <param name="reset">When the limit resets, as far as the provider said.</param>
    /// <param name="innerException">The underlying failure.</param>
    public ModelRateLimitException(
        string message,
        ModelProvider provider,
        RateLimitReset reset,
        Exception? innerException = null)
        : base(message, provider, HttpStatusCode.TooManyRequests, innerException)
    {
        Reset = reset;
    }

    /// <summary>When the limit resets, or an empty reset when the provider did not say.</summary>
    public RateLimitReset Reset { get; init; } = RateLimitReset.Unknown;

    /// <summary>
    /// The instant to record as <c>exhausted_until</c>, or <see langword="null"/> when unknown.
    /// </summary>
    public DateTimeOffset? ExhaustedUntil => Reset.ResetAt;
}

/// <summary>
/// The provider rejected the credential - <c>401</c>, or a <c>403</c> that is not a quota problem.
/// Section 20b.4 requires this be told apart from a rate limit with a reset: waiting will not fix it,
/// so the grant is marked <c>invalid</c> and skipped until a human intervenes.
/// </summary>
public sealed class ModelAuthenticationException : ModelClientException
{
    /// <summary>Creates an exception.</summary>
    public ModelAuthenticationException()
        : base("The model provider rejected the credential.")
    {
    }

    /// <summary>Creates an exception.</summary>
    public ModelAuthenticationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception.</summary>
    public ModelAuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates an authentication failure.</summary>
    /// <param name="message">A message that must not contain credential material.</param>
    /// <param name="provider">The provider that rejected the credential.</param>
    /// <param name="statusCode">The HTTP status.</param>
    /// <param name="innerException">The underlying failure.</param>
    public ModelAuthenticationException(
        string message,
        ModelProvider provider,
        HttpStatusCode statusCode,
        Exception? innerException = null)
        : base(message, provider, statusCode, innerException)
    {
    }
}
