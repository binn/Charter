using System.Globalization;
using Npgsql;

namespace Charter.Configuration.Preflight;

/// <summary>
/// The database questions preflight needs answered, behind a seam so the checks themselves stay
/// testable without a live Postgres.
/// </summary>
public interface IDatabaseProbe
{
    /// <summary>Opens a connection and runs <c>SELECT 1</c>.</summary>
    Task<bool> CanConnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Number of rows in EF Core's migration history, or <c>-1</c> when the table does not exist -
    /// which is what an empty database on first boot looks like (section 2.3: migrations run on boot).
    /// </summary>
    Task<int> AppliedMigrationCountAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Number of linked <c>CredentialGrant</c> rows, or <c>-1</c> when the table does not exist yet.
    /// Section 4.2 footnote *: a linked grant satisfies the model-credential requirement just as an
    /// instance-level key does.
    /// </summary>
    Task<int> LinkedModelCredentialCountAsync(CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IDatabaseProbe"/> over Npgsql, using only the connection string from
/// <see cref="CharterConfig"/>.
/// </summary>
/// <remarks>
/// Deliberately raw SQL rather than the EF Core model: preflight has to work on a database whose
/// schema is older than this build, or empty, and it must not fail merely because a table it asked
/// about is not there yet. Every lookup is guarded by <c>to_regclass</c> for that reason.
/// </remarks>
public sealed class NpgsqlDatabaseProbe : IDatabaseProbe
{
    private static readonly IReadOnlyList<string> CredentialTableCandidates =
    [
        "public.credential_grants",
        "public.\"CredentialGrants\"",
        "public.credentialgrants",
    ];

    private readonly string connectionString;
    private readonly TimeSpan timeout;

    /// <summary>Creates a probe against the configured database.</summary>
    public NpgsqlDatabaseProbe(CharterConfig config)
        : this(GetConnectionString(config), TimeSpan.FromSeconds(5))
    {
    }

    /// <summary>Creates a probe with an explicit connection string and timeout.</summary>
    public NpgsqlDatabaseProbe(string connectionString, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        this.connectionString = connectionString;
        this.timeout = timeout;
    }

    /// <inheritdoc />
    public async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(linked.Token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        _ = await command.ExecuteScalarAsync(linked.Token).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<int> AppliedMigrationCountAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(linked.Token).ConfigureAwait(false);

        const string historyTable = "public.\"__EFMigrationsHistory\"";
        if (!await TableExistsAsync(connection, historyTable, linked.Token).ConfigureAwait(false))
        {
            return -1;
        }

        return await CountAsync(connection, historyTable, linked.Token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> LinkedModelCredentialCountAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(linked.Token).ConfigureAwait(false);

        foreach (var candidate in CredentialTableCandidates)
        {
            if (await TableExistsAsync(connection, candidate, linked.Token).ConfigureAwait(false))
            {
                return await CountAsync(connection, candidate, linked.Token).ConfigureAwait(false);
            }
        }

        return -1;
    }

    private static string GetConnectionString(CharterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Database.ConnectionString.Reveal();
    }

    private static async Task<bool> TableExistsAsync(
        NpgsqlConnection connection,
        string qualifiedName,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT to_regclass(@name) IS NOT NULL", connection);
        command.Parameters.AddWithValue("name", qualifiedName);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is true;
    }

    private static async Task<int> CountAsync(
        NpgsqlConnection connection,
        string qualifiedName,
        CancellationToken cancellationToken)
    {
        // The name comes from the fixed list above, never from user input, and has already been
        // resolved by to_regclass. Identifiers cannot be parameterised in Postgres.
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {qualifiedName}", connection);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull
            ? 0
            : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }
}
