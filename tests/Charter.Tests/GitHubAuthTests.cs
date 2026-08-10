using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Charter.GitHub;

namespace Charter.Tests;

/// <summary>
/// GitHub App authentication: the JWT, the single-repository installation token, and the cache.
/// </summary>
/// <remarks>
/// Section 7.4 is what these assert. Not "does it work" — whether the token that comes out is scoped
/// to one repository, expires, is re-minted rather than clung to, and never appears in a string.
/// </remarks>
public class GitHubAuthTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheJwtIsThreeBase64UrlSegmentsSignedWithRs256()
    {
        var jwt = GitHubAppJwt.Create(1337, GitHubTestFixtures.PrivateKeyPem, Now);

        var segments = jwt.Split('.');
        Assert.Equal(3, segments.Length);

        // Base64url: no padding, and neither of the two characters base64 uses that URLs cannot.
        foreach (var segment in segments)
        {
            Assert.DoesNotContain('=', segment);
            Assert.DoesNotContain('+', segment);
            Assert.DoesNotContain('/', segment);
        }

        Assert.Equal("""{"alg":"RS256","typ":"JWT"}""", Decode(segments[0]));

        using var payload = JsonDocument.Parse(Decode(segments[1]));

        Assert.Equal("1337", payload.RootElement.GetProperty("iss").GetString());

        // iat is backdated by a minute: GitHub rejects a token issued in the future outright, and a
        // container's clock is not GitHub's.
        Assert.Equal(Now.AddSeconds(-60).ToUnixTimeSeconds(), payload.RootElement.GetProperty("iat").GetInt64());
        Assert.Equal(Now.AddMinutes(9).ToUnixTimeSeconds(), payload.RootElement.GetProperty("exp").GetInt64());
    }

    [Fact]
    public void TheJwtSignatureVerifiesAgainstThePublicKey()
    {
        var jwt = GitHubAppJwt.Create(1337, GitHubTestFixtures.PrivateKeyPem, Now);
        var segments = jwt.Split('.');

        using var rsa = RSA.Create();
        rsa.ImportFromPem(GitHubTestFixtures.PrivateKeyPem);

        var signed = Encoding.ASCII.GetBytes($"{segments[0]}.{segments[1]}");
        var signature = Convert.FromBase64String(Pad(segments[2]));

        Assert.True(rsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void ALifetimeOverTenMinutesIsClamped()
    {
        // GitHub refuses anything longer, and failing at GitHub is a worse way to learn that than
        // clamping here.
        var jwt = GitHubAppJwt.Create(1337, GitHubTestFixtures.PrivateKeyPem, Now, TimeSpan.FromHours(1));

        using var payload = JsonDocument.Parse(Decode(jwt.Split('.')[1]));

        Assert.Equal(Now.AddMinutes(10).ToUnixTimeSeconds(), payload.RootElement.GetProperty("exp").GetInt64());
    }

    [Fact]
    public void AKeyThatIsNotAPemIsRefusedWithAnActionableMessage()
    {
        var ex = Assert.Throws<GitHubApiException>(
            () => GitHubAppJwt.Create(1337, "not a pem at all", Now));

        Assert.Contains("GITHUB_APP_PRIVATE_KEY", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTokenRequestNamesExactlyOneRepository()
    {
        var handler = new GitHubStubHandler()
            .On(
                HttpMethod.Post,
                "/access_tokens",
                GitHubTestFixtures.TokenResponse("ghs_first", Now.AddHours(1)));

        using var provider = GitHubTestFixtures.Provider(handler, new ModelFakeTimeProvider(Now));

        var token = await provider.GetInstallationTokenAsync(
            GitHubTestFixtures.Repository,
            GitHubTokenScope.ReadOnly,
            TestContext.Current.CancellationToken);

        // Section 7.4: the runner gets a token for one repository. GitHub takes the bare name here.
        using var body = JsonDocument.Parse(handler.BodyFor("/access_tokens"));
        var repositories = body.RootElement.GetProperty("repositories").EnumerateArray().ToList();

        Assert.Single(repositories);
        Assert.Equal("widgets", repositories[0].GetString());
        Assert.Equal("read", body.RootElement.GetProperty("permissions").GetProperty("contents").GetString());

        Assert.Equal("acme/widgets", token.Repository);
        Assert.Equal("ghs_first", token.Token.Reveal());

        // The installation id is in the path, so a token can only ever be minted for an installation
        // Charter already knows about.
        Assert.Contains("/app/installations/4242/access_tokens", handler.Calls[0].Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAppJwtIsTheAuthorizationOnTheTokenExchangeAndNothingElse()
    {
        var handler = new GitHubStubHandler()
            .On(HttpMethod.Post, "/access_tokens", GitHubTestFixtures.TokenResponse("ghs_x", Now.AddHours(1)));

        using var provider = GitHubTestFixtures.Provider(handler, new ModelFakeTimeProvider(Now));

        await provider.GetInstallationTokenAsync(
            GitHubTestFixtures.Repository,
            GitHubTokenScope.ReadOnly,
            TestContext.Current.CancellationToken);

        // Only one call was made: nothing else in the chain sees the app JWT.
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task ATokenIsCachedUntilShortlyBeforeItExpires()
    {
        var clock = new ModelFakeTimeProvider(Now);

        var handler = new GitHubStubHandler()
            .Once(HttpMethod.Post, "/access_tokens", GitHubTestFixtures.TokenResponse("ghs_first", Now.AddHours(1)))
            .On(HttpMethod.Post, "/access_tokens", GitHubTestFixtures.TokenResponse("ghs_second", Now.AddHours(2)));

        using var provider = GitHubTestFixtures.Provider(handler, clock);

        var first = await Get(provider, clock);
        var second = await Get(provider, clock);

        Assert.Equal("ghs_first", first.Token.Reveal());
        Assert.Equal("ghs_first", second.Token.Reveal());
        Assert.Equal(1, handler.CountFor("/access_tokens"));

        // Still inside the ten-minute margin's reach: 49 minutes in, 11 minutes left.
        clock.Now = Now.AddMinutes(49);
        Assert.Equal("ghs_first", (await Get(provider, clock)).Token.Reveal());
        Assert.Equal(1, handler.CountFor("/access_tokens"));

        // Past the margin. The cached token is abandoned while it would still technically work,
        // because a session handed a token with four minutes left fails halfway through.
        clock.Now = Now.AddMinutes(51);
        Assert.Equal("ghs_second", (await Get(provider, clock)).Token.Reveal());
        Assert.Equal(2, handler.CountFor("/access_tokens"));
    }

    [Fact]
    public async Task EachScopeAndRepositoryIsItsOwnCacheEntry()
    {
        var clock = new ModelFakeTimeProvider(Now);

        var handler = new GitHubStubHandler()
            .On(HttpMethod.Post, "/access_tokens", GitHubTestFixtures.TokenResponse("ghs_any", Now.AddHours(1)));

        using var provider = GitHubTestFixtures.Provider(handler, clock);

        await provider.GetInstallationTokenAsync(
            GitHubTestFixtures.Repository,
            GitHubTokenScope.ReadOnly,
            TestContext.Current.CancellationToken);

        // Widening to a write scope must mint a new token rather than reuse the read-only one.
        await provider.GetInstallationTokenAsync(
            GitHubTestFixtures.Repository,
            GitHubTokenScope.Contribute,
            TestContext.Current.CancellationToken);

        await provider.GetInstallationTokenAsync(
            GitHubRepository.Parse("acme/other", 4242),
            GitHubTokenScope.ReadOnly,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, handler.CountFor("/access_tokens"));
        Assert.Equal(3, provider.CachedTokenCount);
    }

    [Fact]
    public async Task InvalidatingDropsTheCachedToken()
    {
        var clock = new ModelFakeTimeProvider(Now);

        var handler = new GitHubStubHandler()
            .On(HttpMethod.Post, "/access_tokens", GitHubTestFixtures.TokenResponse("ghs_a", Now.AddHours(1)));

        using var provider = GitHubTestFixtures.Provider(handler, clock);

        await Get(provider, clock);
        provider.Invalidate(GitHubTestFixtures.Repository, GitHubTokenScope.ReadOnly);
        await Get(provider, clock);

        Assert.Equal(2, handler.CountFor("/access_tokens"));
    }

    [Fact]
    public async Task ATokenCoveringEveryRepositoryIsRefused()
    {
        // Section 7.4 is not "prefer a narrow token". Handing a runner one that reaches every
        // repository in the installation would silently widen the blast radius of a compromise.
        var handler = new GitHubStubHandler()
            .On(
                HttpMethod.Post,
                "/access_tokens",
                """{"token":"ghs_wide","expires_at":"2026-08-10T13:00:00Z","repository_selection":"all"}""");

        using var provider = GitHubTestFixtures.Provider(handler, new ModelFakeTimeProvider(Now));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(
            () => provider.GetInstallationTokenAsync(
                GitHubTestFixtures.Repository,
                GitHubTokenScope.ReadOnly,
                TestContext.Current.CancellationToken));

        Assert.Contains("acme/widgets", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedExchangeReportsTheStatusAndNotTheBody()
    {
        var handler = new GitHubStubHandler()
            .On(
                HttpMethod.Post,
                "/access_tokens",
                """{"message":"Bad credentials","hint":"ghs_leaked_in_an_error_body"}""",
                HttpStatusCode.Unauthorized);

        using var provider = GitHubTestFixtures.Provider(handler, new ModelFakeTimeProvider(Now));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(
            () => provider.GetInstallationTokenAsync(
                GitHubTestFixtures.Repository,
                GitHubTokenScope.ReadOnly,
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.Status);
        Assert.DoesNotContain("ghs_leaked_in_an_error_body", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingStringifiesATokenValue()
    {
        var handler = new GitHubStubHandler()
            .On(HttpMethod.Post, "/access_tokens", GitHubTestFixtures.TokenResponse("ghs_topsecret", Now.AddHours(1)));

        using var provider = GitHubTestFixtures.Provider(handler, new ModelFakeTimeProvider(Now));

        var token = await Get(provider, new ModelFakeTimeProvider(Now));
        var credential = GitHubRunnerCredential.From(token);

        Assert.DoesNotContain("ghs_topsecret", token.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ghs_topsecret", credential.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ghs_topsecret", token.Token.ToString(), StringComparison.Ordinal);

        // Reading it is an explicit act, and the only one.
        Assert.Equal("ghs_topsecret", credential.Token.Reveal());
    }

    [Fact]
    public void TheRunnerCredentialCarriesNoWayToRenewItself()
    {
        // Section 7.4: the runner gets a token and cannot mint another. If this record ever grows a
        // provider, a factory or an app id, that property is gone.
        var properties = typeof(GitHubRunnerCredential)
            .GetProperties()
            .Select(property => property.PropertyType)
            .ToList();

        Assert.DoesNotContain(typeof(IGitHubAppTokenProvider), properties);
        Assert.DoesNotContain(typeof(IGitHubRunnerCredentialFactory), properties);
        Assert.DoesNotContain(typeof(GitHubRepository), properties);
        Assert.Equal(3, properties.Count);
    }

    private static Task<GitHubInstallationToken> Get(IGitHubAppTokenProvider provider, TimeProvider clock)
        => provider.GetInstallationTokenAsync(
            GitHubTestFixtures.Repository,
            GitHubTokenScope.ReadOnly,
            TestContext.Current.CancellationToken);

    private static string Decode(string segment)
        => Encoding.UTF8.GetString(Convert.FromBase64String(Pad(segment)));

    private static string Pad(string segment)
    {
        var value = segment.Replace('-', '+').Replace('_', '/');

        return value.PadRight(value.Length + ((4 - (value.Length % 4)) % 4), '=');
    }
}
