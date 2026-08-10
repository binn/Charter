using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Charter.Data;

/// <summary>
/// Builds a parameterised command on a context's own connection.
/// </summary>
/// <remarks>
/// A few statements are worth writing by hand rather than expressing in LINQ: an
/// <c>INSERT … ON CONFLICT DO UPDATE … RETURNING</c> is atomic where a read followed by a write is a
/// race, and EF has no way to say it. Every such statement in this assembly is a constant with bound
/// parameters — no value or identifier is ever interpolated into the text — and this helper is the
/// one place that turns one into a command.
/// </remarks>
internal static class RawSqlCommand
{
    /// <summary>Creates a command, opening the connection and enlisting in an ambient transaction.</summary>
    public static async Task<DbCommand> CreateAsync(
        CharterDbContext db,
        string sql,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();

        return command;
    }

    /// <summary>Binds one value. Never a concatenation.</summary>
    public static void AddParameter(this DbCommand command, string name, NpgsqlDbType type, object? value)
    {
        ArgumentNullException.ThrowIfNull(command);

        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
    }
}
