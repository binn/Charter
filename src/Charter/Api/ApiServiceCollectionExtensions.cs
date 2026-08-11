using System.Text.Json;
using System.Text.Json.Serialization;
using Charter.Api.Accounts;
using Charter.Api.Changes;
using Charter.Api.Credentials;
using Charter.Api.Endpoints;
using Charter.Api.Repos;
using Charter.Api.Requests;
using Charter.Api.Runners;
using Charter.Api.Settings;
using Charter.Api.Setup;
using Charter.Api.Viewer;
using Charter.Hubs;
using Charter.Notifications;
using Charter.Onboarding;
using Charter.Orchestration;
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

        // Minimal APIs bind request bodies through the host's JSON options, not through
        // `CharterApiJson` — that one is passed explicitly on the way out and is invisible on the way
        // in. Without this, every body carrying one of the section 12b wire enums failed to bind and
        // came back as a bare 400 with no body: `PATCH /api/me/preferences` (theme, pane, teaching
        // level), `POST /api/invitations` (roles), `POST /api/repos/{id}/access` (a role grant) and
        // `POST /api/members/{id}/roles`. Every one of those is spelled `snake_case` by the SPA
        // because that is how Charter writes them, so the reader has to know the same spelling as the
        // writer. Configured here rather than in the host so the API's own routes cannot be mapped
        // without it.
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.PropertyNameCaseInsensitive = true;
            options.SerializerOptions.Converters.Add(
                new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));

            // Section 7.4's mechanism, applied to anything that serialises without naming
            // `CharterApiJson.Options`: a withheld field is absent, never null.
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        // The read path names a change request by the provider's own word (change spec 001 part
        // A.2), so the registry has to resolve even in a host that wired no provider — with none, it
        // is empty and the wording falls back to Charter's neutral term. TryAdd throughout, so a host
        // that already called AddCharterVersionControl() keeps its registrations.
        services.AddCharterVersionControl();

        // Change spec 001 part C.3's settings screen reads the send path, the delivery log and the
        // tester. TryAdd throughout in there too, so a host that already called it keeps what it has.
        services.AddCharterNotifications();

        // Section 9's flow already existed with no HTTP surface at all. Called here, and TryAdd
        // throughout in there too, so a host that already wired onboarding keeps what it has and one
        // that only called AddCharterApi still resolves the repo routes.
        services.AddCharterOnboarding();

        services.AddScoped<AccountService>();
        services.AddScoped<MembersService>();
        services.AddScoped<AuditQueryService>();
        services.AddScoped<RepoOnboardingService>();

        // Section 20b.2's management surface. Scoped, because it writes through CharterDbContext.
        services.AddScoped<CredentialsService>();

        services.AddScoped<RequestQueryService>();
        services.AddScoped<RequestCommandService>();
        services.AddScoped<TranscriptQueryService>();
        services.AddScoped<FileDiffService>();
        services.AddScoped<RunnersQueryService>();
        services.AddScoped<RunnersCommandService>();
        services.AddScoped<SetupChecklistService>();
        services.AddScoped<EmailSettingsService>();
        services.AddScoped<ViewerService>();

        // Pane 3 reads repository content, which `IVersionControlProvider` does not expose. TryAdd so
        // a host with a better source registered first keeps it; the default degrades to a plain 503
        // when no GitHub client is wired rather than failing to resolve. See the report.
        services.TryAddScoped<IRepositoryFileText, GitHubRepositoryFileText>();

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

        // Section 11's streaming rule, for the one part of the loop that had no live view of itself.
        // `Charter.Orchestration` declares the seam in rows and this is its only implementation: it
        // renders through `RefinementThread` and `RequestProjection`, so a frame and a refetch are the
        // same projection rather than two that agree by convention.
        services.AddScoped<IRefinementStream, RefinementStreamPublisher>();

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
