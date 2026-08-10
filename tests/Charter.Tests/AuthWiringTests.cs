using Charter.Auth;
using Charter.Auth.Authorization;
using Charter.Auth.Providers;
using Charter.Auth.Setup;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Charter.Tests;

/// <summary>
/// <c>AddCharterAuth</c> registers a working graph.
/// </summary>
/// <remarks>
/// A registration mistake in a container is the kind of thing that only shows up on the first request
/// after a deploy. Resolving every service the API will ask for costs milliseconds and moves that
/// discovery to the build.
/// </remarks>
public class AuthWiringTests
{
    private static ServiceProvider BuildContainer(params (string Key, string? Value)[] overrides)
    {
        var config = ConfigTestEnvironment.Valid(overrides);

        var services = new ServiceCollection();
        services.AddLogging();

        // The host gets these from WebApplicationBuilder; the authorization policy cache needs the
        // endpoint data source, so a bare ServiceCollection has to stand them up itself.
        services.AddRouting();
        services.AddCharterConfig(config);
        services.AddCharterData(config.Database.ConnectionString.Reveal());
        services.AddCharterAuth();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
    }

    [Fact]
    public void EveryServiceTheApiWillAskForResolves()
    {
        using var container = BuildContainer();
        using var scope = container.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ICharterAuthorizationService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IRepoScopeAdministration>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<Charter.Auth.Audit.IAuditWriter>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IIdentityLinker>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IdentityProviderRegistry>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<SetupService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<SetupModeService>());

        Assert.NotNull(container.GetRequiredService<SetupState>());
        Assert.NotNull(container.GetRequiredService<SetupTokenStore>());
        Assert.NotNull(container.GetRequiredService<ICharterPasswordHasher>());
        Assert.NotNull(container.GetRequiredService<ISignInThrottle>());
        Assert.NotNull(container.GetRequiredService<IAuthenticationSchemeProvider>());

        // The section 30.1 hosted service is what prints the setup token on boot.
        Assert.Contains(
            container.GetServices<IHostedService>(),
            service => service is SetupHostedService);
    }

    [Fact]
    public void PasswordIsOfferedEvenWhenNothingElseIsConfigured()
    {
        using var container = BuildContainer();
        using var scope = container.CreateScope();

        var registry = scope.ServiceProvider.GetRequiredService<IdentityProviderRegistry>();

        // Section 21: email and password is the default and is always available.
        var only = Assert.Single(registry.All);
        Assert.Equal(IdentityProviderKind.Password, only.Kind);
        Assert.Equal(IdentityProviderStyle.Credential, only.Style);
        Assert.False(registry.SamlConfiguredButUnavailable);
    }

    [Fact]
    public void AConfiguredOAuthProviderJoinsTheRegistryWithoutAClassOfItsOwn()
    {
        using var container = BuildContainer(
            ("CHARTER_OAUTH_GITHUB_ID", "client-id"),
            ("CHARTER_OAUTH_GITHUB_SECRET", "client-secret"));

        using var scope = container.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IdentityProviderRegistry>();

        var github = Assert.IsType<OAuthIdentityProvider>(registry.Find("github"));

        Assert.Equal(IdentityProviderKind.GitHub, github.Kind);
        Assert.Equal(IdentityProviderStyle.Redirect, github.Style);
        Assert.Equal(
            new Uri("https://charter.example.com/api/auth/github/callback"),
            github.CallbackUri);
    }

    [Fact]
    public async Task ARedirectProviderAsksForARedirectCarryingSingleUseState()
    {
        using var container = BuildContainer(
            ("CHARTER_OAUTH_GOOGLE_ID", "client-id"),
            ("CHARTER_OAUTH_GOOGLE_SECRET", "client-secret"));

        using var scope = container.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IdentityProviderRegistry>();
        var google = registry.Find("google")!;

        var begun = await google.BeginAsync(
            new IdentityAuthenticationAttempt(),
            TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<IdentityAuthenticationResult.RedirectRequired>(begun);

        Assert.StartsWith(
            "https://accounts.google.com/o/oauth2/v2/auth?",
            redirect.Location.ToString(),
            StringComparison.Ordinal);
        Assert.Contains($"state={redirect.State}", redirect.Location.ToString(), StringComparison.Ordinal);

        // A callback with no state, or a replayed one, is refused before any network call happens.
        var forged = await google.CompleteAsync(
            new IdentityAuthenticationAttempt
            {
                Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["code"] = "whatever",
                    ["state"] = "not-the-state",
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(
            IdentityFailureReason.MalformedAttempt,
            Assert.IsType<IdentityAuthenticationResult.Failed>(forged).Reason);
    }

    [Fact]
    public void SamlIsReportedAsConfiguredButUnavailableRatherThanOffered()
    {
        using var container = BuildContainer(("CHARTER_SAML_METADATA_URL", "https://idp.example.com/metadata"));
        using var scope = container.CreateScope();

        var registry = scope.ServiceProvider.GetRequiredService<IdentityProviderRegistry>();

        Assert.True(registry.SamlConfiguredButUnavailable);
        Assert.Null(registry.Find("saml"));
    }

    [Fact]
    public void TheSessionCookieIsLockedDownOnAnHttpsDeployment()
    {
        using var container = BuildContainer();

        var options = container
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CharterAuthenticationDefaults.Scheme);

        Assert.Equal(CharterAuthenticationDefaults.SecureCookieName, options.Cookie.Name);
        Assert.True(options.Cookie.HttpOnly);
        Assert.Equal(CookieSecurePolicy.Always, options.Cookie.SecurePolicy);
    }

    [Fact]
    public void AnHttpDeploymentDropsTheHostPrefixRatherThanShippingAnUnusableCookie()
    {
        using var container = BuildContainer(("CHARTER_BASE_URL", "http://localhost:8080"));

        var options = container
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CharterAuthenticationDefaults.Scheme);

        // __Host- requires Secure, so keeping it on plain http would produce a cookie no browser stores.
        Assert.Equal(CharterAuthenticationDefaults.CookieName, options.Cookie.Name);
        Assert.True(options.Cookie.HttpOnly);
    }

    [Fact]
    public void ThePrincipalCarriesEveryRoleAndNoModeAtAll()
    {
        var member = AuthTestData.Member(Member.AllRoles.ToArray());

        var principal = CharterPrincipalFactory.Create(member, "Solo", IdentityProviderKind.Password);
        var read = CharterPrincipalFactory.Read(principal);

        Assert.Equal((member.OrgId, member.UserId), read);
        Assert.Equal(4, principal.FindAll(CharterClaimTypes.Role).Count());
        Assert.DoesNotContain(principal.Claims, claim => claim.Type.Contains("mode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnAnonymousPrincipalReadsAsNobody()
        => Assert.Null(CharterPrincipalFactory.Read(new System.Security.Claims.ClaimsPrincipal()));
}
