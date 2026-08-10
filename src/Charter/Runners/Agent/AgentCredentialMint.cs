using System.Security.Cryptography;
using Charter.Auth.Providers;
using Charter.Configuration;

namespace Charter.Runners.Agent;

/// <summary>A freshly minted bearer token and the verifier to persist for it.</summary>
/// <param name="Token">Shown once. Never stored, never logged, never returned again.</param>
/// <param name="Hash">What goes in the database. Useless to whoever steals the database.</param>
public sealed record MintedAgentToken(string Token, string Hash)
{
    /// <summary>Keeps the token out of any interpolated string a record printer would build.</summary>
    public override string ToString() => "MintedAgentToken { redacted }";
}

/// <summary>
/// Mints and verifies the two bearer secrets of section 33.3: the pairing token and the long-lived
/// agent credential.
/// </summary>
/// <remarks>
/// <para>
/// Both tokens are <c>{prefix}{agentId:n}.{secret}</c>, where <c>secret</c> is 32 bytes from the
/// system CSPRNG. The identifier travelling in front of the secret is what makes verification a
/// single indexed row read rather than a table scan, which is the practical objection to hashing a
/// bearer token like a password: a salted PBKDF2 verifier cannot be looked up by value, so something
/// must name the row first.
/// </para>
/// <para>
/// The secret half is then verified with <see cref="ICharterPasswordHasher"/> — the same PBKDF2
/// construction, salt, and constant-time comparison Charter uses for passwords (section 21). A
/// bearer token that grants a runner the right to claim work and receive repository credentials is
/// worth exactly that treatment, and the cost lands on connect and on pairing, both of which are rare.
/// </para>
/// <para>
/// The identifier is not a secret and is safe to log; the secret half never leaves this type except
/// in the one response that hands it to the agent.
/// </para>
/// </remarks>
public sealed class AgentCredentialMint
{
    /// <summary>Prefix on a pairing token, so a leaked string is recognisable in a paste.</summary>
    public const string PairingTokenPrefix = "chpair_";

    /// <summary>Prefix on the long-lived agent credential.</summary>
    public const string AgentTokenPrefix = "chagt_";

    /// <summary>256 bits. Guessing is not a threat model at this size; leaking is.</summary>
    public const int SecretBytes = 32;

    private const char Separator = '.';

    private readonly ICharterPasswordHasher _hasher;

    public AgentCredentialMint(ICharterPasswordHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        _hasher = hasher;
    }

    /// <summary>Mints the single-use pairing token an admin hands to <c>charter-agent --token</c>.</summary>
    public MintedAgentToken IssuePairingToken(Guid agentId) => Issue(PairingTokenPrefix, agentId);

    /// <summary>Mints the long-lived credential returned by <c>POST /api/agent/pair</c>.</summary>
    public MintedAgentToken IssueAgentToken(Guid agentId) => Issue(AgentTokenPrefix, agentId);

    /// <summary>
    /// Splits a presented token into the agent it names and the secret to verify.
    /// </summary>
    /// <remarks>
    /// A malformed token is rejected here rather than being hashed, which is safe because the shape
    /// carries no secret information: a caller learns only that what they sent was not a token at
    /// all, which they already knew.
    /// </remarks>
    public static bool TryParse(string? presented, string expectedPrefix, out Guid agentId, out string secret)
    {
        agentId = Guid.Empty;
        secret = string.Empty;

        if (string.IsNullOrWhiteSpace(presented))
        {
            return false;
        }

        var trimmed = presented.Trim();
        if (!trimmed.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var body = trimmed[expectedPrefix.Length..];
        var separator = body.IndexOf(Separator, StringComparison.Ordinal);
        if (separator <= 0 || separator == body.Length - 1)
        {
            return false;
        }

        if (!Guid.TryParseExact(body[..separator], "N", out agentId))
        {
            return false;
        }

        secret = body[(separator + 1)..];
        return secret.Length > 0;
    }

    /// <summary>Constant-time verification of the secret half against a stored verifier.</summary>
    public bool Verify(string? storedHash, string secret)
    {
        if (string.IsNullOrEmpty(storedHash))
        {
            // No verifier means revoked or never paired. Spend the work anyway so "revoked" and
            // "wrong secret" cost the same, and neither becomes a timing oracle for the other.
            _hasher.SpendEquivalentWork();
            return false;
        }

        return _hasher.Verify(storedHash, new Secret(secret)) is
            PasswordVerification.Success or PasswordVerification.SuccessRehashNeeded;
    }

    /// <summary>Equal work for a token that named no row at all (section 21's enumeration rule).</summary>
    public void SpendEquivalentWork() => _hasher.SpendEquivalentWork();

    private MintedAgentToken Issue(string prefix, Guid agentId)
    {
        var secret = Base64Url(RandomNumberGenerator.GetBytes(SecretBytes));

        return new MintedAgentToken(
            $"{prefix}{agentId:n}{Separator}{secret}",
            _hasher.Hash(new Secret(secret)));
    }

    private static string Base64Url(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
