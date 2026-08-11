using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using Charter.Api;
using Charter.Api.Accounts;
using Charter.Api.Contracts;
using Charter.Auth;
using Charter.Auth.Setup;
using Charter.Configuration;
using Charter.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Charter.Tests;

/// <summary>
/// The routes that let a human in, checked where they are declared (sections 30.1, 30.2, 21, 31).
/// </summary>
/// <remarks>
/// Three separate claims, and each is checked against the mapped endpoint rather than against a
/// comment: the route exists at the path and verb somebody has to call, it is anonymous only where
/// it has to be, and every route that mints or consumes a credential carries the section 31 limiter.
/// A missing limiter on a new credential route is otherwise invisible until somebody scripts it.
/// </remarks>
public class ApiAuthEndpointTests
{
    private static WebApplication BuildApp()
    {
        var config = ConfigTestEnvironment.Valid();

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddLogging();
        builder.Services.AddCharterConfig(config);
        builder.Services.AddCharterData(config.Database.ConnectionString.Reveal());
        builder.Services.AddCharterAuth();
        builder.Services.AddCharterApi();

        return builder.Build();
    }

    private sealed record MappedEndpoint(
        string Pattern,
        IReadOnlyList<string> Methods,
        bool IsCredential,
        bool IsIntake,
        bool AllowsAnonymous,
        bool RequiresAuthorization);

    private static IReadOnlyList<MappedEndpoint> MapEverything()
    {
        var app = BuildApp();
        app.MapCharterApi();

        return [.. ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => new MappedEndpoint(
                endpoint.RoutePattern.RawText ?? string.Empty,
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [],
                endpoint.Metadata.GetMetadata<CredentialRateLimitMetadata>() is not null,
                endpoint.Metadata.GetMetadata<IntakeRateLimitMetadata>() is not null,
                endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null,
                endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>() is not null))];
    }

    private static MappedEndpoint Find(string method, string pattern)
    {
        var mapped = MapEverything();

        return Assert.Single(
            mapped,
            endpoint => string.Equals(endpoint.Pattern, pattern, StringComparison.Ordinal)
                        && endpoint.Methods.Contains(method, StringComparer.Ordinal));
    }

    /// <summary>Every route somebody uses to get in, and the verb they use.</summary>
    public static TheoryData<string, string> AuthRoutes =>
    new()
    {
        // Section 30.1: first run. Under /api/setup because that is the one prefix the setup gate
        // lets through.
        { "GET", "/api/setup/status" },
        { "POST", "/api/setup/complete" },

        // Section 21: sign-in, sign-out, and who is signed in.
        { "GET", "/api/auth/providers" },
        { "POST", "/api/auth/sign-in" },
        { "POST", "/api/auth/sign-out" },
        { "GET", "/api/auth/session" },
        { "GET", "/api/auth/{provider}/start" },
        { "GET", "/api/auth/{provider}/callback" },

        // Password reset, in its two halves.
        { "POST", "/api/auth/forgot-password" },
        { "POST", "/api/auth/reset-password" },

        // Section 30.2: invitations.
        { "POST", "/api/auth/invitations/accept" },
        { "GET", "/api/invitations/" },
        { "POST", "/api/invitations/" },
        { "DELETE", "/api/invitations/{id}" },

        // The admin-side reset that makes CHARTER_EMAIL_PROVIDER=none usable (part C.1).
        { "POST", "/api/password-resets" },
    };

    [Theory]
    [MemberData(nameof(AuthRoutes))]
    public void TheRouteIsMapped(string method, string pattern) => Assert.NotNull(Find(method, pattern));

    /// <summary>Every route reachable without a cookie, and therefore every one that must be limited.</summary>
    public static TheoryData<string, string> AnonymousRoutes =>
    new()
    {
        { "POST", "/api/setup/complete" },
        { "POST", "/api/auth/sign-in" },
        { "GET", "/api/auth/{provider}/start" },
        { "GET", "/api/auth/{provider}/callback" },
        { "POST", "/api/auth/forgot-password" },
        { "POST", "/api/auth/reset-password" },
        { "POST", "/api/auth/invitations/accept" },
    };

    [Theory]
    [MemberData(nameof(AnonymousRoutes))]
    public void EveryAnonymousCredentialRouteCarriesTheSectionThirtyOneLimiter(string method, string pattern)
    {
        var endpoint = Find(method, pattern);

        Assert.True(endpoint.AllowsAnonymous, $"{method} {pattern} should be reachable without a cookie");
        Assert.True(endpoint.IsCredential, $"{method} {pattern} mints or consumes a credential and must be limited");
    }

    [Theory]
    [InlineData("GET", "/api/auth/session")]
    [InlineData("POST", "/api/auth/sign-out")]
    [InlineData("GET", "/api/invitations/")]
    [InlineData("POST", "/api/invitations/")]
    [InlineData("DELETE", "/api/invitations/{id}")]
    [InlineData("POST", "/api/password-resets")]
    public void TheRestOfTheAccountSurfaceNeedsACookie(string method, string pattern)
    {
        var endpoint = Find(method, pattern);

        Assert.True(endpoint.RequiresAuthorization);
        Assert.False(endpoint.AllowsAnonymous);
    }

    [Fact]
    public void SendingAnInvitationOrAResetIsCountedLikeIntake()
    {
        // Both put a message in a real inbox, and both are reachable by somebody signed in — so the
        // partition that applies is the per-user and per-organisation one (section 31).
        Assert.True(Find("POST", "/api/invitations/").IsIntake);
        Assert.True(Find("POST", "/api/password-resets").IsIntake);
    }

    [Fact]
    public void ReadingWhoIsSignedInIsNotRateLimited()
    {
        // The shell asks this on every page load. Limiting it would break the app in the name of
        // section 31.
        var session = Find("GET", "/api/auth/session");

        Assert.False(session.IsIntake);
        Assert.False(session.IsCredential);
    }

    [Fact]
    public void TheSetupRoutesAreTheOnesAnUnclaimedInstanceStillAnswers()
    {
        // Section 30.1: the redemption route has to be reachable through the setup gate, or the
        // token is unredeemable and the instance is a brick.
        Assert.True(SetupModeMiddleware.IsPermittedDuringSetup(new PathString("/api/setup/complete")));
        Assert.True(SetupModeMiddleware.IsPermittedDuringSetup(new PathString("/api/setup/status")));

        // And nothing else in this surface is: with zero users there is nobody to sign in as, and an
        // unclaimed instance exposes no API beyond the one that claims it.
        Assert.False(SetupModeMiddleware.IsPermittedDuringSetup(new PathString("/api/auth/sign-in")));
        Assert.False(SetupModeMiddleware.IsPermittedDuringSetup(new PathString("/api/invitations")));
        Assert.False(SetupModeMiddleware.IsPermittedDuringSetup(new PathString("/api/password-resets")));
        Assert.False(SetupModeMiddleware.IsPermittedDuringSetup(new PathString("/api/repos")));
    }

    [Fact]
    public void EveryServiceTheseEndpointsAskForResolves()
    {
        using var app = BuildApp();
        using var scope = app.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AccountService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<Charter.Auth.SignInService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<Charter.Api.Repos.RepoOnboardingService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<SetupService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<Charter.Auth.Providers.PasswordResetTokens>());
    }
}

/// <summary>Section 31 at the routes an unauthenticated stranger can reach.</summary>
/// <remarks>
/// Driven directly, for the same reason <see cref="ApiRateLimitTests"/> is: what matters is the
/// partitioning. The credential limiter has to key on the caller rather than on a member, because
/// there is no member yet — and it must not touch anything else.
/// </remarks>
public class ApiCredentialRateLimitTests
{
    private static HttpContext Credential(string address)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);

        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(CredentialRateLimitMetadata.Instance),
            "credential"));

        return context;
    }

    private static int Acquire(PartitionedRateLimiter<HttpContext> limiter, HttpContext context, int attempts)
    {
        var granted = 0;

        for (var index = 0; index < attempts; index++)
        {
            using var lease = limiter.AttemptAcquire(context);
            if (lease.IsAcquired)
            {
                granted++;
            }
        }

        return granted;
    }

    [Fact]
    public void AScriptGuessingAtTheSignInFormIsStopped()
    {
        using var limiter = CharterRateLimiting.CreateCredentialLimiter(new CharterRateLimits
        {
            PerClientCredentialPerMinute = 5,
            PerClientCredentialPerHour = 100,
        });

        Assert.Equal(5, Acquire(limiter, Credential("203.0.113.7"), attempts: 50));
    }

    [Fact]
    public void OneAddressBeingThrottledDoesNotThrottleAnother()
    {
        using var limiter = CharterRateLimiting.CreateCredentialLimiter(new CharterRateLimits
        {
            PerClientCredentialPerMinute = 3,
            PerClientCredentialPerHour = 100,
        });

        Assert.Equal(3, Acquire(limiter, Credential("203.0.113.7"), attempts: 20));
        Assert.Equal(3, Acquire(limiter, Credential("198.51.100.4"), attempts: 20));
    }

    [Fact]
    public void TheHourlyCeilingHoldsWhenTheMinuteOneWouldNot()
    {
        using var limiter = CharterRateLimiting.CreateCredentialLimiter(new CharterRateLimits
        {
            PerClientCredentialPerMinute = 1_000,
            PerClientCredentialPerHour = 7,
        });

        Assert.Equal(7, Acquire(limiter, Credential("203.0.113.7"), attempts: 100));
    }

    [Fact]
    public void SomethingThatIsNotACredentialRouteIsUntouched()
    {
        using var limiter = CharterRateLimiting.CreateCredentialLimiter(new CharterRateLimits
        {
            PerClientCredentialPerMinute = 1,
            PerClientCredentialPerHour = 1,
        });

        var reading = new DefaultHttpContext();
        reading.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        reading.SetEndpoint(new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "read"));

        Assert.False(CharterRateLimiting.IsCredential(reading));
        Assert.Equal(20, Acquire(limiter, reading, attempts: 20));
    }

    [Fact]
    public void ACallerWithNoAddressStillLandsInAPartitionRatherThanEscaping()
    {
        using var limiter = CharterRateLimiting.CreateCredentialLimiter(new CharterRateLimits
        {
            PerClientCredentialPerMinute = 2,
            PerClientCredentialPerHour = 100,
        });

        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(CredentialRateLimitMetadata.Instance),
            "credential"));

        Assert.Equal("client:unknown", CharterRateLimiting.ClientKey(context));
        Assert.Equal(2, Acquire(limiter, context, attempts: 20));
    }
}

/// <summary>Section 7.4 applied to the bodies these new routes return.</summary>
public class ApiAccountOmissionTests
{
    [Fact]
    public async Task AOneTimeLinkTheCallerMayNotSeeIsAnAbsentKey()
    {
        var emailed = new OneTimeLinkResponse { Emailed = true, Message = "Invitation sent." };
        var body = await ApiPayloads.RenderAsync(emailed);

        using var document = JsonDocument.Parse(body);

        // `TryGetProperty` returning false is the assertion. A JSON null would return true, and the
        // client's test is `'link' in response`.
        Assert.False(document.RootElement.TryGetProperty("link", out _));
    }

    [Fact]
    public async Task ASurfacedLinkIsPresentAndUsable()
    {
        var surfaced = new OneTimeLinkResponse
        {
            Emailed = false,
            Message = "Email is not set up on this instance.",
            Link = "https://charter.example.com/accept-invitation?token=abc",
        };

        using var document = JsonDocument.Parse(await ApiPayloads.RenderAsync(surfaced));

        Assert.True(document.RootElement.TryGetProperty("link", out var link));
        Assert.Contains("token=abc", link.GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheForgotPasswordResponseHasNowhereToPutALink()
    {
        // The strongest guarantee is that there is nothing to read. This fails at compile time for
        // anybody who adds one, and here for anybody who does it by reflection-friendly means.
        var properties = typeof(ForgotPasswordResponse).GetProperties().Select(property => property.Name).ToList();

        Assert.Equal(["Message"], properties);
    }

    [Fact]
    public void NoRequestBodyTypeEverComesBackOutOfTheApi()
    {
        // A password, a setup token and an invitation token all arrive in a body type. None of them
        // is a member of any response type, so none can be echoed by accident.
        var responses = new[]
        {
            typeof(SessionResponse),
            typeof(InvitationResponse),
            typeof(InvitationsResponse),
            typeof(InvitationIssuedResponse),
            typeof(OneTimeLinkResponse),
            typeof(ForgotPasswordResponse),
            typeof(AuthProvidersResponse),
            typeof(AuthProviderResponse),
            typeof(SetupStatusResponse),
        };

        foreach (var type in responses)
        {
            // Only the properties that could carry a value: `selfServicePasswordReset` is a boolean
            // saying whether a button is offered, and naming it after the thing it enables is right.
            foreach (var property in type.GetProperties().Where(property => property.PropertyType == typeof(string)))
            {
                Assert.DoesNotContain("password", property.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("token", property.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("secret", property.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task TheProvidersListCarriesNoCredentialOfAnyKind()
    {
        var providers = new AuthProvidersResponse
        {
            Providers =
            [
                new AuthProviderResponse { Name = "password", Style = ApiAuthProviderStyle.Credential },
                new AuthProviderResponse
                {
                    Name = "github",
                    Style = ApiAuthProviderStyle.Redirect,
                    StartUrl = "/api/auth/github/start",
                },
            ],
            SelfServicePasswordReset = false,
        };

        var body = await ApiPayloads.RenderAsync(providers);
        var keys = ApiPayloads.Keys(body);

        Assert.DoesNotContain("clientSecret", keys);
        Assert.DoesNotContain("clientId", keys);

        // The password provider has no start URL, and the key is absent rather than null.
        using var document = JsonDocument.Parse(body);
        var password = document.RootElement.GetProperty("providers")[0];

        Assert.False(password.TryGetProperty("startUrl", out _));
    }
}
