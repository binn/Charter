using System.Net;
using System.Security.Cryptography;
using System.Text;
using Charter.Configuration;
using Charter.GitHub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>
/// The doubles the GitHub and onboarding tests share, and the assertions about them.
/// </summary>
/// <remarks>
/// No test in this file or its siblings makes a network call or talks to a real GitHub App. The
/// private key is generated in-process, the API is a routing table, and the webhook secret is a
/// literal — so a failure here is always Charter's, never GitHub's.
/// </remarks>
public class GitHubFixtureTests
{
    [Fact]
    public async Task TheStubAnswersRoutesAndRefusesEverythingElse()
    {
        var handler = new GitHubStubHandler()
            .On(HttpMethod.Get, "/git/ref/heads/main", """{"object":{"sha":"abc123"}}""");

        using var client = new HttpClient(handler, disposeHandler: false);

        var found = await client.GetAsync(
            new Uri("https://api.github.com/repos/acme/widgets/git/ref/heads/main"),
            TestContext.Current.CancellationToken);

        var missing = await client.GetAsync(
            new Uri("https://api.github.com/repos/acme/widgets/contents/nope"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, found.StatusCode);

        // An unrouted call is a 404 rather than an exception, because that is what GitHub does and
        // the client's "file is not there" path has to be exercised.
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public void ARepositoryFullNameIsOwnerAndName()
    {
        var repository = GitHubRepository.Parse("acme/widgets", 42);

        Assert.Equal("acme", repository.Owner);
        Assert.Equal("widgets", repository.Name);
        Assert.Equal("acme/widgets", repository.FullName);
        Assert.Equal(42, repository.InstallationId);

        Assert.Throws<ArgumentException>(() => GitHubRepository.Parse("widgets", 42));
        Assert.Throws<ArgumentException>(() => GitHubRepository.Parse("acme/widgets/extra", 42));
        Assert.Throws<ArgumentOutOfRangeException>(() => GitHubRepository.Parse("acme/widgets", 0));
    }

    [Fact]
    public void NoTokenScopeAsksForAdministrationOrSecrets()
    {
        // Section 7.4. Charter has no merge button and no reason to read a secret, so a scope that
        // could do either would be a widening nobody asked for.
        //
        // The everyday scopes are the ones a session, a recon run or a request path can reach. The
        // privileged scopes added for change spec 001 part A — reading branch protection, and the
        // optional create / transfer / protect operations — are deliberately not in this list and
        // are asserted separately below.
        foreach (var scope in GitHubTokenScope.Everyday)
        {
            Assert.DoesNotContain("administration", scope.Permissions.Keys, StringComparer.Ordinal);
            Assert.DoesNotContain("secrets", scope.Permissions.Keys, StringComparer.Ordinal);
            Assert.DoesNotContain("members", scope.Permissions.Keys, StringComparer.Ordinal);
            Assert.DoesNotContain("workflows", scope.Permissions.Keys, StringComparer.Ordinal);
        }

        Assert.Equal("read", GitHubTokenScope.ReadOnly.Permissions["contents"]);
        Assert.Equal("write", GitHubTokenScope.Contribute.Permissions["contents"]);
    }

    [Fact]
    public void ThePrivilegedScopesAreNarrowAndStillCannotMerge()
    {
        // Change spec 001 part A.5 needs to read a branch protection rule, and GitHub will not report
        // one to a token without `administration`. That scope reads and never writes, and it carries
        // nothing else — a token that could read protection should not also be able to read code.
        Assert.Equal("read", GitHubTokenScope.Inspect.Permissions["administration"]);
        Assert.DoesNotContain("contents", GitHubTokenScope.Inspect.Permissions.Keys, StringComparer.Ordinal);

        // The optional operations of part A.2 write a setting. Nothing else reaches this scope, and
        // there is no permission in it that would let anything merge.
        Assert.Equal("write", GitHubTokenScope.Administer.Permissions["administration"]);
        Assert.DoesNotContain("contents", GitHubTokenScope.Administer.Permissions.Keys, StringComparer.Ordinal);

        Assert.Equal("write", GitHubTokenScope.Webhooks.Permissions["repository_hooks"]);

        foreach (var scope in new[] { GitHubTokenScope.Inspect, GitHubTokenScope.Administer, GitHubTokenScope.Webhooks })
        {
            Assert.DoesNotContain("secrets", scope.Permissions.Keys, StringComparer.Ordinal);
            Assert.DoesNotContain("members", scope.Permissions.Keys, StringComparer.Ordinal);
            Assert.DoesNotContain("workflows", scope.Permissions.Keys, StringComparer.Ordinal);
        }
    }
}

/// <summary>An <see cref="HttpMessageHandler"/> that answers a routing table and records every call.</summary>
internal sealed class GitHubStubHandler : HttpMessageHandler
{
    private readonly List<Route> _routes = [];

    /// <summary>Every call made, in order: verb, absolute path, and the request body.</summary>
    public List<(string Method, string Path, string Body)> Calls { get; } = [];

    /// <summary>Answers <paramref name="json"/> to any request whose path contains the fragment.</summary>
    public GitHubStubHandler On(
        HttpMethod method,
        string pathFragment,
        string json,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.Add(new Route(method, pathFragment, _ => Respond(json, status), Once: false));
        return this;
    }

    /// <summary>Answers once, then falls through to any later route for the same path.</summary>
    public GitHubStubHandler Once(
        HttpMethod method,
        string pathFragment,
        string json,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.Add(new Route(method, pathFragment, _ => Respond(json, status), Once: true));
        return this;
    }

    /// <summary>How many calls hit a path containing <paramref name="fragment"/>.</summary>
    public int CountFor(string fragment)
        => Calls.Count(call => call.Path.Contains(fragment, StringComparison.Ordinal));

    /// <summary>The body of the first call to a path containing <paramref name="fragment"/>.</summary>
    public string BodyFor(string fragment)
        => Calls.First(call => call.Path.Contains(fragment, StringComparison.Ordinal)).Body;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.PathAndQuery ?? string.Empty;
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);

        Calls.Add((request.Method.Method, path, body));

        for (var index = 0; index < _routes.Count; index++)
        {
            var route = _routes[index];

            if (route.Method == request.Method
                && path.Contains(route.PathFragment, StringComparison.Ordinal))
            {
                if (route.Once)
                {
                    _routes.RemoveAt(index);
                }

                return route.Respond(request);
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"message":"Not Found"}""", Encoding.UTF8, "application/json"),
        };
    }

    private static HttpResponseMessage Respond(string json, HttpStatusCode status)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed record Route(
        HttpMethod Method,
        string PathFragment,
        Func<HttpRequestMessage, HttpResponseMessage> Respond,
        bool Once);
}

/// <summary>Shared fixtures for the GitHub and onboarding tests.</summary>
internal static class GitHubTestFixtures
{
    internal const string WebhookSecret = "a-webhook-secret-that-is-not-real";

    /// <summary>A repository with an installation, for every test that needs one.</summary>
    internal static GitHubRepository Repository { get; } = GitHubRepository.Parse("acme/widgets", 4242);

    /// <summary>An RSA private key generated in-process. No real GitHub App is involved anywhere.</summary>
    internal static string PrivateKeyPem { get; } = CreateKey();

    /// <summary>A config wired to the generated key.</summary>
    internal static GitHubConfig Config() => new()
    {
        AppId = 1337,
        PrivateKeyPem = new Secret(PrivateKeyPem),
        WebhookSecret = new Secret(WebhookSecret),
        PrivateKeyWasBase64 = false,
    };

    /// <summary>Options pointed at the stub, with a margin the tests can reason about.</summary>
    internal static GitHubOptions Options() => new()
    {
        ApiBaseUrl = new Uri("https://api.github.com/"),
        TokenRefreshMargin = TimeSpan.FromMinutes(10),
    };

    /// <summary>A token provider over a stub handler.</summary>
    internal static GitHubAppTokenProvider Provider(GitHubStubHandler handler, TimeProvider clock)
        => new(
            new StubHttpClientFactory(handler),
            Config(),
            Options(),
            clock,
            NullLogger<GitHubAppTokenProvider>.Instance);

    /// <summary>A repository client over a stub handler and a real (stubbed-transport) provider.</summary>
    internal static GitHubRepositoryClient Client(GitHubStubHandler handler, IGitHubAppTokenProvider tokens)
        => new(
            new StubHttpClientFactory(handler),
            tokens,
            Options(),
            NullLogger<GitHubRepositoryClient>.Instance);

    /// <summary>The JSON GitHub answers a token exchange with.</summary>
    internal static string TokenResponse(string token, DateTimeOffset expiresAt)
        => $$"""
             {
               "token": "{{token}}",
               "expires_at": "{{expiresAt:yyyy-MM-ddTHH:mm:ssZ}}",
               "repository_selection": "selected",
               "permissions": { "contents": "read", "metadata": "read" }
             }
             """;

    private static string CreateKey()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportRSAPrivateKeyPem();
    }
}
