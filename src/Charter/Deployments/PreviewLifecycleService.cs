using Charter.Data;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Charter.Deployments;

/// <summary>
/// Keeps every preview and its verification artifact in step, and settles the ones that have lapsed.
/// </summary>
/// <remarks>
/// <para>
/// Both ingestion paths of section 18 write a <see cref="Deployment"/> row and nothing else — the
/// generic webhook because it is called by machines that know nothing about Charter's artifacts, and
/// the comment path because it goes through the same binder. This service is what turns those rows
/// into the card a requester looks at, and it is a loop rather than a callback for the reason section
/// 2.3 gives: the container can restart mid-session, so the only thing that may be relied on is what
/// is in Postgres. A webhook that arrived while the process was rolling is picked up on the next pass
/// instead of being lost.
/// </para>
/// <para>
/// Polling the provider is the second half. Railway's bot comment and Charter's own webhook are both
/// things that <em>might</em> arrive; asking Railway directly is the thing that always can. It runs on
/// its own, slower interval, because a preview that is not ready yet is not made ready by asking more
/// often.
/// </para>
/// </remarks>
public sealed class PreviewLifecycleService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DeploymentOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<PreviewLifecycleService> _logger;

    private DateTimeOffset _lastPollAt = DateTimeOffset.MinValue;

    public PreviewLifecycleService(
        IServiceScopeFactory scopeFactory,
        DeploymentOptions options,
        TimeProvider clock,
        ILogger<PreviewLifecycleService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>What one cycle did.</summary>
    /// <param name="Reconciled">Artifacts a deployment row changed.</param>
    /// <param name="Polled">Change requests the provider was asked about.</param>
    /// <param name="Expired">Previews marked expired.</param>
    /// <param name="Blocked">Change requests whose preview is not coming without a human.</param>
    public sealed record Cycle(int Reconciled, int Polled, int Expired, int Blocked);

    /// <summary>
    /// Runs one cycle. Public so a test can drive it deterministically rather than wait on a timer.
    /// </summary>
    public async Task<Cycle> RunOnceAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        var ingestor = scope.ServiceProvider.GetRequiredService<DeploymentIngestor>();
        var expiry = scope.ServiceProvider.GetRequiredService<PreviewExpiry>();
        var providers = scope.ServiceProvider.GetRequiredService<DeploymentProviderRegistry>();

        var open = await ingestor.OpenAsync(cancellationToken);

        var reconciled = 0;
        var polled = 0;
        var blocked = 0;

        var shouldPoll = providers.Configured is { Capabilities.Poll: true }
                         && now - _lastPollAt >= _options.PollInterval;

        foreach (var context in open)
        {
            var publication = await ingestor.PublishAsync(context.ChangeRequest.HeadSha, cancellationToken);

            if (publication is { Changed: true })
            {
                reconciled++;
            }

            if (!shouldPoll || publication is { State: VerificationArtifactState.Ready })
            {
                continue;
            }

            polled++;
            var observation = await ingestor.PollAsync(context, cancellationToken);

            if (observation.Availability != DeploymentAvailability.Blocked)
            {
                continue;
            }

            blocked++;

            // Section 18's Railway trap: a change request from an account outside the workspace never
            // gets an environment, and nothing anywhere reports an error. A warning naming the change
            // request is the difference between "the preview is taking a while" and a sentence an
            // operator can act on.
            _logger.LogWarning(
                "No preview for {Repo}#{Number}: {Explanation}",
                context.RepoFullName,
                context.ChangeRequest.Number,
                observation.Explanation);
        }

        if (shouldPoll)
        {
            _lastPollAt = now;
        }

        var expired = await expiry.SweepAsync(cancellationToken);

        return new Cycle(reconciled, polled, expired, blocked);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(_clock.GetUtcNow(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // A preview that fails to bind must never take the control plane with it.
                _logger.LogError(exception, "The preview lifecycle cycle failed; retrying");
            }

            try
            {
                await Task.Delay(_options.ReconcileInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
