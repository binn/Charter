using Charter.Configuration;
using Npgsql;

namespace Charter.Tests;

/// <summary>
/// Covers the <c>DATABASE_URL</c> to Npgsql conversion required by specification section 4.3.
/// </summary>
/// <remarks>
/// Assertions go through <see cref="NpgsqlConnectionStringBuilder"/> rather than comparing raw
/// strings, so a change in Npgsql's keyword ordering or casing does not produce a false failure.
/// </remarks>
public class DatabaseUrlTests
{
    private static NpgsqlConnectionStringBuilder Parse(string url)
        => new(DatabaseUrl.ToNpgsql(url));

    [Theory]
    [InlineData("postgres://charter:hunter2@db.internal:5432/charter")]
    [InlineData("postgresql://charter:hunter2@db.internal:5432/charter")]
    public void AcceptsBothPostgresSchemes(string url)
    {
        var built = Parse(url);

        Assert.Equal("db.internal", built.Host);
        Assert.Equal(5432, built.Port);
        Assert.Equal("charter", built.Database);
        Assert.Equal("charter", built.Username);
        Assert.Equal("hunter2", built.Password);
    }

    [Theory]
    [InlineData("mysql://charter:hunter2@db.internal:3306/charter")]
    [InlineData("http://charter:hunter2@db.internal/charter")]
    [InlineData("postgres-ssl://charter@db.internal/charter")]
    public void RejectsOtherSchemes(string url)
    {
        var error = Assert.Throws<ConfigException>(() => DatabaseUrl.ToNpgsql(url));

        Assert.Contains("postgres://", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultsThePortWhenAbsent()
    {
        var built = Parse("postgres://charter:hunter2@db.internal/charter");

        Assert.Equal(DatabaseUrl.DefaultPort, built.Port);
        Assert.Equal(5432, built.Port);
    }

    [Fact]
    public void KeepsAnExplicitNonDefaultPort()
    {
        var built = Parse("postgres://charter:hunter2@db.internal:6543/charter");

        Assert.Equal(6543, built.Port);
    }

    [Theory]
    // Passwords routinely contain @, / and :, all of which have to arrive percent-encoded and be
    // decoded before Npgsql sees them.
    [InlineData("p%40ssword", "p@ssword")]
    [InlineData("a%2Fb", "a/b")]
    [InlineData("colon%3Ainside", "colon:inside")]
    [InlineData("sp%20ace", "sp ace")]
    [InlineData("%C3%BCmlaut", "ümlaut")]
    [InlineData("percent%25sign", "percent%sign")]
    public void UrlDecodesThePassword(string encoded, string expected)
    {
        var built = Parse($"postgres://charter:{encoded}@db.internal/charter");

        Assert.Equal(expected, built.Password);
    }

    [Fact]
    public void UrlDecodesTheUsername()
    {
        var built = Parse("postgres://tenant%40acme:hunter2@db.internal/charter");

        Assert.Equal("tenant@acme", built.Username);
    }

    [Fact]
    public void HandlesAUrlWithNoCredentials()
    {
        var built = Parse("postgres://db.internal/charter");

        Assert.Equal("db.internal", built.Host);
        Assert.Equal("charter", built.Database);
        Assert.True(string.IsNullOrEmpty(built.Username));
        Assert.True(string.IsNullOrEmpty(built.Password));
    }

    [Fact]
    public void HandlesAUsernameWithNoPassword()
    {
        var built = Parse("postgres://charter@db.internal/charter");

        Assert.Equal("charter", built.Username);
        Assert.True(string.IsNullOrEmpty(built.Password));
    }

    [Fact]
    public void MapsSslModeDisable()
    {
        var built = Parse("postgres://charter:hunter2@db.internal/charter?sslmode=disable");

        Assert.Equal(SslMode.Disable, built.SslMode);
    }

    [Fact]
    public void MapsSslModeVerifyFull()
    {
        var built = Parse("postgres://charter:hunter2@db.internal/charter?sslmode=verify-full");

        Assert.Equal(SslMode.VerifyFull, built.SslMode);
    }

    [Fact]
    public void MapsSslModeRequireToEncryptionWithoutCertificateValidation()
    {
        // Section 4.3 wants `require` to encrypt without proving the server's identity. Npgsql 8
        // moved that behaviour into SslMode.Require itself and Npgsql 10 marks the old
        // TrustServerCertificate keyword obsolete, so Require alone is the correct mapping now.
        var connectionString = DatabaseUrl.ToNpgsql("postgres://charter:hunter2@db.internal/charter?sslmode=require");

        Assert.Equal(SslMode.Require, new NpgsqlConnectionStringBuilder(connectionString).SslMode);
        Assert.DoesNotContain("Trust Server Certificate", connectionString, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("postgres://charter:hunter2@db.internal/charter")]
    [InlineData("postgres://charter:hunter2@db.internal/charter?sslmode=")]
    [InlineData("postgres://charter:hunter2@db.internal/charter?application_name=charter")]
    public void DefaultsToSslModeRequireWhenUnspecified(string url)
    {
        var built = Parse(url);

        Assert.Equal(SslMode.Require, built.SslMode);
    }

    [Theory]
    [InlineData("allow")]
    [InlineData("prefer")]
    [InlineData("verify-ca")]
    [InlineData("REQUIRE")]
    public void RejectsAnUnsupportedSslMode(string sslMode)
    {
        var error = Assert.Throws<ConfigException>(
            () => DatabaseUrl.ToNpgsql($"postgres://charter:hunter2@db.internal/charter?sslmode={sslMode}"));

        Assert.Contains("sslmode", error.Message, StringComparison.Ordinal);
        Assert.Contains(sslMode, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("postgres://charter:hunter2@db.internal")]
    [InlineData("postgres://charter:hunter2@db.internal/")]
    [InlineData("postgres://db.internal/?sslmode=require")]
    public void RejectsAMissingDatabaseName(string url)
    {
        var error = Assert.Throws<ConfigException>(() => DatabaseUrl.ToNpgsql(url));

        Assert.Equal("DATABASE_URL is missing a database name", error.Message);
        Assert.Single(error.Problems);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsAnEmptyValue(string url)
    {
        Assert.Throws<ConfigException>(() => DatabaseUrl.ToNpgsql(url));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("charter")]
    [InlineData("/var/run/postgresql")]
    public void RejectsSomethingThatIsNotAUrl(string url)
    {
        Assert.Throws<ConfigException>(() => DatabaseUrl.ToNpgsql(url));
    }

    [Fact]
    public void ProducesAConnectionStringNpgsqlCanRoundTrip()
    {
        // The whole point of the conversion: Npgsql does not accept URI form, so the output has to
        // be a keyword/value string it parses back to the same values.
        const string url = "postgresql://charter:p%40ss%2Fword@db.internal:6543/charter_prod?sslmode=verify-full";

        var connectionString = DatabaseUrl.ToNpgsql(url);
        var built = new NpgsqlConnectionStringBuilder(connectionString);

        Assert.Equal("db.internal", built.Host);
        Assert.Equal(6543, built.Port);
        Assert.Equal("charter_prod", built.Database);
        Assert.Equal("charter", built.Username);
        Assert.Equal("p@ss/word", built.Password);
        Assert.Equal(SslMode.VerifyFull, built.SslMode);
    }
}
