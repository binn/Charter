using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Charter.Updates;

/// <summary>
/// Puts the first release check on the queue, and only the first (section 28).
/// </summary>
/// <remarks>
/// <para>
/// Every check after this one is enqueued by the check before it, so this exists for exactly two
/// moments: the first boot of an instance, and the boot after somebody turned the check back on. It
/// enqueues nothing when a check is already pending or in flight, which is what makes it safe to run
/// on every replica of every restart.
/// </para>
/// <para>
/// The first check is dated a minute out rather than immediately. A crash loop restarts a container
/// every few seconds, and an instance in one should not turn that into a request per restart against
/// somebody else's API.
/// </para>
/// </remarks>
public sealed class UpdateCheckScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly UpdateCheckConfig _config;
    private readonly UpdateCheckOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<UpdateCheckScheduler> _logger;

    public UpdateCheckScheduler(
        IServiceScopeFactory scopes,
        UpdateCheckConfig config,
        UpdateCheckOptions options,
        TimeProvider clock,
        ILogger<UpdateCheckScheduler> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _config = config;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Enqueues a check unless one is already pending or claimed.
    /// </summary>
    /// <returns>Whether a job was added.</returns>
    public async Task<bool> EnsureScheduledAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<CharterDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();

        var scheduled = await db.Jobs.AnyAsync(
            job => job.Type == JobType.UpdateCheck
                   && (job.Status == JobStatus.Pending || job.Status == JobStatus.Claimed),
            cancellationToken);

        if (scheduled)
        {
            return false;
        }

        var now = _clock.GetUtcNow();

        // The payload of the first check is what the instance knows before it has looked: the channel
        // and the running version, and nothing about a release.
        await queue.EnqueueAsync(
            JobType.UpdateCheck,
            UpdateStatus.Unknown(_config.Channel, _options.CurrentVersion).ToJson(),
            availableAt: now + _options.StartupDelay,
            now: now,
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "The daily release check is scheduled on the {Channel} channel (section 28). "
            + "Set CHARTER_UPDATE_CHECK=false to turn it off",
            _config.Channel.ToWireName());

        return true;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await EnsureScheduledAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down before the seed landed. The next boot seeds it.
        }
        catch (Exception exception)
        {
            // Never take the host down over this. An instance that cannot schedule its update check is
            // an instance without an update check, not a broken one.
            _logger.LogWarning(exception, "Could not schedule the release check; it will be retried on the next start");
        }
    }
}
