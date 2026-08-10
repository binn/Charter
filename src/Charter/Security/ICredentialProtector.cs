namespace Charter.Security;

/// <summary>
/// Encrypts and decrypts credential material at rest (section 20b.2).
/// </summary>
/// <remarks>
/// <para>
/// The only thing standing between a Postgres backup and every linked model credential. Section
/// 20b.2 requires this to key off <c>CHARTER_CREDENTIAL_KEY</c> rather than
/// <c>CHARTER_SECRET_KEY</c>, so that rotating cookie signing - a routine, low-stakes operation -
/// does not invalidate every credential every user has linked.
/// </para>
/// <para>
/// The output is an opaque, self-describing envelope. Callers persist the bytes and hand them back
/// unmodified; nothing outside this namespace parses them.
/// </para>
/// </remarks>
public interface ICredentialProtector
{
    /// <summary>Encrypts <paramref name="plaintext"/> into a storable envelope.</summary>
    /// <param name="plaintext">The credential. Never empty - a blank credential is a bug, not a value.</param>
    /// <returns>The envelope to store in <c>credential_grants.secret_encrypted</c>.</returns>
    byte[] Protect(string plaintext);

    /// <summary>Decrypts an envelope produced by <see cref="Protect"/>.</summary>
    /// <param name="envelope">The stored bytes, unmodified.</param>
    /// <returns>The credential.</returns>
    /// <exception cref="CredentialProtectionException">
    /// The envelope is malformed, was written under a different key, or has been tampered with.
    /// Authenticated encryption means these are one failure, not three, and the exception carries no
    /// detail that would help an attacker tell them apart.
    /// </exception>
    string Unprotect(byte[] envelope);
}
