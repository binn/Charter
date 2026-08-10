using Microsoft.EntityFrameworkCore;

namespace Charter.Data;

/// <summary>Registers the persistence layer. The host wires this up; nothing here reads configuration.</summary>
public static class DataServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="CharterDbContext"/> and the Postgres job queue.
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="connectionString">
    /// An Npgsql keyword/value connection string. Convert a <c>DATABASE_URL</c> with
    /// <c>Charter.Configuration.DatabaseUrl.ToNpgsql</c> first (section 4.3) — Npgsql does not accept
    /// URI form.
    /// </param>
    public static IServiceCollection AddCharterData(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<CharterDbContext>(options => ConfigureNpgsql(options, connectionString));
        services.AddScoped<JobQueue>();

        // Section 2.3: state that must survive a restart lives in Postgres, so the stores that other
        // subsystems declare as seams are bound here rather than left on their in-memory defaults.
        services.AddCharterStores();

        return services;
    }

    /// <summary>
    /// The single spelling of the provider options, shared by the host and by the design-time
    /// factory so a migration is never applied under different settings than the app runs with.
    /// </summary>
    internal static void ConfigureNpgsql(DbContextOptionsBuilder options, string connectionString)
        => options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(
            CharterDbContext.MigrationsHistoryTable));

    // Deliberately no EnableRetryOnFailure: a retrying execution strategy refuses user-initiated
    // transactions, and section 34.4's reserve-then-settle accounting needs them. Transient
    // connection failures are handled where they occur rather than by a strategy that would make
    // every explicit transaction throw.
}
