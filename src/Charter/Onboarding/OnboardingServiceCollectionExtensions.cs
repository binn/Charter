using Charter.GitHub;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Charter.Onboarding;

/// <summary>Registers repository onboarding and the <c>.charter/</c> loader (sections 8, 9).</summary>
/// <remarks>
/// <para>Host wiring (see <c>Program.cs</c>):</para>
/// <code>
/// builder.Services.AddCharterGitHub();
/// builder.Services.AddCharterOnboarding();   // after AddCharterGitHub() and AddCharterAuth()
/// // ...
/// app.MapCharterGitHub();
/// </code>
/// <para>
/// Calls <c>AddCharterGitHub()</c> itself, so a host that only wants onboarding gets a working one.
/// Every registration is a <c>TryAdd</c>, so calling both is safe and the first spelling wins.
/// </para>
/// </remarks>
public static class OnboardingServiceCollectionExtensions
{
    /// <summary>Adds the onboarding flow, the <c>.charter/</c> loader and its cache.</summary>
    public static IServiceCollection AddCharterOnboarding(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddCharterGitHub();

        // One cache for the process. The key is (repository, commit), so entries are immutable and
        // sharing them across scopes is free.
        services.TryAddSingleton(new CharterFolderCache());
        services.TryAddSingleton<ICharterFolderLoader, CharterFolderLoader>();

        services.TryAddScoped<IOnboardingRunDispatcher, JobQueueOnboardingDispatcher>();
        services.TryAddScoped<OnboardingService>();
        services.TryAddScoped<IOnboardingRunCallbacks>(provider =>
            provider.GetRequiredService<OnboardingService>());
        services.TryAddScoped<RequestableRepoQuery>();

        // Enumerable, not TryAdd: several subsystems listen, and the receiver fans out to all of them.
        services.AddScoped<IGitHubWebhookListener, OnboardingWebhookListener>();

        return services;
    }
}
