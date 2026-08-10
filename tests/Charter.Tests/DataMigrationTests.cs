using Charter.Configuration;
using Charter.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Charter.Tests;

/// <summary>
/// Guards the migration history: that one exists, that it still describes the current model, and —
/// when a throwaway database is configured — that it actually applies.
/// </summary>
public class DataMigrationTests
{
    private const string DatabaseUrlVariable = "CHARTER_TEST_DATABASE_URL";

    private static CharterDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<CharterDbContext>();
        DataServiceCollectionExtensions.ConfigureNpgsql(options, connectionString);

        return new CharterDbContext(options.Options);
    }

    [Fact]
    public void TheDesignTimeFactoryFallsBackToALocalDatabase()
    {
        // dotnet ef runs outside the host, so it never sees startup configuration validation.
        var previous = Environment.GetEnvironmentVariable("DATABASE_URL");

        try
        {
            Environment.SetEnvironmentVariable("DATABASE_URL", null);

            using var context = new CharterDbContextFactory().CreateDbContext([]);

            Assert.NotNull(context.Model.FindEntityType(typeof(Domain.Job)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATABASE_URL", previous);
        }
    }

    [Fact]
    public void TheDesignTimeFactoryUsesDatabaseUrlWhenItIsSet()
    {
        var previous = Environment.GetEnvironmentVariable("DATABASE_URL");

        try
        {
            Environment.SetEnvironmentVariable("DATABASE_URL", "postgres://alice:hunter2@db.internal:6543/charter");

            using var context = new CharterDbContextFactory().CreateDbContext([]);

            var connectionString = context.Database.GetConnectionString();

            Assert.NotNull(connectionString);
            Assert.Contains("Host=db.internal", connectionString, StringComparison.Ordinal);
            Assert.Contains("Port=6543", connectionString, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATABASE_URL", previous);
        }
    }

    [Fact]
    public void ThereIsExactlyOneMigrationAndItIsTheInitialOne()
    {
        using var context = CreateContext(DatabaseUrl.ToNpgsql(CharterDbContextFactory.LocalDevelopmentUrl));

        var migrations = context.Database.GetMigrations().ToArray();

        var initial = Assert.Single(migrations);
        Assert.EndsWith("InitialCreate", initial, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMigrationsAssemblySnapshotStillMatchesTheModel()
    {
        // Editing the model without adding a migration is the failure that only shows up on a
        // colleague's machine, or on the next deploy.
        using var context = CreateContext(DatabaseUrl.ToNpgsql(CharterDbContextFactory.LocalDevelopmentUrl));

        var snapshot = context.GetService<IMigrationsAssembly>().ModelSnapshot;
        Assert.NotNull(snapshot);

        var initializer = context.GetService<IModelRuntimeInitializer>();
        var snapshotModel = initializer.Initialize(
            ((IMutableModel)snapshot.Model).FinalizeModel(),
            designTime: true,
            validationLogger: null);

        var differences = context.GetService<IMigrationsModelDiffer>().GetDifferences(
            snapshotModel.GetRelationalModel(),
            context.GetService<IDesignTimeModel>().Model.GetRelationalModel());

        Assert.Empty(differences);
    }

    [Fact]
    public async Task TheMigrationAppliesToAnEmptyDatabase()
    {
        var url = Environment.GetEnvironmentVariable(DatabaseUrlVariable);
        if (string.IsNullOrWhiteSpace(url))
        {
            Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the migration against a database.");
            return;
        }

        await using var context = CreateContext(DatabaseUrl.ToNpgsql(url));

        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var pending = await context.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken);
        Assert.Empty(pending);

        var applied = await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(applied);
    }
}
