using Charter.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Charter.Orchestration;

/// <summary>
/// The session-scoped advisory lock that keeps exactly one dispatcher authoritative (section 2.3).
/// </summary>
/// <remarks>
/// <para>
/// Scaling the control plane to two replicas must not double-dispatch. <c>SKIP LOCKED</c> already
/// stops two dispatchers claiming the same row, so the advisory lock is not what makes claiming safe
/// — it is what stops the second replica polling, sweeping expired leases, and reconciling sessions
/// concurrently with the first. One dispatcher doing that is a design; two doing it is a race with
/// nobody watching.
/// </para>
/// <para>
/// The lock lives on the connection, so it is released by disconnecting. That is exactly the
/// behaviour wanted when a container is killed mid-dispatch: nobody has to notice the leader is gone,
/// because Postgres drops the lock when the socket does. The <see cref="AsyncServiceScope"/> is held
/// open for the life of the lease precisely to keep that connection alive.
/// </para>
/// </remarks>
public sealed class DispatcherLease : IAsyncDisposable
{
    private readonly AsyncServiceScope _scope;
    private readonly JobQueue _queue;
    private readonly long _key;
    private bool _released;

    private DispatcherLease(AsyncServiceScope scope, JobQueue queue, long key)
    {
        _scope = scope;
        _queue = queue;
        _key = key;
    }

    /// <summary>The lock key this lease holds.</summary>
    public long Key => _key;

    /// <summary>True until the lease is released.</summary>
    public bool IsHeld => !_released;

    /// <summary>
    /// Takes the lock, or returns null when another replica already has it.
    /// </summary>
    /// <remarks>
    /// Never blocks: <c>pg_try_advisory_lock</c> answers immediately, and a standby that waited would
    /// hold a connection open for the life of the leader for no reason.
    /// </remarks>
    public static async Task<DispatcherLease?> TryAcquireAsync(
        IServiceScopeFactory scopeFactory,
        long key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);

        var scope = scopeFactory.CreateAsyncScope();

        try
        {
            var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();

            if (!await queue.TryAcquireAdvisoryLockAsync(key, cancellationToken))
            {
                await scope.DisposeAsync();
                return null;
            }

            return new DispatcherLease(scope, queue, key);
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    /// <summary>Releases the lock explicitly (section 31), then lets the scope go.</summary>
    public async ValueTask DisposeAsync()
    {
        if (!_released)
        {
            _released = true;

            try
            {
                await _queue.ReleaseAdvisoryLockAsync(_key, CancellationToken.None);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Npgsql.NpgsqlException)
            {
                // The connection is already gone, which released the lock anyway. Shutdown must not
                // fail because the thing it was tidying up had already tidied itself.
            }
        }

        await _scope.DisposeAsync();
    }
}
