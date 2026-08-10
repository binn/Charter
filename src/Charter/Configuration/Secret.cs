using System.Text;

namespace Charter.Configuration;

/// <summary>
/// A configuration value that must never reach a log, a UI, or a <c>ToString()</c>.
/// </summary>
/// <remarks>
/// <para>
/// Section 20b.2 requires that credentials are never logged and never returned to the UI. Relying on
/// nobody ever interpolating a configuration record is not a control; wrapping the value is. Every
/// secret in <see cref="CharterConfig"/> is a <see cref="Secret"/>, so the compiler-generated
/// <c>ToString()</c> of the surrounding record prints <see cref="Placeholder"/> rather than the value.
/// Reading the value is an explicit act: call <see cref="Reveal"/>.
/// </para>
/// <para>
/// <see cref="EntropyBytes"/> implements the measurement rule from section 4.2: base64-decode the
/// value when it parses as base64 and measure the decoded length, otherwise measure UTF-8 bytes.
/// </para>
/// </remarks>
public sealed class Secret : IEquatable<Secret>
{
    /// <summary>What a secret renders as in any string representation.</summary>
    public const string Placeholder = "***redacted***";

    private readonly string value;

    /// <summary>Wraps <paramref name="value"/>.</summary>
    public Secret(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        this.value = value;
    }

    /// <summary>Number of characters in the raw value. Safe to log; the value itself is not.</summary>
    public int Length => value.Length;

    /// <summary>
    /// Decoded entropy in bytes: the base64-decoded length when the value parses as base64, the
    /// UTF-8 byte count otherwise (section 4.2).
    /// </summary>
    public int EntropyBytes => MeasureEntropyBytes(value);

    /// <summary>True when the value parses as base64 and was therefore measured decoded.</summary>
    public bool IsBase64 => TryMeasureBase64(value, out _);

    /// <summary>Returns the wrapped value. The one place a secret becomes an ordinary string.</summary>
    public string Reveal() => value;

    /// <summary>Wraps <paramref name="value"/>, or returns <c>null</c> when it is null or blank.</summary>
    public static Secret? From(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : new Secret(value.Trim());

    /// <inheritdoc cref="EntropyBytes"/>
    public static int MeasureEntropyBytes(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return TryMeasureBase64(value, out var decoded) ? decoded : Encoding.UTF8.GetByteCount(value);
    }

    /// <summary>Decodes <paramref name="value"/> as base64 text, ignoring embedded whitespace.</summary>
    public static bool TryDecodeBase64Text(string value, out string decoded)
    {
        ArgumentNullException.ThrowIfNull(value);
        decoded = string.Empty;

        var compact = string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
        if (compact.Length < 4 || compact.Length % 4 != 0)
        {
            return false;
        }

        var buffer = new byte[compact.Length / 4 * 3];
        if (!Convert.TryFromBase64String(compact, buffer, out var written))
        {
            return false;
        }

        try
        {
            decoded = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(buffer, 0, written);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public bool Equals(Secret? other)
        => other is not null && string.Equals(value, other.value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as Secret);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(value);

    /// <summary>Always <see cref="Placeholder"/>. This is the whole point of the type.</summary>
    public override string ToString() => Placeholder;

    private static bool TryMeasureBase64(string value, out int decodedLength)
    {
        decodedLength = 0;
        if (value.Length < 4 || value.Length % 4 != 0)
        {
            return false;
        }

        var buffer = new byte[value.Length / 4 * 3];
        if (!Convert.TryFromBase64String(value, buffer, out var written))
        {
            return false;
        }

        decodedLength = written;
        return true;
    }
}
