using Charter.Auth.Audit;
using Charter.Auth.Authorization;
using Charter.Auth.Providers;
using Charter.Auth.Setup;
using Charter.Budgets;
using Charter.Configuration;
using Charter.Data;
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

        // Section 34.9: setup seeds an organisation's first budget, so this graph needs the amounts.
        // TryAdd, and the host registers its projection of CHARTER_DEFAULT_MONTHLY_BUDGET_USD and
        // CHARTER_DEFAULT_SESSION_BUDGET_USD before calling this - so an instance gets the operator's
        // figures and a subsystem test that only wants a SetupService gets section 4.2's defaults
        // rather than a missing registration.
        services.TryAddSingleton(new BudgetOptions());

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

        // Section 30.2's one-time reset link. Signed with CHARTER_SECRET_KEY, whose documented job is
        // exactly this, and single-use because the signature is bound to the verifier it was minted
        // against — so no table, and no cleanup job for one.
        services.TryAddSingleton(provider => new PasswordResetTokens(
            provider.GetRequiredService<CharterConfig>().Keys.SecretKey,
            provider.GetRequiredService<TimeProvider>()));

        // Section 21: the one path from a presented credential to a principal, for every provider.
        services.AddScoped<SignInService>();

        services.AddScoped<IAuditWriter, AuditWriter>();
        // Constructed explicitly rather than by convention: section 26.10's instance switch reaches
        // authorisation through CharterConfig, and the constructor defaults it to off, so a graph
        // that let the container guess would deny repository creation on an instance that allows it.
        services.AddScoped<ICharterAuthorizationService>(provider => new CharterAuthorizationService(
            provider.GetRequiredService<CharterDbContext>(),
            provider.GetRequiredService<IAuditWriter>(),
            provider.GetRequiredService<CharterConfig>()));
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
