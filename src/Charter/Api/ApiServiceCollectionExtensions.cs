using Charter.Api.Endpoints;
using Charter.Api.Requests;
using Charter.Api.Viewer;
using Charter.Hubs;
using Charter.VersionControl;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Charter.Api;

/// <summary>
/// Registers and maps the HTTP API and the realtime hub.
/// </summary>
/// <remarks>
/// Requires <c>AddCharterConfig</c>, <c>AddCharterData</c> and <c>AddCharterAuth</c> to have run:
/// this reads <c>CharterDbContext</c>, <c>ICharterAuthorizationService</c> and the cookie scheme from
/// the container, and never touches the environment itself.
///
/// <para>Host wiring (see <c>Program.cs</c>):</para>
/// <code>
/// builder.Services.AddCharterApi();   // after AddCharterAuth()
/// // ...
/// app.MapCharterApi();                // after UseAuthorization(), before MapFallbackToFile
/// app.MapCharterHubs();
/// </code>
/// </remarks>
public static class ApiServiceCollectionExtensions
{
    /// <summary>Adds the API services, the SignalR hub and the section 31 intake limiter.</summary>
    public static IServiceCollection AddCharterApi(this IServiceCollection services)
        => services.AddCharterApi(new CharterRateLimits());

    /// <summary>Adds the API services with explicit intake limits (section 31).</summary>
    public static IServiceCollection AddCharterApi(this IServiceCollection services, CharterRateLimits limits)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(limits);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(limits);

        // The read path names a change request by the provider's own word (change spec 001 part
        // A.2), so the registry has to resolve even in a host that wired no provider — with none, it
        // is empty and the wording falls back to Charter's neutral term. TryAdd throughout, so a host
        // that already called AddCharterVersionControl() keeps its registrations.
        services.AddCharterVersionControl();

        services.AddScoped<RequestQueryService>();
        services.AddScoped<RequestCommandService>();
        services.AddScoped<ViewerService>();

        // Every preference is a column on `users`, so this default writes all of them. TryAdd so a
        // richer store registered before this call — the onboarding work has one in view — wins
        // rather than being silently replaced.
        services.TryAddScoped<IViewerPreferencesStore, UserRecordPreferencesStore>();

        services.AddSingleton<PlainLanguageFailureFilter>();

        // Section 2.1. The payload spelling is the API's, so a frame on the wire and the same object
        // in a GET body serialise identically — including omitting what section 7.4 withholds.
        services
            .AddSignalR()
            .AddJsonProtocol(options => options.PayloadSerializerOptions = CharterApiJson.CreateWritableCopy());

        services.AddScoped<IRequestStreamPublisher, RequestStreamPublisher>();

        // Section 31: per user and per organisation, at intake.
        services.AddRateLimiter(options => CharterRateLimiting.Configure(options, limits));

        return services;
    }

    /// <summary>
    /// Maps every route in <c>ClientApp/src/api/client.ts</c> and turns the intake limiter on.
    /// </summary>
    /// <remarks>
    /// <see cref="RateLimiterApplicationBuilderExtensions.UseRateLimiter(IApplicationBuilder)"/> is
    /// installed here rather than left to the host, so the limiter and the endpoints that need it
    /// cannot be wired up independently and get out of step.
    /// </remarks>
    public static WebApplication MapCharterApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseRateLimiter();
        app.MapCharterApiEndpoints();

        return app;
    }

    /// <summary>Maps the SignalR hub the SPA subscribes to per request (section 2.1).</summary>
    public static WebApplication MapCharterHubs(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapHub<RequestHub>("/hub/requests");

        return app;
    }
}
