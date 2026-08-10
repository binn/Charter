namespace Charter.Configuration;

/// <summary>
/// The two instance keys, which are deliberately not one key (sections 4.2, 20b.2).
/// </summary>
/// <remarks>
/// <c>CHARTER_SECRET_KEY</c> signs cookies and tokens; <c>CHARTER_CREDENTIAL_KEY</c> encrypts stored
/// credentials at rest. They are separate so that rotating cookie signing - a routine, low-stakes
/// operation - does not invalidate every credential every user has linked. If an operator sets both
/// to the same value that separation is gone, silently, so startup refuses it.
/// </remarks>
public sealed record KeyConfig
{
    /// <summary>Minimum decoded entropy for either key, in bytes (section 4.2).</summary>
    public const int MinimumEntropyBytes = 32;

    /// <summary>The command that produces an acceptable value.</summary>
    public const string GenerateHint = "generate one with `openssl rand -base64 32`";

    /// <summary><c>CHARTER_SECRET_KEY</c>. Cookie and token signing.</summary>
    public required Secret SecretKey { get; init; }

    /// <summary><c>CHARTER_CREDENTIAL_KEY</c>. Encrypts stored credentials at rest.</summary>
    public required Secret CredentialKey { get; init; }

    internal static KeyConfig? Parse(EnvReader reader)
    {
        var secretKey = reader.RequiredSecret(
            "CHARTER_SECRET_KEY",
            $"at least {MinimumEntropyBytes} bytes of entropy for cookie and token signing; {GenerateHint}");

        var credentialKey = reader.RequiredSecret(
            "CHARTER_CREDENTIAL_KEY",
            $"at least {MinimumEntropyBytes} bytes of entropy to encrypt stored credentials at rest, " +
            $"separate from CHARTER_SECRET_KEY; {GenerateHint}");

        CheckEntropy(reader, "CHARTER_SECRET_KEY", secretKey);
        CheckEntropy(reader, "CHARTER_CREDENTIAL_KEY", credentialKey);

        if (secretKey is not null && credentialKey is not null && secretKey.Equals(credentialKey))
        {
            reader.Error(
                "CHARTER_CREDENTIAL_KEY",
                "CHARTER_CREDENTIAL_KEY must differ from CHARTER_SECRET_KEY. They are separate keys so " +
                "that rotating cookie signing does not invalidate every stored credential (section 20b.2); " +
                $"{GenerateHint} for each.");
        }

        if (secretKey is null || credentialKey is null)
        {
            return null;
        }

        return new KeyConfig { SecretKey = secretKey, CredentialKey = credentialKey };
    }

    /// <summary>
    /// Section 4.2: at least 32 bytes of decoded entropy, not 32 characters. A value that parses as
    /// base64 is measured decoded - <c>openssl rand -base64 32</c> is 44 characters and 32 bytes -
    /// and anything else is measured as UTF-8.
    /// </summary>
    private static void CheckEntropy(EnvReader reader, string variable, Secret? key)
    {
        if (key is null || key.EntropyBytes >= MinimumEntropyBytes)
        {
            return;
        }

        var measurement = key.IsBase64
            ? $"the value base64-decodes to {key.EntropyBytes} bytes"
            : $"the value is {key.EntropyBytes} UTF-8 bytes";

        reader.Error(
            variable,
            $"{variable} must provide at least {MinimumEntropyBytes} bytes of entropy, not " +
            $"{MinimumEntropyBytes} characters - {measurement}. To fix, {GenerateHint}.");
    }
}
