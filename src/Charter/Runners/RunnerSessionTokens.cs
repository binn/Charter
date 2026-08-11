using System.Security.Cryptography;
using System.Text;
using Charter.Configuration;

namespace Charter.Runners;

/// <summary>
/// The two bearer secrets a sandbox uses to talk to the control plane (sections 7.4, 33.5).
/// </summary>
/// <remarks>
/// <para>
/// Both are HMACs over <c>CHARTER_SECRET_KEY</c> rather than rows in a table, which is deliberate:
/// the dispatch path must not need a write to hand a runner an identity, and a control plane that
/// restarted mid-session must be able to validate a token it has no memory of issuing (section 2.3).
/// </para>
/// <para>
/// The <em>session secret</em> is per repository and long-lived — Charter writes it once as a
/// repository secret during onboarding, and the shipped workflow reads it from
/// <c>secrets.CHARTER_SESSION_SECRET</c>. It proves one thing: the caller is a workflow run inside
/// that repository. It deliberately proves nothing about <em>which</em> session is calling, because
/// every run in the repository holds the same value.
/// </para>
/// <para>
/// The <em>dispatch token</em> is what makes the exchange session-scoped. It is minted per session and
/// travels in the dispatch payload for that session alone, so a run started for one session cannot
/// produce another session's. Both are required at the exchange: the repository secret is the
/// authentication factor and never appears in a payload anybody with repository read access can see;
/// the dispatch token is the binding factor and grants nothing without the secret. Without the second
/// factor, any workflow in the repository could mint credentials — and a 12-hour event token — for
/// every other live session in it, which is the whole of what sections 7.4 and 16 are trying to
/// prevent.
/// </para>
/// <para>
/// The <em>event token</em> is per session and short-lived, and it is what the workflow uses for every
/// subsequent call. None of the three is a GitHub token, none is a model credential, and none can be
/// exchanged for a refresh token (section 20b.2).
/// </para>
/// </remarks>
public sealed class RunnerSessionTokens
{
    /// <summary>The repository secret the shipped workflow reads.</summary>
    public const string RepositorySecretName = "CHARTER_SESSION_SECRET";

    /// <summary>How long an event token is good for. Long enough for a slow build (section 27.5).</summary>
    public static readonly TimeSpan EventTokenLifetime = TimeSpan.FromHours(12);

    private readonly byte[] _key;

    public RunnerSessionTokens(KeyConfig keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _key = Encoding.UTF8.GetBytes(keys.SecretKey.Reveal());
    }

    /// <summary>Test seam: build over an explicit key rather than the instance configuration.</summary>
    public RunnerSessionTokens(string secretKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);
        _key = Encoding.UTF8.GetBytes(secretKey);
    }

    /// <summary>The value Charter writes to the repository's <c>CHARTER_SESSION_SECRET</c>.</summary>
    public string SessionSecretFor(string repoFullName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoFullName);
        return Sign($"session-secret:v1:{repoFullName.Trim().ToLowerInvariant()}");
    }

    /// <summary>Constant-time check of a presented session secret.</summary>
    public bool ValidateSessionSecret(string repoFullName, string? presented)
        => presented is not null && FixedTimeEquals(SessionSecretFor(repoFullName), presented);

    /// <summary>
    /// The per-session half of the credential exchange, handed only to the run started for it.
    /// </summary>
    /// <remarks>
    /// Signed rather than stored, for the reason every token here is: dispatch must not need a write
    /// to hand a runner an identity, and a control plane that restarted mid-session must be able to
    /// validate one it has no memory of issuing (section 2.3). It carries no expiry of its own —
    /// the liveness gate on the exchange is the bound, and it is a tighter one than a clock: the token
    /// stops working the moment its session is cancelled or ends.
    /// </remarks>
    public string DispatchTokenFor(Guid sessionId) => Sign($"session-dispatch:v1:{sessionId:D}");

    /// <summary>Constant-time check that a presented dispatch token names this session.</summary>
    public bool ValidateDispatchToken(Guid sessionId, string? presented)
        => presented is not null && FixedTimeEquals(DispatchTokenFor(sessionId), presented);

    /// <summary>Mints the per-session token every later callback authenticates with.</summary>
    public string IssueEventToken(Guid sessionId, DateTimeOffset? now = null)
    {
        var expires = (now ?? DateTimeOffset.UtcNow).Add(EventTokenLifetime).ToUnixTimeSeconds();
        var signature = Sign($"event-token:v1:{sessionId:D}:{expires}");

        return $"{sessionId:D}.{expires}.{signature}";
    }

    /// <summary>Validates a token against a session and the clock. No database read, by design.</summary>
    public bool ValidateEventToken(Guid sessionId, string? token, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var parts = token.Split('.');
        if (parts.Length != 3
            || !Guid.TryParse(parts[0], out var tokenSession)
            || tokenSession != sessionId
            || !long.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var expires))
        {
            return false;
        }

        if (DateTimeOffset.FromUnixTimeSeconds(expires) <= (now ?? DateTimeOffset.UtcNow))
        {
            return false;
        }

        return FixedTimeEquals(Sign($"event-token:v1:{sessionId:D}:{expires}"), parts[2]);
    }

    private string Sign(string message)
    {
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(message));
        return Base64Url(mac);
    }

    private static string Base64Url(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedTimeEquals(string expected, string presented)
    {
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(presented.Trim());

        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}

/// <summary>What a sandbox gets when it exchanges its session secret (sections 7.4, 33.5).</summary>
/// <param name="GitHubToken">Short-TTL, scoped to one repository, and nothing else.</param>
/// <param name="ModelApiKey">
/// An access token only. Never a refresh token: the control plane owns OAuth refresh (section 20b.2).
/// </param>
public sealed record RunnerCredentials(string GitHubToken, string? ModelApiKey);

/// <summary>
/// Mints the per-job credentials a runner is allowed to hold.
/// </summary>
/// <remarks>
/// A seam, implemented over the GitHub App and the credential resolver. Declared here because the
/// callback endpoint is the only thing that needs it, and because a stub implementation is what lets
/// the whole dispatch path be tested without a GitHub App.
/// </remarks>
public interface IRunnerCredentialBroker
{
    Task<RunnerCredentials> IssueAsync(
        Guid sessionId,
        string repoFullName,
        CancellationToken cancellationToken = default);
}

/// <summary>The fallback when no broker is registered: refuses, and says what to configure.</summary>
public sealed class UnconfiguredRunnerCredentialBroker : IRunnerCredentialBroker
{
    /// <inheritdoc />
    public Task<RunnerCredentials> IssueAsync(
        Guid sessionId,
        string repoFullName,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "Charter cannot mint runner credentials because no GitHub App client is registered. Set "
            + "GITHUB_APP_ID and GITHUB_APP_PRIVATE_KEY so a short-TTL installation token can be issued "
            + "for the session's repository.");
}
