namespace Charter.Security;

/// <summary>
/// A stored credential could not be protected or unprotected.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately typed, and deliberately opaque. Section 20b.2 forbids a token ever reaching a log,
/// and an exception message is a log line waiting to happen: it is caught, wrapped, and printed
/// with its whole chain by anything from a request logger to a crash reporter. So nothing that
/// constructs this exception is allowed to put ciphertext, key material or plaintext into
/// <see cref="Exception.Message"/> - only what went wrong and which knob fixes it.
/// </para>
/// <para>
/// A failure here means one of three operator-visible things: <c>CHARTER_CREDENTIAL_KEY</c> is the
/// wrong length, it has been rotated without re-encrypting the stored grants, or a row has been
/// tampered with. None of those are distinguishable from ciphertext alone - an authentication-tag
/// mismatch is a mismatch - so the message says so rather than guessing.
/// </para>
/// </remarks>
public sealed class CredentialProtectionException : Exception
{
    /// <summary>Creates an exception with the default, non-leaking message.</summary>
    public CredentialProtectionException()
        : base(DefaultMessage)
    {
    }

    /// <summary>Creates an exception. <paramref name="message"/> must carry no credential material.</summary>
    public CredentialProtectionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception. <paramref name="message"/> must carry no credential material.</summary>
    public CredentialProtectionException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>What a decryption failure says, and all it says.</summary>
    public const string DefaultMessage =
        "The stored credential could not be decrypted. Either CHARTER_CREDENTIAL_KEY is not the key " +
        "this credential was encrypted with, or the stored envelope has been altered.";
}
