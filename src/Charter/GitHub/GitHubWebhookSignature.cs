using System.Security.Cryptography;
using System.Text;

namespace Charter.GitHub;

/// <summary>
/// Verifies the HMAC GitHub puts on every webhook delivery.
/// </summary>
/// <remarks>
/// <para>
/// The webhook endpoint is the one route on a Charter instance that is reachable by anybody on the
/// internet and acts on what it is told. Every guarantee downstream of it — that an installation
/// event really came from GitHub, that a <c>check_suite</c> conclusion is real — rests on this
/// function, so it is deliberately small enough to read in one sitting.
/// </para>
/// <para>
/// Two properties matter and both are structural here. The comparison is constant time
/// (<see cref="CryptographicOperations.FixedTimeEquals"/>), so an attacker cannot walk the digest one
/// byte at a time off response latency. And a delivery is rejected <em>before</em> the payload is
/// parsed: an unsigned body never reaches a JSON reader, let alone the database.
/// </para>
/// </remarks>
public static class GitHubWebhookSignature
{
    /// <summary>The header GitHub signs with. The SHA-1 <c>X-Hub-Signature</c> is not accepted.</summary>
    public const string HeaderName = "X-Hub-Signature-256";

    /// <summary>The header carrying the event name.</summary>
    public const string EventHeaderName = "X-GitHub-Event";

    /// <summary>The header carrying the delivery GUID, for de-duplication and support.</summary>
    public const string DeliveryHeaderName = "X-GitHub-Delivery";

    private const string Prefix = "sha256=";
    private const int DigestBytes = 32;

    /// <summary>Computes the header value GitHub would send for this payload.</summary>
    public static string Compute(string secret, ReadOnlySpan<byte> payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        Span<byte> digest = stackalloc byte[DigestBytes];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload, digest);

        return Prefix + Convert.ToHexStringLower(digest);
    }

    /// <summary>
    /// Whether <paramref name="signatureHeader"/> is the right HMAC for <paramref name="payload"/>.
    /// </summary>
    /// <remarks>
    /// A missing header, an empty header, a header without the <c>sha256=</c> prefix and a header
    /// whose hex is the wrong length are all the same answer: <see langword="false"/>. There is no
    /// "unsigned deliveries are allowed in development" branch, because that branch is how a
    /// production instance ends up accepting them.
    /// </remarks>
    public static bool IsValid(string secret, ReadOnlySpan<byte> payload, string? signatureHeader)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            return false;
        }

        var header = signatureHeader.Trim();

        if (!header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hex = header.AsSpan(Prefix.Length);

        if (hex.Length != DigestBytes * 2)
        {
            return false;
        }

        Span<byte> presented = stackalloc byte[DigestBytes];

        if (!TryParseHex(hex, presented))
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[DigestBytes];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload, expected);

        return CryptographicOperations.FixedTimeEquals(expected, presented);
    }

    /// <summary>
    /// Hex-decodes without throwing, so a malformed header is an ordinary rejection rather than an
    /// exception that has to be caught at the endpoint.
    /// </summary>
    private static bool TryParseHex(ReadOnlySpan<char> hex, Span<byte> destination)
    {
        for (var index = 0; index < destination.Length; index++)
        {
            if (!TryParseNibble(hex[index * 2], out var high) || !TryParseNibble(hex[(index * 2) + 1], out var low))
            {
                return false;
            }

            destination[index] = (byte)((high << 4) | low);
        }

        return true;
    }

    private static bool TryParseNibble(char character, out int value)
    {
        value = character switch
        {
            >= '0' and <= '9' => character - '0',
            >= 'a' and <= 'f' => character - 'a' + 10,
            >= 'A' and <= 'F' => character - 'A' + 10,
            _ => -1,
        };

        return value >= 0;
    }
}
