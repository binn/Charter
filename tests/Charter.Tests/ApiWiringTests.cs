using Charter.Api;
using Charter.Api.Changes;
using Charter.Api.Requests;
using Charter.Api.Runners;
using Charter.Api.Settings;
using Charter.Api.Setup;
using Charter.Api.Viewer;
using Charter.Auth;
using Charter.Configuration;
using Charter.Data;
using Charter.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Charter.Tests;

/// <summary>
/// <c>AddCharterApi</c> registers a graph that resolves.
/// </summary>
/// <remarks>
/// A registration mistake only shows up on the first request after a deploy. Resolving everything the
/// endpoints and the hub ask for costs milliseconds and moves that discovery to the build.
/// </remarks>
public class ApiWiringTests
{
    /// <summary>
    /// The container the host actually builds.
    /// </summary>
    /// <remarks>
    /// A bare <see cref="ServiceCollection"/> is not enough here: SignalR's connection manager takes
    /// an <c>IHostApplicationLifetime</c>, which only the host provides. Building through
    /// <see cref="WebApplication"/> is therefore the honest test — it is the graph <c>Program.cs</c>
    /// will have.
    /// </remarks>
    private static WebApplication BuildApp(CharterRateLimits? limits = null)
    {
        var config = ConfigTestEnvironment.Valid();

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddLogging();
        builder.Services.AddCharterConfig(config);
        builder.Services.AddCharterData(config.Database.ConnectionString.Reveal());
        builder.Services.AddCharterAuth();

        if (limits is null)
        {
            builder.Services.AddCharterApi();
        }
        else
        {
            builder.Services.AddCharterApi(limits);
        }

        return builder.Build();
    }

    [Fact]
    public void EveryServiceTheEndpointsAskForResolves()
    {
        using var app = BuildApp();
        using var scope = app.Services.CreateScope();
        var provider = scope.ServiceProvider;

        Assert.NotNull(provider.GetRequiredService<RequestQueryService>());
        Assert.NotNull(provider.GetRequiredService<RequestCommandService>());
        Assert.NotNull(provider.GetRequiredService<ViewerService>());
        Assert.NotNull(provider.GetRequiredService<IViewerPreferencesStore>());
        Assert.NotNull(provider.GetRequiredService<IRequestStreamPublisher>());

        // The panes, the runners list, the checklist and the email screen.
        Assert.NotNull(provider.GetRequiredService<TranscriptQueryService>());
        Assert.NotNull(provider.GetRequiredService<FileDiffService>());
        Assert.NotNull(provider.GetRequiredService<RunnersQueryService>());
        Assert.NotNull(provider.GetRequiredService<RunnersCommandService>());
        Assert.NotNull(provider.GetRequiredService<SetupChecklistService>());
        Assert.NotNull(provider.GetRequiredService<EmailSettingsService>());
    }

    [Fact]
    public void ThePanesResolveInAHostThatWiredNeitherGitHubNorTheAgentPlane()
    {
        // Pane 3 reads repository content and the runners page pairs agents, and neither dependency
        // is guaranteed: `AddCharterGitHub` is a separate call and the agent plane is registered only
        // when CHARTER_RUNNER includes `agent`. Both degrade to a plain sentence at request time
        // rather than failing to resolve at boot, which is what this pins.
        using var app = BuildApp();
        using var scope = app.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<Charter.Api.Changes.IRepositoryFileText>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<RunnersCommandService>());
    }

    [Fact]
    public void TheHubAndItsPublisherResolve()
    {
        using var app = BuildApp();
        using var scope = app.Services.CreateScope();
        var provider = scope.ServiceProvider;

        Assert.NotNull(provider.GetRequiredService<IHubContext<RequestHub>>());

        // The hub is activated by SignalR rather than the container, so its dependencies are what has
        // to resolve.
        Assert.NotNull(provider.GetRequiredService<Charter.Auth.Authorization.ICharterAuthorizationService>());
        Assert.NotNull(provider.GetRequiredService<RequestQueryService>());
    }

    [Fact]
    public void TheIntakeLimitsAreRegisteredAndOverridable()
    {
        using var app = BuildApp();
        Assert.Equal(new CharterRateLimits(), app.Services.GetRequiredService<CharterRateLimits>());

        using var tightened = BuildApp(new CharterRateLimits { PerUserPerMinute = 1 });
        Assert.Equal(1, tightened.Services.GetRequiredService<CharterRateLimits>().PerUserPerMinute);
    }

    [Fact]
    public void APreferenceStoreRegisteredFirstWins()
    {
        // The onboarding work owns the columns the default store cannot write, so the registration is
        // TryAdd and a richer implementation added before AddCharterApi takes precedence.
        var config = ConfigTestEnvironment.Valid();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddLogging();
        builder.Services.AddCharterConfig(config);
        builder.Services.AddCharterData(config.Database.ConnectionString.Reveal());
        builder.Services.AddCharterAuth();
        builder.Services.AddScoped<IViewerPreferencesStore, StubPreferencesStore>();
        builder.Services.AddCharterApi();

        using var app = builder.Build();
        using var scope = app.Services.CreateScope();

        Assert.IsType<StubPreferencesStore>(scope.ServiceProvider.GetRequiredService<IViewerPreferencesStore>());
    }

    /// <summary>
    /// Every route <c>ClientApp/src/api/client.ts</c> declares, and the verb it uses.
    /// </summary>
    /// <remarks>
    /// The SPA is written against this list and cannot be edited by this work, so a rename here has
    /// to break a test rather than a screen.
    /// </remarks>
    public static TheoryData<string, string> ClientRoutes =>
    new()
    {
        { "GET", "/api/me" },
        { "PATCH", "/api/me/preferences" },
        { "POST", "/api/me/onboarding/requester/complete" },
        { "GET", "/api/projects" },
        { "GET", "/api/requests/" },
        { "GET", "/api/requests/{id}" },
        { "POST", "/api/requests/" },
        { "POST", "/api/requests/{id}/refinement" },
        { "POST", "/api/requests/{id}/spec/{version:int}/approve" },
        { "POST", "/api/requests/{id}/spec/{version:int}/changes-requested" },
        { "POST", "/api/requests/{id}/feedback" },
        { "POST", "/api/requests/{id}/cancel" },
        { "POST", "/api/requests/{id}/artifacts/{artifactId}/rebuild" },
        { "GET", "/api/approvals" },

        // Panes 2 and 3 (section 12). The diff route is a catch-all because a repository path has
        // slashes in it, encoded or not.
        { "GET", "/api/requests/{id}/transcript" },
        { "GET", "/api/requests/{id}/changes/{*path}" },

        // The four post-hoc actions (section 7.5).
        { "POST", "/api/requests/{id}/session/approve" },
        { "POST", "/api/requests/{id}/session/steer" },
        { "POST", "/api/requests/{id}/session/revise" },
        { "POST", "/api/requests/{id}/session/take-over" },

        // Settings → Runners (section 33.3).
        { "GET", "/api/runners/" },
        { "POST", "/api/runners/pairing-tokens" },
        { "DELETE", "/api/runners/{agentId}" },

        // The admin setup checklist (section 30.2).
        { "GET", "/api/setup/checklist" },
        { "POST", "/api/setup/checklist/dismiss" },

        // Admin settings → Email (change spec 001 part C.3).
        { "GET", "/api/settings/email" },
        { "POST", "/api/settings/email/test" },
    };

    [Theory]
    [MemberData(nameof(ClientRoutes))]
    public void TheRouteTheClientCallsIsMapped(string method, string pattern)
    {
        var mapped = MapEverything();

        Assert.Contains(mapped, endpoint =>
            string.Equals(endpoint.Pattern, pattern, StringComparison.Ordinal)
            && endpoint.Methods.Contains(method, StringComparer.Ordinal));
    }

    [Fact]
    public void TheHubIsMappedWhereTheClientSubscribes()
    {
        // client.ts: "SignalR hub `/hub/requests`, group-joined per request."
        var mapped = MapEverything();

        Assert.Contains(mapped, endpoint => endpoint.Pattern.StartsWith("/hub/requests", StringComparison.Ordinal));
    }

    [Fact]
    public void IntakeCarriesTheSectionThirtyOneLimiterAndReadsDoNot()
    {
        var mapped = MapEverything();

        var intake = mapped.Single(endpoint =>
            endpoint.Pattern == "/api/requests/" && endpoint.Methods.Contains("POST", StringComparer.Ordinal));
        var reading = mapped.Single(endpoint =>
            endpoint.Pattern == "/api/requests/" && endpoint.Methods.Contains("GET", StringComparer.Ordinal));

        Assert.True(intake.IsIntake);
        Assert.False(reading.IsIntake);
    }

    [Fact]
    public void TheCollectionRoutesMatchTheUnslashedPathTheClientActuallyCalls()
    {
        // client.ts calls `/api/requests`, and the group root is written `MapGet("/")`. Routing drops
        // the trailing empty segment, so the two are the same route — but that is worth pinning rather
        // than assuming, because getting it wrong is a 404 on the app's busiest screen.
        var app = BuildApp();
        app.MapCharterApi();

        var collection = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText == "/api/requests/")
            .ToList();

        Assert.NotEmpty(collection);
        Assert.All(collection, endpoint => Assert.Equal(
            ["api", "requests"],
            endpoint.RoutePattern.PathSegments.Select(segment =>
                ((Microsoft.AspNetCore.Routing.Patterns.RoutePatternLiteralPart)segment.Parts[0]).Content)));
    }

    private sealed record MappedEndpoint(string Pattern, IReadOnlyList<string> Methods, bool IsIntake);

    private static IReadOnlyList<MappedEndpoint> MapEverything()
    {
        var app = BuildApp();
        app.MapCharterApi();
        app.MapCharterHubs();

        return [.. ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => new MappedEndpoint(
                endpoint.RoutePattern.RawText ?? string.Empty,
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [],
                endpoint.Metadata.GetMetadata<IntakeRateLimitMetadata>() is not null))];
    }

    private sealed class StubPreferencesStore : IViewerPreferencesStore
    {
        public Task<ViewerPreferencesRecord> GetAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult(Record());

        public Task<ViewerPreferencesRecord> UpdateAsync(
            Guid userId,
            Charter.Api.Contracts.UpdatePreferencesBody patch,
            CancellationToken cancellationToken = default) => Task.FromResult(Record());

        public Task<ViewerPreferencesRecord> CompleteRequesterOnboardingAsync(
            Guid userId,
            CancellationToken cancellationToken = default) => Task.FromResult(Record());

        private static ViewerPreferencesRecord Record() => new()
        {
            Theme = Charter.Api.Contracts.ApiThemePreference.Dark,
            Pane = Charter.Api.Contracts.ApiPanePreference.Developer,
            TeachingLevel = Charter.Api.Contracts.ApiTeachingLevel.JustTheDecisions,
        };
    }
}
