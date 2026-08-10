using Charter.Auth.Audit;
using Charter.Auth.Authorization;
using Charter.Auth.Providers;
using Charter.Auth.Setup;
using Charter.Configuration;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Charter.Auth;

/// <summary>
/// Registers identity, authorisation, the audit write path and first-run setup (sections 7, 21, 30.1).
/// </summary>
/// <remarks>
/// Requires <c>AddCharterConfig</c> and <c>AddCharterData</c> to have run: this reads
/// <see cref="CharterConfig"/> and <see cref="Charter.Data.CharterDbContext"/> from the container and
/// never touches the environment itself.
/// </remarks>
public static class AuthServiceCollectionExtensions
{
    /// <summary>
    /// Adds every service in <c>Charter.Auth</c>, plus the cookie authentication scheme the API signs
    /// people in with.
    /// </summary>
    public static IServiceCollection AddCharterAuth(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        // Section 21: never hand-rolled. This wraps ASP.NET Core's PasswordHasher.
        services.TryAddSingleton<ICharterPasswordHasher>(_ => new CharterPasswordHasher());

        // Section 31: rate limiting at the one endpoint a stranger can spend CPU on.
        services.TryAddSingleton<ISignInThrottle>(provider =>
            new SignInThrottle(provider.GetRequiredService<TimeProvider>()));

        services.TryAddSingleton<IOAuthStateStore>(provider =>
            new OAuthStateStore(provider.GetRequiredService<TimeProvider>()));

        services.AddHttpClient(HttpOAuthExchange.HttpClientName);
        services.TryAddSingleton<IOAuthExchange>(provider => new HttpOAuthExchange(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpOAuthExchange.HttpClientName)));

        services.AddScoped<PasswordIdentityProvider>();

        // One registry per scope, because the password provider needs the request's DbContext. The
        // OAuth providers are built from configuration rather than registered one by one, so adding a
        // fifth provider is a descriptor and not a registration (section 21).
        services.AddScoped(provider => IdentityProviderRegistry.Build(
            provider.GetRequiredService<AuthConfig>(),
            provider.GetRequiredService<CharterConfig>().BaseUrl,
            provider.GetRequiredService<PasswordIdentityProvider>(),
            provider.GetRequiredService<IOAuthExchange>(),
            provider.GetRequiredService<IOAuthStateStore>()));

        services.AddScoped<IIdentityLinker, IdentityLinker>();

        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<ICharterAuthorizationService, CharterAuthorizationService>();
        services.AddScoped<IRepoScopeAdministration, RepoScopeAdministration>();

        // Section 30.1. The state latch and the token are singletons because setup is a property of
        // the process, not of a request.
        services.TryAddSingleton<SetupState>();
        services.TryAddSingleton(provider => new SetupTokenStore(provider.GetRequiredService<TimeProvider>()));
        services.AddScoped<SetupModeService>();
        services.AddScoped<SetupService>();
        services.AddHostedService<SetupHostedService>();

        services
            .AddAuthentication(CharterAuthenticationDefaults.Scheme)
            .AddCookie(CharterAuthenticationDefaults.Scheme);

        services.AddSingleton<IConfigureOptions<CookieAuthenticationOptions>, CharterCookieOptionsSetup>();

        services.AddAuthorization();

        return services;
    }

    /// <summary>
    /// Puts the section 30.1 setup gate in front of everything else.
    /// </summary>
    /// <remarks>
    /// Register this before authentication and before any endpoint: an instance nobody has claimed
    /// must not answer an API call, and a middleware placed after routing would already have run the
    /// endpoint's filters by the time it refused.
    /// </remarks>
    public static IApplicationBuilder UseCharterSetupMode(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<SetupModeMiddleware>();
    }
}
