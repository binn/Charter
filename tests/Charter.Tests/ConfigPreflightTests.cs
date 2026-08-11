using Charter.Configuration;
using Charter.Configuration.Preflight;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Charter.Tests;

/// <summary>
/// Covers the first-run checks of section 30.1: database reachable, migrations applied, base URL
/// resolves, a model credential is valid, keys long enough - each with remediation an operator can
/// act on, and none of it inside the configuration parser.
/// </summary>
public class ConfigPreflightTests
{
    private sealed class FakeDatabaseProbe : IDatabaseProbe
    {
        public bool Connects { get; set; } = true;

        public int Migrations { get; set; } = 4;

        public int Credentials { get; set; }

        public int CredentialQueries { get; private set; }

        public Task<bool> CanConnectAsync(CancellationToken cancellationToken)
            => Connects
                ? Task.FromResult(true)
                : Task.FromException<bool>(new NpgsqlException("connection refused"));

        public Task<int> AppliedMigrationCountAsync(CancellationToken cancellationToken)
            => Connects
                ? Task.FromResult(Migrations)
                : Task.FromException<int>(new NpgsqlException("connection refused"));

        public Task<int> LinkedModelCredentialCountAsync(CancellationToken cancellationToken)
        {
            CredentialQueries++;
            return Connects
                ? Task.FromResult(Credentials)
                : Task.FromException<int>(new NpgsqlException("connection refused"));
        }
    }

    private sealed class FakeHostnameResolver(bool resolves) : IHostnameResolver
    {
        public Task<bool> CanResolveAsync(string host, CancellationToken cancellationToken)
            => Task.FromResult(resolves);
    }

    private sealed class ExplodingCheck : IPreflightCheck
    {
        public string Name => "exploding";

        public bool RequiresIo => false;

        public ValueTask<PreflightResult> RunAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("boom");
    }

    private static PreflightRunner Runner(
        CharterConfig config,
        IDatabaseProbe probe,
        bool resolves = true) => new(
    [
        new KeyStrengthPreflightCheck(config),
        new BaseUrlPreflightCheck(config, new FakeHostnameResolver(resolves)),
        new DatabaseConnectivityPreflightCheck(config, probe),
        new MigrationsPreflightCheck(probe),
        new ModelCredentialPreflightCheck(config, probe),
    ]);

    [Fact]
    public async Task ReportsEveryCheckOnAHealthyInstance()
    {
        var config = ConfigTestEnvironment.Valid();

        var report = await Runner(config, new FakeDatabaseProbe()).RunAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(report.Passed);
        Assert.Equal(5, report.Results.Count);
        Assert.All(report.Results, result => Assert.Equal(PreflightStatus.Passed, result.Status));
        Assert.Equal(
            ["secret keys", "base URL", "database", "migrations", "model credential"],
            report.Results.Select(result => result.Name).ToArray());
    }

    [Fact]
    public async Task SeparatesChecksThatNeedIoFromThoseThatDoNot()
    {
        // The parser makes no network call, so preflight has to be runnable in a mode that makes
        // none either - for a config-only check in CI, or before the database exists.
        var config = ConfigTestEnvironment.Valid();

        var report = await Runner(config, new FakeDatabaseProbe()).RunAsync(
            PreflightScope.PureOnly,
            TestContext.Current.CancellationToken);

        Assert.True(report.Passed);
        Assert.Equal(PreflightStatus.Passed, report.Results[0].Status);
        Assert.All(report.Results.Skip(1), result => Assert.Equal(PreflightStatus.Skipped, result.Status));
    }

    [Fact]
    public void PassesTheKeyCheckOnDistinctStrongKeys()
    {
        var result = new KeyStrengthPreflightCheck(ConfigTestEnvironment.Valid()).Run();

        Assert.Equal(PreflightStatus.Passed, result.Status);
        Assert.False(new KeyStrengthPreflightCheck(ConfigTestEnvironment.Valid()).RequiresIo);
    }

    [Fact]
    public void FailsTheKeyCheckWhenTheKeysAreIdentical()
    {
        var valid = ConfigTestEnvironment.Valid();
        var config = valid with
        {
            Keys = new KeyConfig
            {
                SecretKey = valid.Keys.SecretKey,
                CredentialKey = valid.Keys.SecretKey,
            },
        };

        var result = new KeyStrengthPreflightCheck(config).Run();

        Assert.Equal(PreflightStatus.Failed, result.Status);
        Assert.NotNull(result.Remediation);
        Assert.Contains("CHARTER_CREDENTIAL_KEY", result.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public void FailsTheKeyCheckWhenAKeyIsTooShort()
    {
        var valid = ConfigTestEnvironment.Valid();
        var config = valid with
        {
            Keys = new KeyConfig
            {
                SecretKey = new Secret("too-short"),
                CredentialKey = valid.Keys.CredentialKey,
            },
        };

        var result = new KeyStrengthPreflightCheck(config).Run();

        Assert.Equal(PreflightStatus.Failed, result.Status);
        Assert.Contains("openssl rand -base64 32", result.Remediation!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailsWhenTheBaseUrlDoesNotResolve()
    {
        var config = ConfigTestEnvironment.Valid();
        var check = new BaseUrlPreflightCheck(config, new FakeHostnameResolver(resolves: false));

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PreflightStatus.Failed, result.Status);
        Assert.True(check.RequiresIo);
        Assert.Contains("charter.example.com", result.Detail, StringComparison.Ordinal);
        Assert.Contains("CHARTER_BASE_URL", result.Remediation!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailsWhenTheDatabaseIsUnreachable()
    {
        var config = ConfigTestEnvironment.Valid();
        var probe = new FakeDatabaseProbe { Connects = false };

        var report = await Runner(config, probe).RunAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        // Connectivity and migrations fail; the model credential check still passes, because an
        // instance-level key answers it without asking the database.
        Assert.False(report.Passed);
        Assert.Equal(2, report.Failures.Count);
        var database = Assert.Single(report.Failures, result => result.Name == "database");
        Assert.Contains("DATABASE_URL", database.Remediation!, StringComparison.Ordinal);
        Assert.Contains("[FAIL] database", report.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The login role from <c>DATABASE_URL</c> reaches the operator.
    /// </summary>
    /// <remarks>
    /// It was parsed alongside host, port and database and then shown nowhere, which is a shame,
    /// because "connected as which role" is the whole answer to a migration that cannot create
    /// tables - a failure that looks nothing like the connectivity failure this check otherwise
    /// reports.
    /// </remarks>
    [Fact]
    public async Task TheDatabaseCheckNamesTheRoleItConnectedAs()
    {
        var config = ConfigTestEnvironment.Valid(
            ("DATABASE_URL", "postgres://charter_app:secret@db.internal:6432/charter"));

        var result = await new DatabaseConnectivityPreflightCheck(config, new FakeDatabaseProbe())
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PreflightStatus.Passed, result.Status);
        Assert.Contains("charter_app", result.Detail, StringComparison.Ordinal);
        Assert.Contains("db.internal:6432", result.Detail, StringComparison.Ordinal);

        // Never the password, which travels in the same URL.
        Assert.DoesNotContain("secret", result.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Section 4.2 accepts the GitHub App key as PEM or as base64 PEM, and the parser was the only
    /// thing that knew which.
    /// </summary>
    /// <remarks>
    /// A key encoded twice decodes to something that is not PEM, and the only symptom is that every
    /// GitHub call fails to sign. Saying which encoding was accepted turns that into a one-line
    /// diagnosis instead of a hunt.
    /// </remarks>
    [Fact]
    public async Task TheGitHubAppCheckSaysHowThePrivateKeyArrived()
    {
        var pem = ConfigTestEnvironment.Valid().GitHub.PrivateKeyPem.Reveal();
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(pem));

        var asPem = await new GitHubAppPreflightCheck(ConfigTestEnvironment.Valid())
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PreflightStatus.Passed, asPem.Status);
        Assert.Contains("PEM", asPem.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("base64", asPem.Detail, StringComparison.Ordinal);

        var asBase64 = await new GitHubAppPreflightCheck(
                ConfigTestEnvironment.Valid(("GITHUB_APP_PRIVATE_KEY", encoded)))
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PreflightStatus.Passed, asBase64.Status);
        Assert.Contains("base64", asBase64.Detail, StringComparison.Ordinal);
        Assert.Contains("encoded twice", asBase64.Detail, StringComparison.Ordinal);

        // Never the key itself, in either encoding.
        Assert.DoesNotContain("PRIVATE KEY-----", asBase64.Detail, StringComparison.Ordinal);
    }

    /// <summary>A key encoded twice is refused at startup, with the cause named.</summary>
    [Fact]
    public void ADoubleEncodedPrivateKeyIsRefusedWithAHint()
    {
        var pem = ConfigTestEnvironment.Valid().GitHub.PrivateKeyPem.Reveal();
        var once = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(pem));
        var twice = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(once));

        var result = CharterConfigParser.Parse(
            ConfigTestEnvironment.With(("GITHUB_APP_PRIVATE_KEY", twice)));

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            problem => problem.Text.Contains("encoded twice", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-1, "does not exist")]
    [InlineData(0, "empty")]
    public async Task FailsWhenMigrationsHaveNotBeenApplied(int applied, string expected)
    {
        var probe = new FakeDatabaseProbe { Migrations = applied };

        var result = await new MigrationsPreflightCheck(probe).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PreflightStatus.Failed, result.Status);
        Assert.Contains(expected, result.Detail, StringComparison.Ordinal);
        Assert.NotNull(result.Remediation);
    }

    [Fact]
    public async Task AcceptsAnInstanceLevelModelCredentialWithoutTouchingTheDatabase()
    {
        var probe = new FakeDatabaseProbe();
        var check = new ModelCredentialPreflightCheck(ConfigTestEnvironment.Valid(), probe);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PreflightStatus.Passed, result.Status);
        Assert.Equal(0, probe.CredentialQueries);
    }

    [Fact]
    public async Task AcceptsALinkedCredentialGrantInsteadOfAnInstanceKey()
    {
        // Section 4.2 footnote *: the database is the other half of this requirement, which is why
        // the parser only warns and this check decides.
        var config = ConfigTestEnvironment.Valid(("ANTHROPIC_API_KEY", null));
        var probe = new FakeDatabaseProbe { Credentials = 2 };

        var result = await new ModelCredentialPreflightCheck(config, probe)
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PreflightStatus.Passed, result.Status);
        Assert.Equal(1, probe.CredentialQueries);
        Assert.Contains("2 credential grant(s)", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailsWhenNoModelCredentialExistsAnywhere()
    {
        var config = ConfigTestEnvironment.Valid(("ANTHROPIC_API_KEY", null));
        var probe = new FakeDatabaseProbe { Credentials = -1 };

        var result = await new ModelCredentialPreflightCheck(config, probe)
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PreflightStatus.Failed, result.Status);
        Assert.Contains("ANTHROPIC_API_KEY", result.Remediation!, StringComparison.Ordinal);
        Assert.Contains("OPENROUTER_API_KEY", result.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsEveryFailureAtOnceRatherThanTheFirst()
    {
        // Section 4.1's rule for configuration, applied to preflight: an operator who fixes one
        // variable, redeploys, and is then told about the next one has been made to pay for a round
        // trip Charter already knew about. Four things are wrong here and all four are named.
        var valid = ConfigTestEnvironment.Valid(("ANTHROPIC_API_KEY", null));
        var config = valid with
        {
            Keys = new KeyConfig
            {
                SecretKey = new Secret("too-short"),
                CredentialKey = valid.Keys.CredentialKey,
            },
        };

        var report = await Runner(config, new FakeDatabaseProbe { Connects = false }, resolves: false)
            .RunAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            ["secret keys", "base URL", "database", "migrations", "model credential"],
            report.Failures.Select(failure => failure.Name).ToArray());

        // Every one of them carries what to change, which is the other half of section 30.1.
        Assert.All(report.Failures, failure => Assert.False(string.IsNullOrWhiteSpace(failure.Remediation)));

        var described = report.Describe();
        Assert.All(report.Failures, failure => Assert.Contains(failure.Name, described, StringComparison.Ordinal));
    }

    [Fact]
    public async Task StopsTheBootForTheDatabaseAndWarnsForTheBaseUrl()
    {
        // The deliberate split. An unreachable database is observed against the very resource that
        // has to work, so a failure is conclusive and nothing functions without it. The public base
        // URL is resolved by GitHub and by browsers, not by this container - split-horizon DNS and a
        // PaaS private network both make the in-container lookup fail on a healthy instance - so it
        // is shouted and booted through.
        var config = ConfigTestEnvironment.Valid();

        var databaseDown = await Runner(config, new FakeDatabaseProbe { Connects = false })
            .RunAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(databaseDown.ShouldHalt);
        Assert.Contains(databaseDown.BlockingFailures, failure => failure.Name == "database");

        var dnsDown = await Runner(config, new FakeDatabaseProbe(), resolves: false)
            .RunAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(dnsDown.Passed);
        Assert.False(dnsDown.ShouldHalt);
        Assert.Equal("base URL", Assert.Single(dnsDown.Advisories).Name);

        // The operator has to be able to tell the two apart in the log without reading the source.
        Assert.Contains("[WARN] base URL", dnsDown.Describe(), StringComparison.Ordinal);
        Assert.Contains("[FAIL] database", databaseDown.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheHostedServiceRefusesToStartOnABlockingFailureAndNamesThemAll()
    {
        var config = ConfigTestEnvironment.Valid(("ANTHROPIC_API_KEY", null));
        var probe = new FakeDatabaseProbe { Connects = false };

        var service = new PreflightHostedService(
            Runner(config, probe),
            NullLogger<PreflightHostedService>.Instance);

        var failure = await Assert.ThrowsAsync<PreflightException>(
            () => service.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("database", failure.Message, StringComparison.Ordinal);
        Assert.Contains("migrations", failure.Message, StringComparison.Ordinal);
        Assert.Contains("model credential", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheHostedServiceStartsThroughAnAdvisoryFailure()
    {
        var config = ConfigTestEnvironment.Valid();

        var service = new PreflightHostedService(
            Runner(config, new FakeDatabaseProbe(), resolves: false),
            NullLogger<PreflightHostedService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ARunnerStampsEachCheckSeverityOntoItsResult()
    {
        // The severity lives on the check, not on every return path inside it, so a check cannot
        // report one of its several failures at the wrong severity by forgetting to say.
        var report = await Runner(ConfigTestEnvironment.Valid(), new FakeDatabaseProbe { Connects = false })
            .RunAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            PreflightSeverity.Advisory,
            Assert.Single(report.Results, result => result.Name == "base URL").Severity);

        Assert.All(
            report.Results.Where(result => result.Name != "base URL"),
            result => Assert.Equal(PreflightSeverity.Blocking, result.Severity));
    }

    [Fact]
    public async Task TurnsAThrowingCheckIntoAFailedResult()
    {
        // Preflight exists to explain what is wrong; a broken check must not become an unhandled
        // exception that hides the other four results.
        var runner = new PreflightRunner([new ExplodingCheck(), new KeyStrengthPreflightCheck(ConfigTestEnvironment.Valid())]);

        var report = await runner.RunAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(report.Passed);
        Assert.Equal(2, report.Results.Count);
        Assert.Contains("boom", report.Failures[0].Detail, StringComparison.Ordinal);
        Assert.Equal(PreflightStatus.Passed, report.Results[1].Status);
    }
}
