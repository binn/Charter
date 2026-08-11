using Charter.Configuration;
using Charter.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Charter.Updates;

/// <summary>Registers the section 28 release check.</summary>
public static class UpdateServiceCollectionExtensions
{
    /// <summary>
    /// Adds the daily release check, or the drain that replaces it when the check is off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call this <em>after</em> <c>AddCharterConfig</c> and <c>AddCharterData</c>: the handler resolves
    /// <c>UpdateCheckConfig</c>, <c>CharterDbContext</c> and the job queue rather than constructing
    /// any of them. It may be called before or after <c>AddCharterOrchestration</c> — the dispatcher
    /// resolves <c>IQueuedJobHandler</c> per job, so registration order does not matter.
    /// </para>
    /// <para>
    /// Two switches decide what is registered, and both are read here rather than inside the check:
    /// <c>CHARTER_UPDATE_CHECK</c>, and <c>CHARTER_DEMO</c> through
    /// <see cref="CharterConfig.OutboundCallsAllowed"/>. Section 30.6's promise is that a demo instance
    /// contacts nobody, so the component that would contact somebody is never built rather than being
    /// built and blocked. The kill switch on <c>IHttpClientFactory</c> still stands behind it.
    /// </para>
    /// <para>
    /// A handler is registered in <em>both</em> positions. The dispatcher defers a job whose type has
    /// no handler, and re-enqueues it on every cycle forever, so an instance that turned the check off
    /// while one was scheduled would churn that row for the rest of its life. The disabled handler
    /// exists to drain it once.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddCharterUpdates(this IServiceCollection services, CharterConfig config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new UpdateCheckOptions());

        if (!config.ShouldCheckForUpdates)
        {
            services.AddScoped<IQueuedJobHandler, DisabledUpdateCheckJobHandler>();
            services.TryAddScoped<IUpdateStatusReader, DisabledUpdateStatusReader>();

            return services;
        }

        // Named, so section 30.6's kill switch and section 19.2's HttpClient instrumentation both see
        // it like every other egress in Charter.
        //
        // RemoveAllLoggers, and only on this client. IHttpClientFactory installs its own logging,
        // which writes "Start processing HTTP request", "Sending HTTP request" and "HTTP request
        // failed after 13ms" at Information for every attempt. On an air-gapped instance that is four
        // information lines a day about a failure section 28 requires to be silent - the exact noise
        // that teaches an operator to stop reading logs. Charter's own messages about the check are
        // written by the source and the handler, at levels they choose.
        services.AddHttpClient(GitHubReleaseSource.HttpClientName).RemoveAllLoggers();

        services.TryAddSingleton<IReleaseSource, GitHubReleaseSource>();
        services.AddScoped<IQueuedJobHandler, UpdateCheckJobHandler>();
        services.TryAddScoped<IUpdateStatusReader, UpdateStatusReader>();
        services.AddHostedService<UpdateCheckScheduler>();

        return services;
    }
}
