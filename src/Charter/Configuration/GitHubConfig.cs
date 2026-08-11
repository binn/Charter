namespace Charter.Configuration;

/// <summary>
/// The GitHub App this instance authenticates as (section 4.2).
/// </summary>
public sealed record GitHubConfig
{
    /// <summary><c>GITHUB_APP_ID</c>.</summary>
    public required long AppId { get; init; }

    /// <summary>
    /// <c>GITHUB_APP_PRIVATE_KEY</c>, normalised to PEM text regardless of how it arrived.
    /// </summary>
    public required Secret PrivateKeyPem { get; init; }

    /// <summary><c>GITHUB_WEBHOOK_SECRET</c>. Verifies the HMAC on every delivery.</summary>
    public required Secret WebhookSecret { get; init; }

    /// <summary>True when the private key arrived base64-encoded and was decoded.</summary>
    public required bool PrivateKeyWasBase64 { get; init; }

    internal static GitHubConfig? Parse(EnvReader reader)
    {
        var appId = reader.RequiredId(
            "GITHUB_APP_ID",
            "the numeric App ID from the GitHub App's settings page");

        var rawKey = reader.Required(
            "GITHUB_APP_PRIVATE_KEY",
            "the GitHub App's private key, as PEM text or that PEM base64-encoded");

        var webhookSecret = reader.RequiredSecret(
            "GITHUB_WEBHOOK_SECRET",
            "the webhook secret configured on the GitHub App, used to verify delivery signatures");

        Secret? privateKey = null;
        var wasBase64 = false;

        if (rawKey is not null)
        {
            if (TryNormalisePem(rawKey, out var pem, out wasBase64))
            {
                privateKey = new Secret(pem);
            }
            else
            {
                reader.Error(
                    "GITHUB_APP_PRIVATE_KEY",
                    "GITHUB_APP_PRIVATE_KEY must be a PEM private key (a block beginning " +
                    "-----BEGIN RSA PRIVATE KEY-----) or that same PEM base64-encoded. The value is " +
                    "neither: it contains no PEM header and does not base64-decode to one. The usual " +
                    "cause is a key that was base64-encoded twice, or one pasted with the surrounding " +
                    "quotes included - decode it once and check the first line reads -----BEGIN.");
            }
        }

        if (appId == 0 || privateKey is null || webhookSecret is null)
        {
            return null;
        }

        return new GitHubConfig
        {
            AppId = appId,
            PrivateKeyPem = privateKey,
            WebhookSecret = webhookSecret,
            PrivateKeyWasBase64 = wasBase64,
        };
    }

    /// <summary>
    /// Accepts raw PEM or base64-encoded PEM and returns PEM text.
    /// </summary>
    /// <remarks>
    /// Both forms are common: GitHub hands out a <c>.pem</c> file, and every PaaS environment-variable
    /// editor that dislikes multi-line values pushes operators towards base64. Literal <c>\n</c>
    /// escapes are also normalised, because that is what a JSON-shaped secret store produces.
    /// </remarks>
    internal static bool TryNormalisePem(string raw, out string pem, out bool wasBase64)
    {
        ArgumentNullException.ThrowIfNull(raw);
        pem = string.Empty;
        wasBase64 = false;

        var value = raw.Trim();
        if (value.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            pem = value.Replace("\\n", "\n", StringComparison.Ordinal);
            return LooksLikeAPrivateKey(pem);
        }

        if (Secret.TryDecodeBase64Text(value, out var decoded) && LooksLikeAPrivateKey(decoded))
        {
            pem = decoded.Trim();
            wasBase64 = true;
            return true;
        }

        return false;
    }

    private static bool LooksLikeAPrivateKey(string candidate)
        => candidate.Contains("-----BEGIN", StringComparison.Ordinal)
           && candidate.Contains("PRIVATE KEY", StringComparison.Ordinal);
}
