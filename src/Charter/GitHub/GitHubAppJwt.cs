using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Charter.GitHub;

/// <summary>
/// The RS256 JSON Web Token a GitHub App presents to prove it is itself.
/// </summary>
/// <remarks>
/// <para>
/// Hand-rolled against <see cref="System.Security.Cryptography"/> rather than pulling in a JWT
/// library: the token has three claims and one algorithm, and section 16.1 counts every dependency
/// added for convenience as attack surface Charter chose to take on.
/// </para>
/// <para>
/// This token authenticates the <em>app</em>, and is only ever sent to
/// <c>/app/installations/{id}/access_tokens</c>. Nothing outside this assembly receives it: what
/// leaves Charter is a single-repository installation token (section 7.4).
/// </para>
/// </remarks>
public static class GitHubAppJwt
{
    /// <summary>GitHub refuses a JWT claiming to live longer than this.</summary>
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(10);

    private const string Header = """{"alg":"RS256","typ":"JWT"}""";

    /// <summary>
    /// Signs a JWT for <paramref name="appId"/> with the App's PEM private key.
    /// </summary>
    /// <param name="appId"><c>GITHUB_APP_ID</c>.</param>
    /// <param name="privateKeyPem">
    /// <c>GITHUB_APP_PRIVATE_KEY</c>, already normalised to PEM text by
    /// <see cref="Charter.Configuration.GitHubConfig"/>. PKCS#1 (<c>BEGIN RSA PRIVATE KEY</c>) and
    /// PKCS#8 (<c>BEGIN PRIVATE KEY</c>) both work; GitHub hands out the former.
    /// </param>
    /// <param name="now">The current instant.</param>
    /// <param name="lifetime">How long the token claims to live. Clamped to ten minutes.</param>
    /// <param name="backdate">
    /// How far <c>iat</c> is moved into the past to absorb clock skew. GitHub rejects a token issued
    /// in the future outright, and a container's clock is not GitHub's.
    /// </param>
    public static string Create(
        long appId,
        string privateKeyPem,
        DateTimeOffset now,
        TimeSpan? lifetime = null,
        TimeSpan? backdate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(appId);

        var live = lifetime ?? TimeSpan.FromMinutes(9);
        if (live > MaximumLifetime)
        {
            live = MaximumLifetime;
        }

        var issuedAt = now - (backdate ?? TimeSpan.FromSeconds(60));
        var expiresAt = now + live;

        var payload = string.Create(
            CultureInfo.InvariantCulture,
            $$"""{"iat":{{issuedAt.ToUnixTimeSeconds()}},"exp":{{expiresAt.ToUnixTimeSeconds()}},"iss":"{{appId}}"}""");

        var signingInput = $"{Base64Url(Encoding.UTF8.GetBytes(Header))}.{Base64Url(Encoding.UTF8.GetBytes(payload))}";

        using var rsa = RSA.Create();

        try
        {
            rsa.ImportFromPem(privateKeyPem);
        }
        catch (ArgumentException ex)
        {
            throw new GitHubApiException(
                "GITHUB_APP_PRIVATE_KEY is not a PEM key this runtime can import. Re-download the "
                + "private key from the GitHub App's settings page.",
                ex);
        }
        catch (CryptographicException ex)
        {
            throw new GitHubApiException(
                "GITHUB_APP_PRIVATE_KEY could not be used to sign. The PEM parsed but the key material "
                + "is not a usable RSA private key.",
                ex);
        }

        var signature = rsa.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{Base64Url(signature)}";
    }

    /// <summary>Base64url without padding, which is what a JWT segment is.</summary>
    internal static string Base64Url(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
