using Charter.Data;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;

namespace Charter.Updates;

/// <summary>Reads what the last release check found (section 28).</summary>
/// <remarks>
/// The read side of the cache. Section 28's badge and banner are visible to admins and engineers only,
/// and that authorisation belongs to whoever maps the endpoint (section 7.4) — this only answers the
/// question.
/// </remarks>
public interface IUpdateStatusReader
{
    /// <summary>
    /// The most recent result, or <see langword="null"/> when the check is off or has never run.
    /// </summary>
    Task<UpdateStatus?> ReadAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc />
/// <remarks>
/// The status lives on the newest <see cref="JobType.UpdateCheck"/> row, because that is the row the
/// last check wrote when it scheduled the next one. Ordering by <c>CreatedAt</c> descending picks the
/// pending future check on a healthy instance, and the last completed one during the moment a check is
/// in flight.
/// </remarks>
public sealed class UpdateStatusReader : IUpdateStatusReader
{
    private readonly CharterDbContext _db;

    public UpdateStatusReader(CharterDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc />
    public async Task<UpdateStatus?> ReadAsync(CancellationToken cancellationToken = default)
    {
        // Scheduled first, then newest. The pending row is the one the last check wrote its result
        // into when it armed the next check, so it always carries the freshest answer; ordering by
        // timestamp alone ties whenever a check completes in the same instant it was enqueued, and
        // returns the stale payload half the time.
        var payloads = await _db.Jobs
            .Where(job => job.Type == JobType.UpdateCheck)
            .OrderBy(job => job.Status == JobStatus.Pending ? 0 : 1)
            .ThenByDescending(job => job.CreatedAt)
            .ThenByDescending(job => job.Id)
            .Select(job => job.Payload)
            .Take(3)
            .ToListAsync(cancellationToken);

        foreach (var payload in payloads)
        {
            if (UpdateStatus.TryParse(payload) is { } status)
            {
                return status;
            }
        }

        return null;
    }
}

/// <summary>
/// The reader on an instance with <c>CHARTER_UPDATE_CHECK=false</c>.
/// </summary>
/// <remarks>
/// Answers "nothing known" without reading the queue at all, so a stale row left behind by an instance
/// that once had the check on can never surface a notice on one that has it off. That is the whole
/// promise of the variable: an operator who turned it off is not told about releases.
/// </remarks>
public sealed class DisabledUpdateStatusReader : IUpdateStatusReader
{
    /// <inheritdoc />
    public Task<UpdateStatus?> ReadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<UpdateStatus?>(null);
}
