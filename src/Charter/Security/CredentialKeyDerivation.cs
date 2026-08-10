using System.Globalization;
using System.Text;
using Charter.Configuration;

namespace Charter.Security;

/// <summary>
/// Turns the configured <c>CHARTER_CREDENTIAL_KEY</c> into the 32 raw bytes AES-256 needs.
/// </summary>
/// <remarks>
/// <para>
/// The measurement rule is section 4.2's, and it is copied here on purpose rather than approximated:
/// base64-decode the value when it parses as base64, otherwise take its UTF-8 bytes. If this
/// derived material differently from the way <see cref="Secret.EntropyBytes"/> measures it, an
/// operator could pass startup validation and still be encrypting under something other than the
/// entropy they were told they had provided.
/// </para>
/// <para>
/// Validation accepts <em>at least</em> 32 bytes; AES-256 accepts <em>exactly</em> 32. The gap is
/// closed loudly here rather than by silently hashing or truncating, because both of those quietly
/// change which key a given configuration value means, and that is a decision an operator has to
/// make deliberately - by generating a 32-byte key - not one to discover after their stored
/// credentials stop decrypting.
/// </para>
/// </remarks>
internal static class CredentialKeyDerivation
{
    /// <summary>AES-256. Not negotiable, and not padded to.</summary>
    public const int RequiredKeyBytes = 32;

    /// <summary>The variable being derived from, named in every failure message.</summary>
    public const string Variable = "CHARTER_CREDENTIAL_KEY";

    /// <summary>Derives the AES key from a configured secret.</summary>
    /// <exception cref="CredentialProtectionException">
    /// The derived material is not exactly <see cref="RequiredKeyBytes"/> bytes. The message reports
    /// the length and how it was measured, never the value.
    /// </exception>
    public static byte[] Derive(Secret key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var value = key.Reveal();
        var isBase64 = TryDecodeBase64(value, out var material);

        material ??= Encoding.UTF8.GetBytes(value);

        if (material.Length == RequiredKeyBytes)
        {
            return material;
        }

        var measurement = isBase64
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"it base64-decodes to {material.Length} bytes")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"it is {material.Length} UTF-8 bytes");

        // No value, no prefix, no length of the raw string: only the derived byte count, which the
        // operator needs and an attacker learns nothing from.
        throw new CredentialProtectionException(
            $"{Variable} must derive to exactly {RequiredKeyBytes} bytes for AES-256, but {measurement}. " +
            "Generate one with `openssl rand -base64 32`.");
    }

    /// <summary>
    /// Section 4.2's base64 test, spelled the same way <see cref="Secret"/> spells it: at least four
    /// characters, a length that is a multiple of four, and a successful decode.
    /// </summary>
    private static bool TryDecodeBase64(string value, out byte[]? decoded)
    {
        decoded = null;

        if (value.Length < 4 || value.Length % 4 != 0)
        {
            return false;
        }

        var buffer = new byte[value.Length / 4 * 3];
        if (!Convert.TryFromBase64String(value, buffer, out var written))
        {
            return false;
        }

        decoded = buffer[..written];
        return true;
    }
}
