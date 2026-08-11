using Charter.Configuration;
using Charter.Configuration.Preflight;
using Charter.Data;
using Charter.Data.Demo;
using Charter.Domain;
using Charter.Hosting;
using Charter.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>
/// Section 30.6: <c>CHARTER_DEMO=true</c> seeds a fake organisation, repository and completed
/// sessions, and disables all outbound calls.
/// </summary>
/// <remarks>
/// The kill switch is the security-relevant half. Demo mode exists so that someone evaluating
/// Charter spends no token and connects no GitHub App, and a demo mode that quietly makes one call
/// anyway would have broken that promise while appearing to keep it - so the tests here drive the
/// composed graph and assert that the request never leaves.
/// </remarks>
public class DemoModeTests
{
    private static ServiceCollection Compose(bool demo)
    {
        var read = ConfigTestEnvironment.With(
            ("CHARTER_DEMO", demo ? "true" : "false"),
            ("CHARTER_SMTP_URL", "smtp://mailer:secret@smtp.example.com:587"));

        var config = CharterConfigParser.Parse(read).ConfigOrThrow();

        var services = new ServiceCollection();
        services.AddLogging();
        CharterHost.ConfigureServices(services, config, config.ToStartupOptions(), read);

        return services;
    }

    [Fact]
    public async Task NoNamedHttpClientReachesTheNetworkInDemoMode()
    {
        // Every outbound call Charter makes goes through IHttpClientFactory - the three model
        // clients, the OpenRouter price catalog, the GitHub App, the OAuth exchange, the Railway
        // provider, the preview probe. Asserting per client name would pass on the day it was
        // written and miss the eighth, so this asserts the factory itself.
        using var provider = Compose(demo: true).BuildServiceProvider();

        var factory = provider.GetRequiredService<IHttpClientFactory>();

        foreach (var name in new[]
                 {
                     "charter-anthropic",
                     "charter-openai-compatible",
                     "charter-gemini",
                     "charter-openrouter-catalog",
                     "charter-github",
                     "charter-oauth",
                     "an-http-client-nobody-has-written-yet",
                 })
        {
            using var client = factory.CreateClient(name);

            var blocked = await Assert.ThrowsAsync<DemoModeException>(
                () => client.GetAsync(
                    new Uri("https://api.anthropic.com/v1/messages"),
                    TestContext.Current.CancellationToken));

            Assert.Contains("CHARTER_DEMO", blocked.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheSameClientsWorkNormallyWhenDemoModeIsOff()
    {
        // The switch has to be a switch. If the handler were installed unconditionally, every one of
        // these assertions would still pass and the feature would be a permanent outage.
        using var provider = Compose(demo: false).BuildServiceProvider();

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("charter-github");

        // Never sent: an unroutable address fails in the socket layer, which is proof enough that
        // nothing refused it first.
        var failure = await Assert.ThrowsAnyAsync<Exception>(
            () => client.GetAsync(
                new Uri("http://127.0.0.1:1/never-listens"),
                TestContext.Current.CancellationToken));

        Assert.IsNotType<DemoModeException>(failure);
    }

    [Fact]
    public async Task NoMailServerIsContactedInDemoMode()
    {
        // SMTP is the one egress path that is not an HttpClient, so it needs its own stop. The
        // provider degrades the way CHARTER_EMAIL_PROVIDER=none already does, and the transport
        // underneath it refuses outright for any future caller that resolves it directly.
        using var provider = Compose(demo: true).BuildServiceProvider();

        var email = provider.GetRequiredService<IEmailProvider>();

        Assert.IsType<NullEmailProvider>(email);
        Assert.False(email.IsEnabled);

        var transport = provider.GetRequiredService<ISmtpTransport>();
        Assert.IsType<DemoModeSmtpTransport>(transport);

        await Assert.ThrowsAsync<DemoModeException>(() => transport.SendAsync(
            new SmtpEnvelope
            {
                From = "charter@example.com",
                To = "someone@example.com",
                Message = "Subject: hello\r\n\r\nhello",
            },
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void SmtpStaysConnectedWhenDemoModeIsOff()
    {
        using var provider = Compose(demo: false).BuildServiceProvider();

        Assert.IsType<SmtpEmailProvider>(provider.GetRequiredService<IEmailProvider>());
        Assert.IsType<SmtpTransport>(provider.GetRequiredService<ISmtpTransport>());
    }

    [Fact]
    public void TheSeederIsRegisteredOnlyWhenDemoModeIsOn()
    {
        Assert.Contains(
            Compose(demo: true),
            descriptor => descriptor.ImplementationType == typeof(DemoSeedHostedService));

        Assert.DoesNotContain(
            Compose(demo: false),
            descriptor => descriptor.ImplementationType == typeof(DemoSeedHostedService));
    }

    [Fact]
    public void DemoModeIsNotASecondCodePath()
    {
        // Section 7.2's rule, applied to section 30.6. Demo mode is one composition-time decision;
        // if it grows into a flag that subsystems branch on, the demo path becomes the tested one and
        // the real path stops being.
        var services = Compose(demo: false);

        Assert.DoesNotContain(services, descriptor => descriptor.ImplementationType == typeof(DemoModeSmtpTransport));
        Assert.DoesNotContain(services, descriptor => descriptor.ImplementationType == typeof(NullEmailProvider));
    }

    [Fact]
    public void PreflightShowsTheKillSwitchToTheOperator()
    {
        var demo = new OutboundCallsPreflightCheck(ConfigTestEnvironment.Valid(("CHARTER_DEMO", "true"))).Run();
        var live = new OutboundCallsPreflightCheck(ConfigTestEnvironment.Valid()).Run();

        Assert.Contains("CHARTER_DEMO", demo.Detail, StringComparison.Ordinal);
        Assert.Contains("no model provider", demo.Detail, StringComparison.Ordinal);
        Assert.Contains("enabled", live.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DemoModeDoesNotDemandACredentialOrResolveAHostname()
    {
        // Two checks an evaluator cannot satisfy and does not need to: one asks for the token demo
        // mode exists to avoid spending, the other is a DNS lookup, which is itself an outbound call.
        var config = ConfigTestEnvironment.Valid(("CHARTER_DEMO", "true"), ("ANTHROPIC_API_KEY", null));

        Assert.False(config.OutboundCallsAllowed);
        Assert.False(config.ShouldCheckForUpdates);

        var probe = new UnreachableProbe();

        var credential = await new ModelCredentialPreflightCheck(config, probe)
            .RunAsync(TestContext.Current.CancellationToken);

        var baseUrl = await new BaseUrlPreflightCheck(config, new RefusingResolver())
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PreflightStatus.Skipped, credential.Status);
        Assert.Equal(PreflightStatus.Skipped, baseUrl.Status);
        Assert.Equal(0, probe.Queries);
    }

    private sealed class UnreachableProbe : IDatabaseProbe
    {
        public int Queries { get; private set; }

        public Task<bool> CanConnectAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<int> AppliedMigrationCountAsync(CancellationToken cancellationToken) => Task.FromResult(1);

        public Task<int> LinkedModelCredentialCountAsync(CancellationToken cancellationToken)
        {
            Queries++;
            return Task.FromResult(0);
        }
    }

    private sealed class RefusingResolver : IHostnameResolver
    {
        public Task<bool> CanResolveAsync(string host, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Demo mode must not resolve a hostname.");
    }

    [Fact]
    public async Task SeedingWritesAnOrganisationARepositoryAndCompletedSessions()
    {
        await using var fixture = await DemoDatabase.CreateAsync(TestContext.Current.CancellationToken);
        if (fixture.Skipped)
        {
            Assert.Skip(DemoDatabase.SkipReason);
            return;
        }

        var seeded = await new DemoSeeder(fixture.Database, TimeProvider.System)
            .SeedAsync(TestContext.Current.CancellationToken);

        Assert.True(seeded);

        var cancellationToken = TestContext.Current.CancellationToken;

        var organization = await fixture.Database.Organizations.SingleAsync(cancellationToken);
        Assert.Equal(DemoSeeder.OrganizationName, organization.Name);

        var repo = await fixture.Database.Repos.SingleAsync(cancellationToken);
        Assert.Equal(DemoSeeder.RepositoryFullName, repo.FullName);

        // Section 9: a repo is invisible to requesters until the smoke test passes, so a demo that
        // showed a Pending one would be demonstrating something an operator never sees.
        Assert.True(repo.IsRequesterVisible);
        Assert.NotNull(repo.PrimerMd);

        // Section 7.3, deny by default: the repo is requestable because a grant says so.
        Assert.NotEmpty(await fixture.Database.RepoScopes.ToListAsync(cancellationToken));

        var sessions = await fixture.Database.Sessions.ToListAsync(cancellationToken);
        Assert.Equal(2, sessions.Count);
        Assert.Contains(sessions, session => session.Status == SessionStatus.Merged);
        Assert.All(sessions, session => Assert.True(session.CostUsd > 0m));

        // "Realistic transcripts and artifacts" is the part of section 30.6 that does the work: the
        // three panes have nothing to render without them.
        foreach (var session in sessions)
        {
            var events = await fixture.Database.Events
                .Where(candidate => candidate.SessionId == session.Id)
                .OrderBy(candidate => candidate.Seq)
                .ToListAsync(cancellationToken);

            Assert.True(events.Count >= 10);
            Assert.Equal(Enumerable.Range(1, events.Count).Select(seq => (long)seq), events.Select(e => e.Seq));
            Assert.Equal(EventTypes.SessionStarted, events[0].Type);

            Assert.Equal(4, await fixture.Database.Milestones
                .CountAsync(milestone => milestone.SessionId == session.Id, cancellationToken));

            Assert.True(await fixture.Database.Recaps
                .AnyAsync(recap => recap.SessionId == session.Id, cancellationToken));

            var artifact = await fixture.Database.VerificationArtifacts
                .SingleAsync(candidate => candidate.SessionId == session.Id, cancellationToken);

            Assert.Equal(VerificationArtifactState.Ready, artifact.State);
            Assert.False(string.IsNullOrWhiteSpace(artifact.Url));
        }

        Assert.Equal(3, await fixture.Database.Requests.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task SeedingTwiceWritesNothingTheSecondTime()
    {
        await using var fixture = await DemoDatabase.CreateAsync(TestContext.Current.CancellationToken);
        if (fixture.Skipped)
        {
            Assert.Skip(DemoDatabase.SkipReason);
            return;
        }

        var seeder = new DemoSeeder(fixture.Database, TimeProvider.System);

        Assert.True(await seeder.SeedAsync(TestContext.Current.CancellationToken));

        // The hosted service runs on every boot, and a container restarts. A second organisation
        // would also be a section 7.2a violation: an instance serves exactly one.
        Assert.False(await new DemoSeeder(fixture.Database, TimeProvider.System)
            .SeedAsync(TestContext.Current.CancellationToken));

        Assert.Equal(1, await fixture.Database.Organizations.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SeedingIsSkippedOnAnInstanceThatIsAlreadyInUse()
    {
        await using var fixture = await DemoDatabase.CreateAsync(TestContext.Current.CancellationToken);
        if (fixture.Skipped)
        {
            Assert.Skip(DemoDatabase.SkipReason);
            return;
        }

        fixture.Database.Organizations.Add(Organization.Create("A real customer"));
        await fixture.Database.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.False(await new DemoSeeder(fixture.Database, TimeProvider.System)
            .SeedAsync(TestContext.Current.CancellationToken));

        Assert.Empty(await fixture.Database.Repos.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheSeederHostedServiceRunsTheSeeder()
    {
        await using var fixture = await DemoDatabase.CreateAsync(TestContext.Current.CancellationToken);
        if (fixture.Skipped)
        {
            Assert.Skip(DemoDatabase.SkipReason);
            return;
        }

        var services = new ServiceCollection();
        services.AddSingleton(fixture.Database);

        using var provider = services.BuildServiceProvider();

        var hosted = new DemoSeedHostedService(
            provider,
            TimeProvider.System,
            NullLogger<DemoSeedHostedService>.Instance);

        await hosted.StartAsync(TestContext.Current.CancellationToken);
        await hosted.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, await fixture.Database.Organizations.CountAsync(TestContext.Current.CancellationToken));
    }
}

/// <summary>A throwaway migrated database for the seeding tests, or a skip when there is none.</summary>
internal sealed class DemoDatabase : IAsyncDisposable
{
    public const string SkipReason =
        "Set CHARTER_TEST_DATABASE_URL to a throwaway Postgres to run the demo seeding tests.";

    private readonly string? administrative;
    private readonly string? name;

    private DemoDatabase(CharterDbContext database, string? administrative, string? name)
    {
        Database = database;
        this.administrative = administrative;
        this.name = name;
    }

    public CharterDbContext Database { get; }

    public bool Skipped => name is null;

    public static async Task<DemoDatabase> CreateAsync(CancellationToken cancellationToken)
    {
        var url = Environment.GetEnvironmentVariable("CHARTER_TEST_DATABASE_URL");
        if (string.IsNullOrWhiteSpace(url))
        {
            return new DemoDatabase(null!, null, null);
        }

        var administrative = DatabaseUrl.ToNpgsql(url);
        var name = "charter_demo_" + Guid.NewGuid().ToString("n")[..12];

        await using (var connection = new Npgsql.NpgsqlConnection(administrative))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new Npgsql.NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var target = new UriBuilder(url) { Path = "/" + name }.Uri.ToString();

        var options = new DbContextOptionsBuilder<CharterDbContext>();
        DataServiceCollectionExtensions.ConfigureNpgsql(options, DatabaseUrl.ToNpgsql(target));

        var database = new CharterDbContext(options.Options);
        await database.Database.MigrateAsync(cancellationToken);

        return new DemoDatabase(database, administrative, name);
    }

    public async ValueTask DisposeAsync()
    {
        if (name is null)
        {
            return;
        }

        await Database.DisposeAsync();
        Npgsql.NpgsqlConnection.ClearAllPools();

        await using var connection = new Npgsql.NpgsqlConnection(administrative);
        await connection.OpenAsync();

        await using var command = new Npgsql.NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)",
            connection);

        await command.ExecuteNonQueryAsync();
    }
}
