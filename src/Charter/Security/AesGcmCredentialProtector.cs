using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Charter.Configuration;

namespace Charter.Security;

/// <summary>
/// AES-256-GCM credential encryption at rest, keyed by <c>CHARTER_CREDENTIAL_KEY</c> (section 20b.2).
/// </summary>
/// <remarks>
/// <para>
/// GCM rather than CBC because a stored credential needs authentication as much as confidentiality:
/// a row an attacker can flip bits in is a row an attacker can steer, and an unauthenticated
/// ciphertext gives no way to tell a corrupted value from a forged one. The tag makes tampering a
/// hard failure instead of a plausible-looking decrypt.
/// </para>
/// <para>
/// <b>Nonces are random and never reused.</b> Reusing a nonce under the same key in GCM does not
/// merely weaken it - it leaks the XOR of the two plaintexts and, worse, exposes the authentication
/// subkey, which lets an attacker forge tags for that key from then on. So the nonce is drawn from
/// the CSPRNG per call and stored alongside the ciphertext, and nothing in this type ever derives a
/// nonce from a counter, a timestamp, or the plaintext.
/// </para>
/// <para>
/// The envelope is self-describing, laid out as:
/// </para>
/// <code>
/// offset  0        1 byte    version (currently 0x01)
/// offset  1       12 bytes   nonce
/// offset 13       16 bytes   authentication tag
/// offset 29        n bytes   ciphertext
/// </code>
/// <para>
/// The version byte is what makes a future key rotation or algorithm change a decision rather than a
/// guess: a v2 reader can tell a v1 row apart without a data migration and without inferring the
/// scheme from a length. It is also fed to GCM as associated data, so flipping it fails
/// authentication rather than being taken at face value.
/// </para>
/// </remarks>
public sealed class AesGcmCredentialProtector : ICredentialProtector
{
    /// <summary>The only envelope version written today.</summary>
    public const byte CurrentVersion = 0x01;

    /// <summary>Bytes of version prefix.</summary>
    public const int VersionSize = 1;

    /// <summary>GCM's standard 96-bit nonce - the size the construction is specified for.</summary>
    public const int NonceSize = 12;

    /// <summary>A full-length 128-bit tag. Truncating it buys 4 bytes a row and costs forgery resistance.</summary>
    public const int TagSize = 16;

    /// <summary>Where the nonce starts.</summary>
    public const int NonceOffset = VersionSize;

    /// <summary>Where the tag starts.</summary>
    public const int TagOffset = NonceOffset + NonceSize;

    /// <summary>Where the ciphertext starts, and therefore the fixed overhead per stored credential.</summary>
    public const int HeaderSize = TagOffset + TagSize;

    private readonly byte[] _key;

    /// <summary>Creates a protector from the instance keys.</summary>
    /// <exception cref="CredentialProtectionException">
    /// <c>CHARTER_CREDENTIAL_KEY</c> does not derive to exactly 32 bytes.
    /// </exception>
    public AesGcmCredentialProtector(KeyConfig keys)
        : this(CredentialKeyOf(keys))
    {
    }

    /// <summary>Creates a protector from the <c>CHARTER_CREDENTIAL_KEY</c> value.</summary>
    /// <exception cref="CredentialProtectionException">
    /// The key does not derive to exactly 32 bytes.
    /// </exception>
    public AesGcmCredentialProtector(Secret credentialKey)
    {
        _key = CredentialKeyDerivation.Derive(credentialKey);
    }

    /// <inheritdoc />
    public byte[] Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);

        try
        {
            var envelope = new byte[HeaderSize + plainBytes.Length];
            envelope[0] = CurrentVersion;

            // Fresh, from the CSPRNG, every single time. See the nonce note in the type remarks.
            RandomNumberGenerator.Fill(envelope.AsSpan(NonceOffset, NonceSize));

            using var aes = new AesGcm(_key, TagSize);
            aes.Encrypt(
                envelope.AsSpan(NonceOffset, NonceSize),
                plainBytes,
                envelope.AsSpan(HeaderSize),
                envelope.AsSpan(TagOffset, TagSize),
                envelope.AsSpan(0, VersionSize));

            return envelope;
        }
        finally
        {
            // The managed string is out of our hands; the copy we made is not.
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    /// <inheritdoc />
    public string Unprotect(byte[] envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.Length <= HeaderSize)
        {
            throw new CredentialProtectionException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The stored credential envelope is truncated: {envelope.Length} bytes, and a " +
                    $"valid envelope is more than {HeaderSize}."));
        }

        if (envelope[0] != CurrentVersion)
        {
            // A version this build does not know how to read. Said plainly, because it means the
            // rows were written by a different Charter, not that anything is corrupt.
            throw new CredentialProtectionException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The stored credential envelope is version {envelope[0]}, which this build " +
                    $"cannot read; it writes and reads version {CurrentVersion}."));
        }

        var plainBytes = new byte[envelope.Length - HeaderSize];

        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(
                envelope.AsSpan(NonceOffset, NonceSize),
                envelope.AsSpan(HeaderSize),
                envelope.AsSpan(TagOffset, TagSize),
                plainBytes,
                envelope.AsSpan(0, VersionSize));

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException ex)
        {
            // The tag did not verify. Wrong key, altered row, or a forgery attempt - indistinguishable
            // by design, and the message says nothing that would help tell them apart.
            throw new CredentialProtectionException(CredentialProtectionException.DefaultMessage, ex);
        }
        catch (ArgumentException ex)
        {
            throw new CredentialProtectionException(CredentialProtectionException.DefaultMessage, ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    /// <summary>
    /// The type name and nothing else. Spelled out so that a future field cannot quietly promote
    /// itself into a log line through a generated or inherited representation.
    /// </summary>
    public override string ToString() => nameof(AesGcmCredentialProtector);

    private static Secret CredentialKeyOf(KeyConfig keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return keys.CredentialKey;
    }
}
