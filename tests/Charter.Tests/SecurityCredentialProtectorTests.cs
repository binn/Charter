using System.Security.Cryptography;
using System.Text;
using Charter.Configuration;
using Charter.Security;

namespace Charter.Tests;

/// <summary>
/// Section 20b.2: credentials are encrypted at rest under <c>CHARTER_CREDENTIAL_KEY</c>.
/// </summary>
/// <remarks>
/// These assert the properties that make the scheme worth having rather than the fact that it runs:
/// a fresh nonce every time, tampering that fails instead of decoding to something, and failure
/// messages that carry none of the material they failed on.
/// </remarks>
public class SecurityCredentialProtectorTests
{
    private const string Plaintext = "sk-ant-api03-not-a-real-key-0123456789abcdef";

    private static string RandomKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static AesGcmCredentialProtector CreateProtector(string? key = null)
        => new(new Secret(key ?? RandomKey()));

    [Fact]
    public void ARoundTripReturnsExactlyWhatWentIn()
    {
        var protector = CreateProtector();

        var envelope = protector.Protect(Plaintext);

        Assert.Equal(Plaintext, protector.Unprotect(envelope));
    }

    [Fact]
    public void UnicodeSurvivesTheRoundTrip()
    {
        var protector = CreateProtector();
        const string value = "clé-secrète-🔐-Ω";

        Assert.Equal(value, protector.Unprotect(protector.Protect(value)));
    }

    [Fact]
    public void TheEnvelopeIsSelfDescribing()
    {
        var protector = CreateProtector();

        var envelope = protector.Protect(Plaintext);

        // version | nonce | tag | ciphertext. The version byte is what lets a later build read these
        // rows without guessing at the scheme from their length.
        Assert.Equal(AesGcmCredentialProtector.CurrentVersion, envelope[0]);
        Assert.Equal(29, AesGcmCredentialProtector.HeaderSize);
        Assert.Equal(
            AesGcmCredentialProtector.HeaderSize + Encoding.UTF8.GetByteCount(Plaintext),
            envelope.Length);
    }

    [Fact]
    public void TheCiphertextDoesNotContainThePlaintext()
    {
        var protector = CreateProtector();

        var envelope = protector.Protect(Plaintext);

        var plainBytes = Encoding.UTF8.GetBytes(Plaintext);

        Assert.Equal(-1, envelope.AsSpan().IndexOf(plainBytes));
        Assert.DoesNotContain(Plaintext, Convert.ToBase64String(envelope), StringComparison.Ordinal);
    }

    [Fact]
    public void EncryptingTheSameValueTwiceProducesDifferentEnvelopes()
    {
        var protector = CreateProtector();

        var first = protector.Protect(Plaintext);
        var second = protector.Protect(Plaintext);

        // A nonce reused under one key in GCM leaks the XOR of the plaintexts and the authentication
        // subkey with it. Deterministic output would be the visible symptom of exactly that.
        Assert.NotEqual(first, second);

        var firstNonce = first[
            AesGcmCredentialProtector.NonceOffset..(AesGcmCredentialProtector.NonceOffset + AesGcmCredentialProtector.NonceSize)];
        var secondNonce = second[
            AesGcmCredentialProtector.NonceOffset..(AesGcmCredentialProtector.NonceOffset + AesGcmCredentialProtector.NonceSize)];

        Assert.NotEqual(firstNonce, secondNonce);
        Assert.Equal(Plaintext, protector.Unprotect(first));
        Assert.Equal(Plaintext, protector.Unprotect(second));
    }

    [Fact]
    public void NoNonceRepeatsAcrossManyEncryptions()
    {
        var protector = CreateProtector();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < 500; i++)
        {
            var envelope = protector.Protect(Plaintext);
            var nonce = Convert.ToBase64String(
                envelope,
                AesGcmCredentialProtector.NonceOffset,
                AesGcmCredentialProtector.NonceSize);

            Assert.True(seen.Add(nonce), "A nonce repeated under the same key.");
        }
    }

    [Theory]
    [InlineData(0)] // the version byte, bound as associated data
    [InlineData(AesGcmCredentialProtector.NonceOffset)]
    [InlineData(AesGcmCredentialProtector.TagOffset)]
    [InlineData(AesGcmCredentialProtector.HeaderSize)]
    public void TamperingWithAnyPartOfTheEnvelopeFailsRatherThanDecodingToGarbage(int offset)
    {
        var protector = CreateProtector();
        var envelope = protector.Protect(Plaintext);

        envelope[offset] ^= 0xFF;

        // Authenticated encryption: the point is that this throws rather than handing back a
        // plausible-looking value an attacker chose.
        Assert.Throws<CredentialProtectionException>(() => protector.Unprotect(envelope));
    }

    [Fact]
    public void AnAppendedByteFails()
    {
        var protector = CreateProtector();
        var envelope = protector.Protect(Plaintext);

        Assert.Throws<CredentialProtectionException>(() => protector.Unprotect([.. envelope, (byte)0x00]));
    }

    [Fact]
    public void ATruncatedEnvelopeFails()
    {
        var protector = CreateProtector();
        var envelope = protector.Protect(Plaintext);

        Assert.Throws<CredentialProtectionException>(
            () => protector.Unprotect(envelope[..AesGcmCredentialProtector.HeaderSize]));
        Assert.Throws<CredentialProtectionException>(() => protector.Unprotect([]));
    }

    [Fact]
    public void AnUnknownVersionSaysSoInsteadOfGuessing()
    {
        var protector = CreateProtector();
        var envelope = protector.Protect(Plaintext);

        envelope[0] = 0x7F;

        var failure = Assert.Throws<CredentialProtectionException>(() => protector.Unprotect(envelope));

        Assert.Contains("version", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnotherKeyCannotReadTheEnvelope()
    {
        var envelope = CreateProtector().Protect(Plaintext);

        var failure = Assert.Throws<CredentialProtectionException>(
            () => CreateProtector().Unprotect(envelope));

        Assert.Contains("CHARTER_CREDENTIAL_KEY", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RotatingTheCookieKeyDoesNotAffectStoredCredentials()
    {
        // The whole reason section 20b.2 insists on a separate key: the protector only ever reads
        // CHARTER_CREDENTIAL_KEY, so CHARTER_SECRET_KEY can change without touching a stored grant.
        var credentialKey = new Secret(RandomKey());

        var before = new AesGcmCredentialProtector(new KeyConfig
        {
            SecretKey = new Secret(RandomKey()),
            CredentialKey = credentialKey,
        });

        var after = new AesGcmCredentialProtector(new KeyConfig
        {
            SecretKey = new Secret(RandomKey()),
            CredentialKey = credentialKey,
        });

        Assert.Equal(Plaintext, after.Unprotect(before.Protect(Plaintext)));
    }

    [Fact]
    public void FailureNeverCarriesKeyCiphertextOrPlaintext()
    {
        var key = RandomKey();
        var envelope = CreateProtector(key).Protect(Plaintext);
        envelope[^1] ^= 0x01;

        var failure = Assert.Throws<CredentialProtectionException>(
            () => CreateProtector(key).Unprotect(envelope));

        var rendered = failure.ToString();

        Assert.DoesNotContain(Plaintext, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(key, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(envelope), rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProtectorRendersAsItsTypeNameAndNothingElse()
    {
        var key = RandomKey();
        var protector = CreateProtector(key);

        Assert.Equal(nameof(AesGcmCredentialProtector), protector.ToString());
        Assert.DoesNotContain(key, $"{protector}", StringComparison.Ordinal);
    }

    [Fact]
    public void ABlankCredentialIsRejectedRatherThanStored()
    {
        var protector = CreateProtector();

        Assert.Throws<ArgumentException>(() => protector.Protect(string.Empty));
        Assert.Throws<ArgumentNullException>(() => protector.Protect(null!));
        Assert.Throws<ArgumentNullException>(() => protector.Unprotect(null!));
    }

    [Fact]
    public void ABase64KeyIsMeasuredDecodedAndAUtf8KeyIsNot()
    {
        // Section 4.2's rule, and the same one KeyConfig validates with: base64 when it parses as
        // base64, UTF-8 otherwise. Both of these are 32 bytes derived.
        var base64Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        const string passphraseKey = "charter-credential-key-not-b64!!";

        Assert.Equal(32, new Secret(base64Key).EntropyBytes);
        Assert.Equal(32, new Secret(passphraseKey).EntropyBytes);

        var encoded = new AesGcmCredentialProtector(new Secret(base64Key));
        Assert.Equal(Plaintext, encoded.Unprotect(encoded.Protect(Plaintext)));

        var passphrase = new AesGcmCredentialProtector(new Secret(passphraseKey));
        Assert.Equal(Plaintext, passphrase.Unprotect(passphrase.Protect(Plaintext)));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(64)]
    public void AKeyThatIsNotExactlyThirtyTwoBytesFailsLoudly(int bytes)
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes));

        var failure = Assert.Throws<CredentialProtectionException>(
            () => new AesGcmCredentialProtector(new Secret(key)));

        Assert.Contains("CHARTER_CREDENTIAL_KEY", failure.Message, StringComparison.Ordinal);
        Assert.Contains("32 bytes", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(key, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyIsNeverSilentlyStretchedOrTruncated()
    {
        // A 64-byte key passes section 4.2's ">= 32 bytes" validation. Hashing or truncating it to
        // fit AES-256 would work, and would mean the operator could never tell which 32 bytes their
        // credentials were actually encrypted under.
        var overlong = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

        Assert.True(new Secret(overlong).EntropyBytes >= KeyConfig.MinimumEntropyBytes);
        Assert.Throws<CredentialProtectionException>(() => new AesGcmCredentialProtector(new Secret(overlong)));
    }
}
