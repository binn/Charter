using Npgsql;

namespace Charter.Configuration;

/// <summary>
/// The Postgres connection, derived from <c>DATABASE_URL</c> (sections 4.2, 4.3).
/// </summary>
/// <remarks>
/// The connection string carries the password, so it is a <see cref="Secret"/>. Host, port, database
/// and username are broken out because those are the parts worth logging when a connection fails.
/// </remarks>
public sealed record DatabaseConfig
{
    /// <summary>The Npgsql keyword/value connection string produced by <see cref="DatabaseUrl"/>.</summary>
    public required Secret ConnectionString { get; init; }

    /// <summary>Database host.</summary>
    public required string Host { get; init; }

    /// <summary>Database port; 5432 when the URL omitted it.</summary>
    public required int Port { get; init; }

    /// <summary>Database name.</summary>
    public required string Database { get; init; }

    /// <summary>Login role, when the URL carried credentials.</summary>
    public string? Username { get; init; }

    /// <summary>What section 4.2 says a valid <c>DATABASE_URL</c> looks like.</summary>
    internal const string Expectation =
        "a postgres:// or postgresql:// URL, for example postgres://charter:password@db.internal:5432/charter";

    /// <summary>
    /// Reads <c>DATABASE_URL</c>, recording every parse problem rather than throwing.
    /// </summary>
    /// <param name="reader">Accumulates problems.</param>
    /// <param name="required">
    /// True for the full <see cref="CharterConfig"/>, where section 4.2 marks the variable required.
    /// False for <see cref="StartupOptions"/>, which boots the host so that <c>/ready</c> can report
    /// the missing database rather than the process dying before it can say anything.
    /// </param>
    internal static DatabaseConfig? Parse(EnvReader reader, bool required)
    {
        var raw = required
            ? reader.Required("DATABASE_URL", Expectation)
            : reader.Optional("DATABASE_URL");

        if (raw is null)
        {
            return null;
        }

        string connectionString;
        try
        {
            connectionString = DatabaseUrl.ToNpgsql(raw);
        }
        catch (ConfigException ex)
        {
            foreach (var problem in ex.Problems)
            {
                reader.Error("DATABASE_URL", problem);
            }

            return null;
        }

        var built = new NpgsqlConnectionStringBuilder(connectionString);
        return new DatabaseConfig
        {
            ConnectionString = new Secret(connectionString),
            Host = built.Host ?? string.Empty,
            Port = built.Port,
            Database = built.Database ?? string.Empty,
            Username = built.Username,
        };
    }
}
