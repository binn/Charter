using System.Collections.Concurrent;

namespace Charter.Agent.Logging;

/// <summary>
/// Belt and braces for section 33.5: no secret value ever reaches a log line or a streamed job event.
/// </summary>
/// <remarks>
/// The first line of defence is structural — secret-bearing records override <c>ToString</c> and the
/// agent never formats one into a message. This is the second: every value the agent is handed is
/// registered here, and every line written passes through <see cref="Scrub"/>. Child-process output
/// is the case that motivates it, since a job's own tooling may echo a token the agent gave it.
/// <para>
/// Short values are not registered. Redacting a two-character string would blank out half of every
/// line for no security benefit, and a secret that short is not one.
/// </para>
/// </remarks>
public sealed class SecretScrubber
{
    /// <summary>Values shorter than this are ignored — see the remarks.</summary>
    public const int MinimumLength = 8;

    public const string Placeholder = "[redacted]";

    private readonly ConcurrentDictionary<string, byte> _secrets = new(StringComparer.Ordinal);

    /// <summary>How many distinct values are being scrubbed. For diagnostics only.</summary>
    public int Count => _secrets.Count;

    public void Register(string? secret)
    {
        if (!string.IsNullOrEmpty(secret) && secret.Length >= MinimumLength)
        {
            _secrets.TryAdd(secret, 0);
        }
    }

    public void Register(IEnumerable<string>? secrets)
    {
        if (secrets is null)
        {
            return;
        }

        foreach (var secret in secrets)
        {
            Register(secret);
        }
    }

    /// <summary>Forgets a value, for when a job ends and its short-TTL credentials die with it.</summary>
    public void Forget(string? secret)
    {
        if (!string.IsNullOrEmpty(secret))
        {
            _secrets.TryRemove(secret, out _);
        }
    }

    public void Forget(IEnumerable<string>? secrets)
    {
        if (secrets is null)
        {
            return;
        }

        foreach (var secret in secrets)
        {
            Forget(secret);
        }
    }

    /// <summary>Replaces every registered value in <paramref name="text"/> with a placeholder.</summary>
    public string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text) || _secrets.IsEmpty)
        {
            return text ?? string.Empty;
        }

        var scrubbed = text;
        foreach (var secret in _secrets.Keys)
        {
            if (scrubbed.Contains(secret, StringComparison.Ordinal))
            {
                scrubbed = scrubbed.Replace(secret, Placeholder, StringComparison.Ordinal);
            }
        }

        return scrubbed;
    }

    /// <summary>
    /// A stable, non-reversible fingerprint for a token, safe to log. Used so an operator can tell
    /// two credentials apart in a log without either being recoverable from it.
    /// </summary>
    public static string Fingerprint(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return "(none)";
        }

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret));
        return string.Concat("sha256:", Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant());
    }
}
