using Charter.Configuration;
using Charter.Configuration.Preflight;
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
