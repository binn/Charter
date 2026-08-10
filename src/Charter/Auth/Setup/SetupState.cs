using Charter.Data;
using Microsoft.EntityFrameworkCore;

namespace Charter.Auth.Setup;

/// <summary>
/// Whether this instance is still in setup mode, and the latch that says it never will be again.
/// </summary>
/// <remarks>
/// Section 30.1: setup mode ends permanently and cannot be re-entered while a user exists. The latch
/// only ever moves one way. Deleting every user from the database would still not reopen setup on a
/// running process, which is the conservative direction to be wrong in.
/// </remarks>
public sealed class SetupState
{
    private int completed;

    /// <summary>True once a user has been observed. Never returns to false.</summary>
    public bool IsCompleted => Volatile.Read(ref completed) == 1;

    /// <summary>Latches setup closed. Idempotent.</summary>
    public void MarkCompleted() => Volatile.Write(ref completed, 1);
}

/// <summary>Answers "is this instance still waiting to be claimed?" against the database.</summary>
public sealed class SetupModeService
{
    private readonly CharterDbContext database;
    private readonly SetupState state;

    public SetupModeService(CharterDbContext database, SetupState state)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(state);

        this.database = database;
        this.state = state;
    }

    /// <summary>
    /// True when there are zero users. Latches <see cref="SetupState"/> the first time it sees one,
    /// so the database is consulted at most until the instance is claimed and never afterwards.
    /// </summary>
    public async Task<bool> IsSetupRequiredAsync(CancellationToken cancellationToken = default)
    {
        if (state.IsCompleted)
        {
            return false;
        }

        if (await database.Users.AnyAsync(cancellationToken))
        {
            state.MarkCompleted();
            return false;
        }

        return true;
    }
}
