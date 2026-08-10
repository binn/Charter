using Charter.Configuration;

namespace Charter.GitHub;

/// <summary>
/// An installation access token, scoped to exactly one repository (section 7.4).
/// </summary>
/// <remarks>
/// The value is a <see cref="Secret"/>, so the compiler-generated <c>ToString()</c> of anything
/// holding one prints a placeholder rather than a token. GitHub fixes the lifetime at one hour and
/// offers no way to shorten it, so the short-TTL property in section 7.4 is carried by the two things
/// Charter controls: the token names one repository, and it is minted per unit of work.
/// </remarks>
public sealed record GitHubInstallationToken
{
    /// <summary>The bearer token. Never logged, never returned to the UI.</summary>
    public required Secret Token { get; init; }

    /// <summary>When GitHub stops accepting it.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>The single repository this token reaches, as <c>owner/name</c>.</summary>
    public required string Repository { get; init; }

    /// <summary>The scope it was minted under.</summary>
    public required string Scope { get; init; }

    /// <summary>What GitHub said it granted, which may be narrower than what was asked for.</summary>
    public IReadOnlyDictionary<string, string> Permissions { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Whether the token is still worth handing out, with a margin (section 7.4).</summary>
    public bool IsUsableAt(DateTimeOffset now, TimeSpan margin) => now + margin < ExpiresAt;

    /// <inheritdoc />
    public override string ToString()
        => $"GitHub installation token for {Repository} ({Scope}), expires {ExpiresAt:O}";
}

/// <summary>
/// The credential handed to a runner. It can be used and it cannot be renewed.
/// </summary>
/// <remarks>
/// <para>
/// Section 7.4: the runner receives a short-TTL, single-repository installation token and cannot read
/// the control plane's environment. This record is that boundary made structural — it carries a
/// token, a repository and an expiry, and deliberately holds no reference to
/// <see cref="IGitHubAppTokenProvider"/>, no app id and no private key. A downstream component that
/// wanted to mint a fresh token would have to come back through the control plane to ask, which is
/// exactly the audit point we want.
/// </para>
/// <para>
/// A session outliving its token is therefore a real failure mode, and the right one: it fails
/// visibly rather than silently extending its own reach.
/// </para>
/// </remarks>
public sealed record GitHubRunnerCredential
{
    /// <summary>The single repository this credential reaches.</summary>
    public required string Repository { get; init; }

    /// <summary>The token itself.</summary>
    public required Secret Token { get; init; }

    /// <summary>When it stops working. There is no renewal path from here.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Projects a minted token into the credential the runner is given.</summary>
    public static GitHubRunnerCredential From(GitHubInstallationToken token)
    {
        ArgumentNullException.ThrowIfNull(token);

        return new GitHubRunnerCredential
        {
            Repository = token.Repository,
            Token = token.Token,
            ExpiresAt = token.ExpiresAt,
        };
    }

    /// <inheritdoc />
    public override string ToString() => $"GitHub credential for {Repository}, expires {ExpiresAt:O}";
}
