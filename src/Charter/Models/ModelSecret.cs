using System.Diagnostics;

namespace Charter.Models;

/// <summary>
/// A credential secret that refuses to reveal itself except to the code that sends it upstream.
/// </summary>
/// <remarks>
/// <para>
/// Section 20b.2: never log a token. Passing raw strings around makes that a discipline problem -
/// one interpolated log line, one exception message, one record's generated <c>ToString</c> and the
/// key is in Seq forever. Wrapping the value moves it from discipline to type system: reading the
/// secret requires calling <see cref="Reveal"/>, which is easy to grep for and easy to review.
/// </para>
/// <para>
/// This is a redaction boundary, not encryption at rest. Stored credentials are encrypted with
/// <c>CHARTER_CREDENTIAL_KEY</c> before they ever reach this type.
/// </para>
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct ModelSecret : IEquatable<ModelSecret>
{
    private readonly string? _value;

    /// <summary>Wraps a secret value.</summary>
    public ModelSecret(string? value) => _value = value;

    /// <summary>The text substituted for a secret anywhere it might be displayed or logged.</summary>
    public const string RedactedPlaceholder = "[redacted]";

    /// <summary>Whether a value is present.</summary>
    public bool HasValue => !string.IsNullOrEmpty(_value);

    /// <summary>
    /// Returns the secret. Call this only at the point the value is written to an outbound request
    /// header; never to build a log message, an exception or a UI payload.
    /// </summary>
    public string Reveal() => _value ?? string.Empty;

    /// <summary>Always the redaction placeholder. Never the secret.</summary>
    public override string ToString() => RedactedPlaceholder;

    /// <inheritdoc />
    public bool Equals(ModelSecret other) => string.Equals(_value, other._value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ModelSecret other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value is null ? 0 : StringComparer.Ordinal.GetHashCode(_value);

    /// <summary>Equality.</summary>
    public static bool operator ==(ModelSecret left, ModelSecret right) => left.Equals(right);

    /// <summary>Inequality.</summary>
    public static bool operator !=(ModelSecret left, ModelSecret right) => !left.Equals(right);

    /// <summary>Wraps a secret value.</summary>
    public static ModelSecret FromString(string? value) => new(value);

    /// <summary>
    /// Removes any occurrence of a set of secrets from text bound for a log or an exception message.
    /// A defence in depth for provider error bodies, which occasionally echo the credential back.
    /// </summary>
    public static string Redact(string? text, params ModelSecret[] secrets)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        ArgumentNullException.ThrowIfNull(secrets);

        var result = text;
        foreach (var secret in secrets)
        {
            var value = secret._value;
            if (!string.IsNullOrEmpty(value))
            {
                result = result.Replace(value, RedactedPlaceholder, StringComparison.Ordinal);
            }
        }

        return result;
    }
}
