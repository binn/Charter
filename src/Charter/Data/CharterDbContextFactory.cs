using Charter.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Charter.Data;

/// <summary>
/// Builds a context for <c>dotnet ef</c>, which runs outside the host and therefore outside startup
/// configuration validation.
/// </summary>
/// <remarks>
/// Reads <c>DATABASE_URL</c> and converts it with <see cref="DatabaseUrl.ToNpgsql"/>, the same
/// conversion the application uses (section 4.3). With no <c>DATABASE_URL</c> set it falls back to a
/// local development database so <c>dotnet ef migrations add</c> works on a fresh clone — the
/// fallback is never used at runtime, because the host passes an explicit connection string to
/// <see cref="DataServiceCollectionExtensions.AddCharterData"/>.
/// </remarks>
public sealed class CharterDbContextFactory : IDesignTimeDbContextFactory<CharterDbContext>
{
    internal const string LocalDevelopmentUrl = "postgres://postgres:postgres@localhost:5432/charter?sslmode=disable";

    public CharterDbContext CreateDbContext(string[] args)
    {
        var url = Environment.GetEnvironmentVariable("DATABASE_URL");
        var connectionString = DatabaseUrl.ToNpgsql(
            string.IsNullOrWhiteSpace(url) ? LocalDevelopmentUrl : url);

        var options = new DbContextOptionsBuilder<CharterDbContext>();
        DataServiceCollectionExtensions.ConfigureNpgsql(options, connectionString);

        return new CharterDbContext(options.Options);
    }
}
