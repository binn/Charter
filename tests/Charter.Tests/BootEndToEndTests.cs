using System.Net;
using System.Text.Json;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Charter.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Charter.Tests;

/// <summary>
/// A throwaway database of its own, so a boot test can assert things a shared database cannot answer.
/// </summary>
/// <remarks>
/// Setup mode (section 30.1) is a property of "this instance has zero users", and every other suite
/// in this project creates users. Asking whether a freshly booted Charter enters setup mode is only a
/// meaningful question against a database nothing else has touched — and it is the same reason
/// "migrations applied" can be asserted here and nowhere else: on a fresh database there is something
/// for boot to actually do.
/// </remarks>
internal sealed class ThrowawayDatabase : IAsyncDisposable
{
    private readonly string administrativeConnectionString;

    private ThrowawayDatabase(string administrativeConnectionString, string name, Uri url)
    {
        this.administrativeConnectionString = administrativeConnectionString;
        Name = name;
        Url = url;
    }

    public string Name { get; }

    /// <summary>The <c>postgres://</c> URL to hand to <c>DATABASE_URL</c>.</summary>
    public Uri Url { get; }

    public string ConnectionString => DatabaseUrl.ToNpgsql(Url.ToString());

    public static async Task<ThrowawayDatabase> CreateAsync(string databaseUrl, CancellationToken cancellationToken)
    {
        var administrative = DatabaseUrl.ToNpgsql(databaseUrl);
        var name = "charter_boot_" + Guid.NewGuid().ToString("n")[..12];

        await using (var connection = new NpgsqlConnection(administrative))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var url = new UriBuilder(databaseUrl) { Path = "/" + name }.Uri;

        return new ThrowawayDatabase(administrative, name, url);
    }

    public async ValueTask DisposeAsync()
    {
        // The host's own pool still holds sessions against this database; Postgres refuses to drop a
        // database anything is connected to.
        NpgsqlConnection.ClearAllPools();

        await using var connection = new NpgsqlConnection(administrativeConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)",
            connection);

        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>A Charter process, started the way <c>Program.cs</c> starts one.</summary>
internal sealed class BootedCharter : IAsyncDisposable
{
    private readonly WebApplication app;

    private BootedCharter(WebApplication app, HttpClient client)
    {
        this.app = app;
        Client = client;
    }

    public HttpClient Client { get; }

    /// <summary>
    /// Runs the exact sequence <c>Program.cs</c> runs — create, build, migrate, configure, start —
    /// on an ephemeral port.
    /// </summary>
    /// <remarks>
    /// <c>Microsoft.AspNetCore.Mvc.Testing</c> is not in the ASP.NET Core shared framework and this
    /// project does not reference it, so there is no <c>WebApplicationFactory</c> here. That turns out
    /// to be the stronger test anyway: <c>WebApplicationFactory</c> substitutes its own server and
    /// never binds a socket, whereas this is Kestrel, on a real port, serving the pipeline the host
    /// composed.
    /// </remarks>
    public static async Task<BootedCharter> StartAsync(
        CharterConfig config,
        Func<string, string?> environment,
        CancellationToken cancellationToken)
    {
        var builder = CharterHost.CreateBuilder([], config, config.ToStartupOptions(), environment);

        // The only departure from Program.cs: port 0 rather than the configured one, so a test run
        // never collides with a developer's running instance or with itself.
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();

        await CharterHost.MigrateAsync(app, cancellationToken);

        CharterHost.ConfigurePipeline(app);
        CharterHost.LogStartupSummary(config.ToStartupOptions());

        await app.StartAsync(cancellationToken);

        var address = app.Urls.First();
        var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) };

        return new BootedCharter(app, client);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();

        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        try
        {
            await app.StopAsync(shutdown.Token);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }
}

/// <summary>
/// Charter, actually started: Kestrel bound, migrations applied, probes answering, setup mode holding
/// the door.
/// </summary>
/// <remarks>
/// <para>
/// This is the test that answers "does the application boot", which is the question all three of the
/// defects behind this suite failed and no unit test asked. It would have caught the agent-plane
/// defect outright — the default configuration crashed at startup — and the missing preview
/// registration, because the graph it resolves is the host's.
/// </para>
/// <para>
/// It runs only when <c>CHARTER_TEST_DATABASE_URL</c> points at a throwaway Postgres, matching every
/// other integration suite here.
/// </para>
/// </remarks>
public class BootEndToEndTests
{
    private const string DatabaseUrlVariable = "CHARTER_TEST_DATABASE_URL";

    /// <summary>Builds a valid environment pointing at <paramref name="database"/>.</summary>
    private static (CharterConfig Config, Func<string, string?> Environment) Environment(
        ThrowawayDatabase database,
        params (string Key, string? Value)[] overrides)
    {
        var values = ConfigTestEnvironment.Required();
        values["DATABASE_URL"] = database.Url.ToString();

        foreach (var (key, value) in overrides)
        {
            if (value is null)
            {
                values.Remove(key);
            }
            else
            {
                values[key] = value;
            }
        }

        Func<string, string?> read = name => values.GetValueOrDefault(name);

        var parsed = CharterConfigParser.Parse(read);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Errors.Select(problem => problem.Text)));

        return (parsed.Config!, read);
    }

    private static CharterDbContext Connect(ThrowawayDatabase database)
    {
        var options = new DbContextOptionsBuilder<CharterDbContext>();
        DataServiceCollectionExtensions.ConfigureNpgsql(options, database.ConnectionString);

        return new CharterDbContext(options.Options);
    }

    [Fact]
    public async Task TheDefaultConfigurationBootsAppliesMigrationsAndAnswersItsProbes()
    {
        var databaseUrl = System.Environment.GetEnvironmentVariable(DatabaseUrlVariable);
        if (string.IsNullOrWhiteSpace(databaseUrl))
        {
            Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the boot tests.");
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;

        await using var database = await ThrowawayDatabase.CreateAsync(databaseUrl, cancellationToken);

        // No CHARTER_RUNNER: the default is github-actions, and the default is what shipped broken.
        var (config, environment) = Environment(database);

        await using var charter = await BootedCharter.StartAsync(config, environment, cancellationToken);

        // Section 2.3: migrations run on boot, because a PaaS offers no pre-deploy hook. The database
        // was created empty a moment ago, so every migration in the assembly had to have been applied
        // by the code above and not by anything else.
        await using (var database_ = Connect(database))
        {
            var applied = await database_.Database.GetAppliedMigrationsAsync(cancellationToken);
            var pending = await database_.Database.GetPendingMigrationsAsync(cancellationToken);

            Assert.NotEmpty(applied);
            Assert.Empty(pending);
        }

        // Section 31: liveness never touches a dependency.
        var health = await charter.Client.GetAsync("/health", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using (var document = JsonDocument.Parse(await health.Content.ReadAsStringAsync(cancellationToken)))
        {
            Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
        }

        // Readiness does, and the database it names is the one just migrated.
        var ready = await charter.Client.GetAsync("/ready", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        using (var document = JsonDocument.Parse(await ready.Content.ReadAsStringAsync(cancellationToken)))
        {
            Assert.Equal("ready", document.RootElement.GetProperty("status").GetString());
        }

        // Section 24: the AGPL section 13 descriptor, which the SPA footer links from.
        var instance = await charter.Client.GetAsync("/api/instance", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, instance.StatusCode);

        using (var document = JsonDocument.Parse(await instance.Content.ReadAsStringAsync(cancellationToken)))
        {
            Assert.Equal("AGPL-3.0-only", document.RootElement.GetProperty("license").GetString());
            Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("sourceUrl").GetString()));
        }

        // Section 30.1: zero users, so the instance is unclaimed and serves nothing but setup - while
        // the platform probes stay up, or a PaaS kills the container before anyone can claim it.
        var gated = await charter.Client.GetAsync("/api/me", cancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, gated.StatusCode);
        Assert.Contains("setup", await gated.Content.ReadAsStringAsync(cancellationToken), StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.OK, (await charter.Client.GetAsync("/health", cancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await charter.Client.GetAsync("/ready", cancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await charter.Client.GetAsync("/api/instance", cancellationToken)).StatusCode);

        // Claiming the instance ends setup mode: the same route stops being refused and starts being
        // an ordinary unauthenticated request.
        await ClaimAsync(database, cancellationToken);

        var claimed = await charter.Client.GetAsync("/api/me", cancellationToken);
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, claimed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, claimed.StatusCode);

        // Only now is the route table observable from outside: while the gate was closed every
        // /api/* path answered 503 whether or not it was mapped. With the default runner the agent
        // plane is not registered, so its routes must be genuinely absent - a present-but-broken
        // route is what took the default install down.
        // 405, not 404: with no POST handler at that path the only endpoint left matching it is the
        // SPA fallback, which is GET-only. Either way it is not the agent plane, which answers a
        // malformed pairing body with 400.
        var pair = await charter.Client.PostAsync(
            Charter.Agent.Protocol.AgentProtocol.PairPath,
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            cancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, pair.StatusCode);
    }

    /// <summary>Puts one user in the database, which is all section 30.1 needs to end setup mode.</summary>
    private static async Task ClaimAsync(ThrowawayDatabase database, CancellationToken cancellationToken)
    {
        await using var db = Connect(database);

        db.Users.Add(User.Create(
            "first@example.com",
            "First Admin",
            TeachingLevel.SkipTheBasics,
            DateTimeOffset.UtcNow));

        await db.SaveChangesAsync(cancellationToken);
    }

    [Fact]
    public async Task AnAgentInstanceBootsWithTheAgentPlaneServing()
    {
        var databaseUrl = System.Environment.GetEnvironmentVariable(DatabaseUrlVariable);
        if (string.IsNullOrWhiteSpace(databaseUrl))
        {
            Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the boot tests.");
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;

        await using var database = await ThrowawayDatabase.CreateAsync(databaseUrl, cancellationToken);

        var (config, environment) = Environment(
            database,
            ("CHARTER_RUNNER", "agent"),
            ("CHARTER_MODE", "organization"),
            ("CHARTER_DEPLOYMENT_PROVIDER", "railway"),
            ("CHARTER_RAILWAY_TOKEN", "railway-token"),
            ("CHARTER_RAILWAY_PROJECT_ID", "proj_123"),
            ("CHARTER_RAILWAY_BASE_ENVIRONMENT", "staging"));

        await using var charter = await BootedCharter.StartAsync(config, environment, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, (await charter.Client.GetAsync("/health", cancellationToken)).StatusCode);

        // Section 30.1 gates /api/agent/* like everything else, so the gate has to be open before the
        // route table says anything.
        await ClaimAsync(database, cancellationToken);

        // Section 33.3: the pairing route exists, and is refused on its merits rather than missing.
        // This is the configuration whose services were registered and whose routes had to be mapped
        // to match - the half of the agent-plane defect that a "never map them" fix would break.
        var pair = await charter.Client.PostAsync(
            Charter.Agent.Protocol.AgentProtocol.PairPath,
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            cancellationToken);

        // The plane is here: the request reached PairAsync and was refused on its contents. Anything
        // in the 404 / 405 / 503 family would mean the route was missing or still gated.
        Assert.Equal(HttpStatusCode.BadRequest, pair.StatusCode);
    }
}
