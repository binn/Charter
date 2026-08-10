using System.Text;
using Charter.Configuration;
using Charter.Logging;
using Serilog.Events;

namespace Charter.Tests;

/// <summary>
/// A usable environment, plus the machinery to break one variable at a time.
/// </summary>
internal static class ConfigTestEnvironment
{
    /// <summary>44 characters of base64, 32 bytes decoded - what <c>openssl rand -base64 32</c> gives.</summary>
    public static string SecretKey { get; } = Convert.ToBase64String([.. Enumerable.Range(0, 32).Select(i => (byte)i)]);

    /// <summary>A different 32 bytes: section 20b.2 requires the two keys to differ.</summary>
    public static string CredentialKey { get; } = Convert.ToBase64String([.. Enumerable.Range(100, 32).Select(i => (byte)i)]);

    /// <summary>A PEM block. Preflight never parses the key material, only the framing.</summary>
    public const string PrivateKeyPem =
        "-----BEGIN RSA PRIVATE KEY-----\nMIIBOgIBAAJBAKj34GkxFhD90vcNLYLI\n-----END RSA PRIVATE KEY-----";

    /// <summary>Every variable section 4.2 marks required, with a model credential.</summary>
    public static Dictionary<string, string> Required() => new(StringComparer.Ordinal)
    {
        ["DATABASE_URL"] = "postgres://charter:hunter2@db.internal:5432/charter",
        ["CHARTER_BASE_URL"] = "https://charter.example.com",
        ["CHARTER_SECRET_KEY"] = SecretKey,
        ["CHARTER_CREDENTIAL_KEY"] = CredentialKey,
        ["GITHUB_APP_ID"] = "123456",
        ["GITHUB_APP_PRIVATE_KEY"] = PrivateKeyPem,
        ["GITHUB_WEBHOOK_SECRET"] = "webhook-secret-value",
        ["ANTHROPIC_API_KEY"] = "sk-ant-instance-key",
    };

    /// <summary>The required set with <paramref name="overrides"/> applied; a null value unsets.</summary>
    public static Func<string, string?> With(params (string Key, string? Value)[] overrides)
    {
        var values = Required();
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

        return Read(values);
    }

    /// <summary>Only the variables given - nothing else is set.</summary>
    public static Func<string, string?> Only(params (string Key, string Value)[] pairs)
        => Read(pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    /// <summary>A parsed config from the required set plus <paramref name="overrides"/>.</summary>
    public static CharterConfig Valid(params (string Key, string? Value)[] overrides)
    {
        var result = CharterConfigParser.Parse(With(overrides));
        Assert.True(
            result.IsValid,
            "expected a valid config, got: " + string.Join("; ", result.Errors.Select(problem => problem.Text)));
        return result.Config!;
    }

    private static Func<string, string?> Read(Dictionary<string, string> values)
        => name => values.GetValueOrDefault(name);
}

/// <summary>
/// Covers the section 4.1 contract for the full <see cref="CharterConfig"/>: validate everything at
/// once, name the variable, say what was expected, and never fail lazily on first use.
/// </summary>
public class ConfigValidationTests
{
    [Fact]
    public void ParsesTheFullHappyPath()
    {
        var config = ConfigTestEnvironment.Valid(
            ("PORT", "3000"),
            ("CHARTER_MODE", "organization"),
            ("CHARTER_RUNNER", "agent,docker"),
            ("CHARTER_MODEL_BUILD", "openrouter/deepseek/deepseek-r1"),
            ("CHARTER_ALLOW_SHARED_POOL", "true"),
            ("LOGGING_MODE", "RAILWAY_JSON"),
            ("CHARTER_LOG_LEVEL", "debug"),
            ("CHARTER_SEQ_URL", "http://seq:5341"),
            ("CHARTER_SEQ_API_KEY", "seq-key"),
            ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4317"),
            ("OTEL_SERVICE_NAME", "charter-prod"),
            ("CHARTER_OAUTH_GITHUB_ID", "gh-id"),
            ("CHARTER_OAUTH_GITHUB_SECRET", "gh-secret"),
            ("CHARTER_SAML_METADATA_URL", "https://idp.example.com/metadata"),
            ("CHARTER_SMTP_URL", "smtp://mailer:p%40ss@smtp.example.com:2525"),
            ("CHARTER_DEFAULT_SESSION_BUDGET_USD", "12.50"),
            ("CHARTER_DEFAULT_MONTHLY_BUDGET_USD", "250"),
            ("CHARTER_STORAGE_ENDPOINT", "https://minio.internal:9000"),
            ("CHARTER_STORAGE_BUCKET", "charter-artifacts"),
            ("CHARTER_STORAGE_ACCESS_KEY", "storage-access"),
            ("CHARTER_STORAGE_SECRET_KEY", "storage-secret"),
            ("CHARTER_STORAGE_REGION", "us-east-1"),
            ("CHARTER_STORAGE_FORCE_PATH_STYLE", "false"),
            ("CHARTER_UPDATE_CHECK", "false"),
            ("CHARTER_UPDATE_CHANNEL", "prerelease"),
            ("CHARTER_ALLOW_REPO_CREATION", "true"),
            ("CHARTER_LOG_INCLUDE_TRANSCRIPTS", "true"),
            ("CHARTER_DEMO", "true"));

        Assert.Equal(3000, config.Port);
        Assert.Equal(new Uri("https://charter.example.com"), config.BaseUrl);
        Assert.Equal(CharterMode.Organization, config.Mode);
        Assert.Equal([RunnerBackend.Agent, RunnerBackend.Docker], config.Runners);

        Assert.Equal("db.internal", config.Database.Host);
        Assert.Equal(5432, config.Database.Port);
        Assert.Equal("charter", config.Database.Database);

        Assert.Equal("openrouter/anthropic/claude-sonnet-5", config.Models.Refine.Qualified);
        Assert.Equal("openrouter", config.Models.Build.Provider);
        Assert.Equal("deepseek/deepseek-r1", config.Models.Build.Model);
        Assert.True(config.Models.AllowSharedPool);
        Assert.True(config.Models.HasInstanceCredential);

        Assert.Equal(123456, config.GitHub.AppId);
        Assert.Contains("BEGIN RSA PRIVATE KEY", config.GitHub.PrivateKeyPem.Reveal(), StringComparison.Ordinal);

        Assert.Equal(LoggingMode.RailwayJson, config.Logging.Mode);
        Assert.Equal(LogEventLevel.Debug, config.Logging.MinimumLevel);
        Assert.True(config.Logging.SeqEnabled);
        Assert.True(config.Logging.IncludeTranscripts);
        Assert.True(config.Telemetry.OtlpEnabled);
        Assert.Equal("charter-prod", config.Telemetry.ServiceName);

        var github = config.Auth.Provider("github");
        Assert.NotNull(github);
        Assert.Equal("gh-id", github.ClientId);
        Assert.NotNull(config.Auth.SamlMetadataUrl);

        Assert.NotNull(config.Smtp);
        Assert.Equal("smtp.example.com", config.Smtp.Host);
        Assert.Equal(2525, config.Smtp.Port);
        Assert.Equal("mailer", config.Smtp.Username);
        Assert.Equal("p@ss", config.Smtp.Password!.Reveal());

        Assert.Equal(12.50m, config.Budgets.DefaultSessionUsd);
        Assert.Equal(250m, config.Budgets.DefaultMonthlyUsd);

        Assert.NotNull(config.Storage);
        Assert.Equal("charter-artifacts", config.Storage.Bucket);
        Assert.Equal("us-east-1", config.Storage.Region);
        Assert.False(config.Storage.ForcePathStyle);

        Assert.False(config.UpdateCheck.Enabled);
        Assert.Equal(UpdateChannel.Prerelease, config.UpdateCheck.Channel);
        Assert.True(config.AllowRepoCreation);
        Assert.True(config.DemoMode);
        Assert.False(config.OutboundCallsAllowed);
        Assert.False(config.ShouldCheckForUpdates);
    }

    [Fact]
    public void AppliesEveryDocumentedDefault()
    {
        var config = ConfigTestEnvironment.Valid();

        Assert.Equal(8080, config.Port);
        Assert.Equal(CharterMode.Personal, config.Mode);
        Assert.Equal([RunnerBackend.GitHubActions], config.Runners);
        // Section 4.2: the two control-plane models default to OpenRouter, the build model does not -
        // it is dispatched to an agent CLI whose adapter decides what it can authenticate against.
        Assert.Equal("openrouter/anthropic/claude-sonnet-5", config.Models.Refine.Qualified);
        Assert.Equal("anthropic/claude-opus-5", config.Models.Build.Qualified);
        Assert.Equal("openrouter/anthropic/claude-sonnet-5", config.Models.Teach.Qualified);
        Assert.False(config.Models.AllowSharedPool);
        Assert.Equal(LoggingMode.Default, config.Logging.Mode);
        Assert.Equal(LogEventLevel.Information, config.Logging.MinimumLevel);
        Assert.False(config.Logging.IncludeTranscripts);
        Assert.Equal("charter", config.Telemetry.ServiceName);
        Assert.Equal("grpc", config.Telemetry.OtlpProtocol);
        Assert.Empty(config.Auth.OAuthProviders);
        Assert.Null(config.Auth.SamlMetadataUrl);
        Assert.Null(config.Smtp);
        Assert.Equal(5.00m, config.Budgets.DefaultSessionUsd);
        Assert.Equal(100.00m, config.Budgets.DefaultMonthlyUsd);
        Assert.Null(config.Storage);
        Assert.False(config.StorageEnabled);
        Assert.True(config.UpdateCheck.Enabled);
        Assert.Equal(UpdateChannel.Stable, config.UpdateCheck.Channel);
        Assert.False(config.AllowRepoCreation);
        Assert.False(config.DemoMode);
        Assert.True(config.ShouldCheckForUpdates);
    }

    [Fact]
    public void ReportsEveryMissingRequiredVariableAtOnce()
    {
        // Section 4.1: an operator with an empty environment should see the whole list, not the first
        // line of it, seven container restarts in a row.
        var result = CharterConfigParser.Parse(ConfigTestEnvironment.Only());

        Assert.False(result.IsValid);
        Assert.Null(result.Config);

        string[] required =
        [
            "DATABASE_URL", "CHARTER_BASE_URL", "CHARTER_SECRET_KEY", "CHARTER_CREDENTIAL_KEY",
            "GITHUB_APP_ID", "GITHUB_APP_PRIVATE_KEY", "GITHUB_WEBHOOK_SECRET",
        ];

        foreach (var variable in required)
        {
            Assert.Contains(result.Errors, problem => problem.Variable == variable);
        }
    }

    [Fact]
    public void ReportsFiveFaultsAsFiveProblems()
    {
        var result = CharterConfigParser.Parse(ConfigTestEnvironment.With(
            ("PORT", "not-a-port"),
            ("LOGGING_MODE", "LOGFMT"),
            ("CHARTER_MODE", "solo"),
            ("CHARTER_RUNNER", "kubernetes"),
            ("CHARTER_OAUTH_GITHUB_ID", "gh-id")));

        Assert.False(result.IsValid);
        Assert.Equal(5, result.Errors.Count);
        Assert.Equal(
            ["CHARTER_MODE", "CHARTER_OAUTH_GITHUB_SECRET", "CHARTER_RUNNER", "LOGGING_MODE", "PORT"],
            result.Errors.Select(problem => problem.Variable).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void EveryProblemNamesItsVariableAndTheExpectation()
    {
        var result = CharterConfigParser.Parse(ConfigTestEnvironment.With(("CHARTER_MODE", "solo")));

        var problem = Assert.Single(result.Errors);
        Assert.Equal("CHARTER_MODE", problem.Variable);
        Assert.Contains("CHARTER_MODE", problem.Text, StringComparison.Ordinal);
        Assert.Contains("personal, organization", problem.Text, StringComparison.Ordinal);
        Assert.Contains("solo", problem.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribesEveryProblemForStdout()
    {
        // This is what the operator sees before the process exits non-zero, printed before the
        // logging pipeline exists (section 4.1).
        var result = CharterConfigParser.Parse(ConfigTestEnvironment.With(
            ("CHARTER_MODE", "solo"),
            ("ANTHROPIC_API_KEY", null)));

        var described = result.Describe();

        Assert.Contains("Charter cannot start", described, StringComparison.Ordinal);
        Assert.Contains("  - CHARTER_MODE must be one of", described, StringComparison.Ordinal);
        Assert.Contains("Configuration warnings:", described, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowsWithEveryProblemWhenAskedForAConfigDirectly()
    {
        var error = Assert.Throws<ConfigException>(
            () => CharterConfig.FromEnvironment(ConfigTestEnvironment.With(
                ("CHARTER_BASE_URL", null),
                ("DATABASE_URL", null))));

        Assert.Equal(2, error.Problems.Count);
    }

    [Theory]
    [InlineData("postgres://charter:hunter2@db.internal/charter", true)]
    [InlineData("postgresql://charter@db.internal:6543/charter?sslmode=verify-full", true)]
    [InlineData("mysql://charter@db.internal/charter", false)]
    [InlineData("postgres://db.internal", false)]
    [InlineData("not-a-url", false)]
    public void ValidatesDatabaseUrlThroughTheSharedHelper(string url, bool valid)
    {
        var result = CharterConfigParser.Parse(ConfigTestEnvironment.With(("DATABASE_URL", url)));

        Assert.Equal(valid, result.IsValid);
        if (!valid)
        {
            Assert.Contains(result.Errors, problem => problem.Variable == "DATABASE_URL");
        }
    }

    [Fact]
    public void RequiresDatabaseUrl()
    {
        var result = CharterConfigParser.Parse(ConfigTestEnvironment.With(("DATABASE_URL", null)));

        var problem = Assert.Single(result.Errors);
        Assert.Equal("DATABASE_URL", problem.Variable);
        Assert.Contains("postgres://", problem.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("charter.example.com")]
    [InlineData("/charter")]
    [InlineData("ftp://charter.example.com")]
    public void RequiresAnAbsoluteBaseUrl(string value)
    {
        var result = CharterConfigParser.Parse(ConfigTestEnvironment.With(("CHARTER_BASE_URL", value)));

        var problem = Assert.Single(result.Errors);
        Assert.Equal("CHARTER_BASE_URL", problem.Variable);
        Assert.Contains("absolute", problem.Text, StringComparison.Ordinal);
    }

    [Theory]
    // 32 characters is not 32 bytes: this decodes as base64 to 24.
    [InlineData("abcdefghijklmnopqrstuvwxyz012345", false)]
    [InlineData("too-short", false)]
    [InlineData("", false)]
    // Not base64, so measured as UTF-8: 38 bytes.
    [InlineData("this-is-a-32-plus-byte-secret-value!!!", true)]
    public void MeasuresKeyEntropyInDecodedBytes(string key, bool valid)
    {
        var result = CharterConfigParser.Parse(ConfigTestEnvironment.With(("CHARTER_SECRET_KEY", key)));

        Assert.Equal(valid, result.IsValid);
        if (!valid)
        {
            var problem = Assert.Single(result.Errors);
            Assert.Equal("CHARTER_SECRET_KEY", problem.Variable);
            Assert.Contains("openssl rand -base64 32", problem.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ExplainsWhenAKeyOnlyLooksLongEnough()
    {
        var result = CharterConfigParser.Parse(
            ConfigTestEnvironment.With(("CHARTER_CREDENTIAL_KEY", "abcdefghijklmnopqrstuvwxyz012345")));

        var problem = Assert.Single(result.Errors);
        Assert.Contains("base64-decodes to 24 bytes", problem.Text, StringComparison.Ordinal);
        Assert.Contains("not 32 characters", problem.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsIdenticalSecretAndCredentialKeys()
    {
        // Section 20b.2: they are separate so cookie-key rotation does not invalidate every stored
        // credential. Setting them to the same value silently removes that property.
        var result = CharterConfigParser.Parse(
            ConfigTestEnvironment.With(("CHARTER_CREDENTIAL_KEY", ConfigTestEnvironment.SecretKey)));

        var problem = Assert.Single(result.Errors);
        Assert.Equal("CHARTER_CREDENTIAL_KEY", problem.Variable);
        Assert.Contains("must differ from CHARTER_SECRET_KEY", problem.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptsDistinctKeysOfSufficientEntropy()
    {
        var config = ConfigTestEnvironment.Valid();

        Assert.Equal(32, config.Keys.SecretKey.EntropyBytes);
        Assert.Equal(32, config.Keys.CredentialKey.EntropyBytes);
        Assert.NotEqual(config.Keys.SecretKey, config.Keys.CredentialKey);
    }

    [Fact]
    public void WarnsRatherThanFailsWhenNoInstanceModelCredentialIsSet()
    {
        // Section 4.2 footnote *: a linked CredentialGrant in the database satisfies this too, and
        // the parser cannot see the database. Saying "unusable" here would be a lie to the operator
        // who linked a credential in the UI; the preflight check settles it.
        var result = CharterConfigParser.Parse(ConfigTestEnvironment.With(
            ("ANTHROPIC_API_KEY", null),
            ("OPENROUTER_API_KEY", null)));

        Assert.True(result.IsValid);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("ANTHROPIC_API_KEY", warning.Text, StringComparison.Ordinal);
        Assert.Contains("OPENROUTER_API_KEY", warning.Text, StringComparison.Ordinal);
        Assert.Contains("link a credential in the database", warning.Text, StringComparison.Ordinal);
        Assert.False(result.Config!.Models.HasInstanceCredential);
    }

    [Fact]
    public void AcceptsOpenRouterAloneAsTheInstanceCredential()
    {
        var result = CharterConfigParser.Parse(ConfigTestEnvironment.With(
            ("ANTHROPIC_API_KEY", null),
            ("OPENROUTER_API_KEY", "sk-or-key")));

        Assert.True(result.IsValid);
        Assert.Empty(result.Warnings);
        Assert.True(result.Config!.Models.HasInstanceCredential);
    }

    [Theory]
    [InlineData("DEFAULT", LoggingMode.Default)]
    [InlineData("json", LoggingMode.Json)]
    [InlineData("Railway_Json", LoggingMode.RailwayJson)]
    public void ParsesLoggingModeCaseInsensitively(string raw, LoggingMode expected)
    {
        var config = ConfigTestEnvironment.Valid(("LOGGING_MODE", raw));

        Assert.Equal(expected, config.Logging.Mode);
    }

    [Fact]
    public void ListsTheAcceptedLoggingModes()
    {
        var result = CharterConfigParser.Parse(ConfigTestEnvironment.With(("LOGGING_MODE", "LOGFMT")));

        var problem = Assert.Single(result.Errors);
        Assert.Contains("DEFAULT, JSON, RAILWAY_JSON", problem.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("personal", CharterMode.Personal)]
    [InlineData("ORGANIZATION", CharterMode.Organization)]
    public void ParsesCharterMode(string raw, CharterMode expected)
    {
        Assert.Equal(expected, ConfigTestEnvironment.Valid(("CHARTER_MODE", raw)).Mode);
    }

    [Theory]
    [InlineData("agent", new[] { RunnerBackend.Agent })]
    [InlineData("docker,agent", new[] { RunnerBackend.Docker, RunnerBackend.Agent })]
    [InlineData(" github-actions , docker ", new[] { RunnerBackend.GitHubActions, RunnerBackend.Docker })]
    [InlineData("AGENT,agent", new[] { RunnerBackend.Agent })]
    [InlineData("agent,github-actions,docker", new[] { RunnerBackend.Agent, RunnerBackend.GitHubActions, RunnerBackend.Docker })]
    public void ParsesCommaSeparatedRunners(string raw, RunnerBackend[] expected)
    {
        var config = ConfigTestEnvironment.Valid(("CHARTER_RUNNER", raw));

        Assert.Equal(expected, config.Runners);
        Assert.True(config.SupportsRunner(expected[0]));
    }

    [Theory]
    [InlineData("kubernetes")]
    [InlineData("agent,nomad")]
    [InlineData("github actions")]
    public void RejectsAnUnknownRunner(string raw)
    {
        var result = CharterConfigParser.Parse(ConfigTestEnvironment.With(("CHARTER_RUNNER", raw)));

        var problem = Assert.Single(result.Errors);
        Assert.Equal("CHARTER_RUNNER", problem.Variable);
        Assert.Contains("agent, github-actions, docker", problem.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CHARTER_OAUTH_GITHUB_ID", "CHARTER_OAUTH_GITHUB_SECRET")]
    [InlineData("CHARTER_OAUTH_GOOGLE_ID", "CHARTER_OAUTH_GOOGLE_SECRET")]
    [InlineData("CHARTER_OAUTH_DISCORD_ID", "CHARTER_OAUTH_DISCORD_SECRET")]
    [InlineData("CHARTER_OAUTH_SLACK_ID", "CHARTER_OAUTH_SLACK_SECRET")]
    public void RejectsHalfAnOAuthPair(string idVariable, string secretVariable)
    {
        var missingSecret = CharterConfigParser.Parse(ConfigTestEnvironment.With((idVariable, "id-value")));
        var missingId = CharterConfigParser.Parse(ConfigTestEnvironment.With((secretVariable, "secret-value")));

        Assert.Equal(secretVariable, Assert.Single(missingSecret.Errors).Variable);
        Assert.Equal(idVariable, Assert.Single(missingId.Errors).Variable);
    }

    [Fact]
    public void EnablesAnOAuthProviderOnlyWhenBothHalvesAreSet()
    {
        var config = ConfigTestEnvironment.Valid(
            ("CHARTER_OAUTH_GOOGLE_ID", "google-id"),
            ("CHARTER_OAUTH_GOOGLE_SECRET", "google-secret"));

        var provider = Assert.Single(config.Auth.OAuthProviders);
        Assert.Equal("google", provider.Name);
        Assert.Equal("google-id", provider.ClientId);
        Assert.Equal("google-secret", provider.ClientSecret.Reveal());
        Assert.Null(config.Auth.Provider("github"));
        Assert.True(config.Auth.HasAnyProvider);
    }

    [Fact]
    public void RequiresTheWholeStorageBlockOnceTheEndpointIsSet()
    {
        var result = CharterConfigParser.Parse(
            ConfigTestEnvironment.With(("CHARTER_STORAGE_ENDPOINT", "https://minio.internal:9000")));

        Assert.False(result.IsValid);
        Assert.Equal(3, result.Errors.Count);
        foreach (var variable in new[]
                 {
                     "CHARTER_STORAGE_BUCKET", "CHARTER_STORAGE_ACCESS_KEY", "CHARTER_STORAGE_SECRET_KEY",
                 })
        {
            Assert.Contains(result.Errors, problem => problem.Variable == variable);
        }
    }

    [Fact]
    public void LeavesStorageDisabledButWarnsWhenOnlyPartOfTheBlockIsSet()
    {
        var result = CharterConfigParser.Parse(
            ConfigTestEnvironment.With(("CHARTER_STORAGE_BUCKET", "charter-artifacts")));

        Assert.True(result.IsValid);
        Assert.Null(result.Config!.Storage);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("CHARTER_STORAGE_ENDPOINT", warning.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsesACompleteStorageBlock()
    {
        var config = ConfigTestEnvironment.Valid(
            ("CHARTER_STORAGE_ENDPOINT", "https://minio.internal:9000"),
            ("CHARTER_STORAGE_BUCKET", "charter-artifacts"),
            ("CHARTER_STORAGE_ACCESS_KEY", "access"),
            ("CHARTER_STORAGE_SECRET_KEY", "secret"));

        Assert.NotNull(config.Storage);
        Assert.True(config.StorageEnabled);
        Assert.Equal("auto", config.Storage.Region);
        Assert.True(config.Storage.ForcePathStyle);
    }

    [Fact]
    public void AcceptsARawPemPrivateKey()
    {
        var config = ConfigTestEnvironment.Valid();

        Assert.False(config.GitHub.PrivateKeyWasBase64);
        Assert.StartsWith("-----BEGIN", config.GitHub.PrivateKeyPem.Reveal(), StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptsABase64EncodedPemPrivateKey()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(ConfigTestEnvironment.PrivateKeyPem));

        var config = ConfigTestEnvironment.Valid(("GITHUB_APP_PRIVATE_KEY", encoded));

        Assert.True(config.GitHub.PrivateKeyWasBase64);
        Assert.Equal(ConfigTestEnvironment.PrivateKeyPem, config.GitHub.PrivateKeyPem.Reveal());
    }

    [Fact]
    public void NormalisesEscapedNewlinesInAPemPrivateKey()
    {
        var escaped = ConfigTestEnvironment.PrivateKeyPem.Replace("\n", "\\n", StringComparison.Ordinal);

        var config = ConfigTestEnvironment.Valid(("GITHUB_APP_PRIVATE_KEY", escaped));

        Assert.Equal(ConfigTestEnvironment.PrivateKeyPem, config.GitHub.PrivateKeyPem.Reveal());
        Assert.DoesNotContain("\\n", config.GitHub.PrivateKeyPem.Reveal(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-a-key-at-all")]
    [InlineData("bm90LWEta2V5LWF0LWFsbA==")]
    [InlineData("-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----")]
    public void RejectsAPrivateKeyThatIsNeitherPemNorBase64Pem(string raw)
    {
        var result = CharterConfigParser.Parse(ConfigTestEnvironment.With(("GITHUB_APP_PRIVATE_KEY", raw)));

        var problem = Assert.Single(result.Errors);
        Assert.Equal("GITHUB_APP_PRIVATE_KEY", problem.Variable);
        Assert.Contains("PEM", problem.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("0")]
    [InlineData("-3")]
    public void RejectsANonNumericGitHubAppId(string raw)
    {
        var result = CharterConfigParser.Parse(ConfigTestEnvironment.With(("GITHUB_APP_ID", raw)));

        Assert.Equal("GITHUB_APP_ID", Assert.Single(result.Errors).Variable);
    }

    [Theory]
    [InlineData("claude-opus-5", "anthropic", "claude-opus-5")]
    [InlineData("anthropic/claude-opus-5", "anthropic", "claude-opus-5")]
    [InlineData("openrouter/deepseek/deepseek-r1", "openrouter", "deepseek/deepseek-r1")]
    [InlineData("Google/gemini-3-pro", "google", "gemini-3-pro")]
    public void QualifiesModelIdentifiers(string raw, string provider, string model)
    {
        var config = ConfigTestEnvironment.Valid(("CHARTER_MODEL_BUILD", raw));

        Assert.Equal(provider, config.Models.Build.Provider);
        Assert.Equal(model, config.Models.Build.Model);
        Assert.Equal($"{provider}/{model}", config.Models.Build.Qualified);
    }

    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("llama-3.1-405b")]
    [InlineData("nosuchprovider/some-model")]
    [InlineData("openrouter/")]
    public void RejectsAModelIdentifierItWouldOtherwiseHaveToGuessAt(string raw)
    {
        var result = CharterConfigParser.Parse(ConfigTestEnvironment.With(("CHARTER_MODEL_REFINE", raw)));

        var problem = Assert.Single(result.Errors);
        Assert.Equal("CHARTER_MODEL_REFINE", problem.Variable);
    }

    [Fact]
    public void TreatsNumericAndBooleanParseFailuresAsProblemsRatherThanExceptions()
    {
        // Nothing here may throw: a bad boolean is a validation problem like any other, reported
        // alongside the rest rather than aborting the parse at the first one.
        var result = CharterConfigParser.Parse(ConfigTestEnvironment.With(
            ("PORT", "eight-thousand"),
            ("CHARTER_DEFAULT_SESSION_BUDGET_USD", "free"),
            ("CHARTER_DEFAULT_MONTHLY_BUDGET_USD", "-1"),
            ("CHARTER_DEMO", "yes"),
            ("CHARTER_UPDATE_CHECK", "sometimes"),
            ("CHARTER_STORAGE_FORCE_PATH_STYLE", "1")));

        Assert.False(result.IsValid);
        Assert.Equal(6, result.Errors.Count);
        Assert.All(result.Errors, problem => Assert.Contains("must be", problem.Text, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("smtp://smtp.example.com", 587, false)]
    [InlineData("smtps://smtp.example.com", 465, true)]
    [InlineData("smtp://user:pass@smtp.example.com:2525", 2525, false)]
    public void ParsesTheSmtpUrl(string url, int port, bool implicitTls)
    {
        var config = ConfigTestEnvironment.Valid(("CHARTER_SMTP_URL", url));

        Assert.NotNull(config.Smtp);
        Assert.Equal("smtp.example.com", config.Smtp.Host);
        Assert.Equal(port, config.Smtp.Port);
        Assert.Equal(implicitTls, config.Smtp.ImplicitTls);
        Assert.True(config.SmtpEnabled);
    }

    [Theory]
    [InlineData("mailto:someone@example.com")]
    [InlineData("smtp.example.com:587")]
    [InlineData("http://smtp.example.com")]
    public void RejectsAnUnusableSmtpUrl(string url)
    {
        var result = CharterConfigParser.Parse(ConfigTestEnvironment.With(("CHARTER_SMTP_URL", url)));

        Assert.Equal("CHARTER_SMTP_URL", Assert.Single(result.Errors).Variable);
    }

    [Fact]
    public void WarnsWhenSamlIsConfiguredInPersonalMode()
    {
        var result = CharterConfigParser.Parse(
            ConfigTestEnvironment.With(("CHARTER_SAML_METADATA_URL", "https://idp.example.com/metadata")));

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, problem => problem.Variable == "CHARTER_SAML_METADATA_URL");
    }

    [Fact]
    public void ProjectsTheBootSubsetWithoutReparsing()
    {
        var config = ConfigTestEnvironment.Valid(
            ("PORT", "9000"),
            ("LOGGING_MODE", "JSON"),
            ("CHARTER_SEQ_URL", "http://seq:5341"));

        var startup = config.ToStartupOptions();

        Assert.Equal(9000, startup.Port);
        Assert.Equal(LoggingMode.Json, startup.LoggingMode);
        Assert.True(startup.SeqEnabled);
        Assert.Equal(config.Database.ConnectionString.Reveal(), startup.DatabaseConnectionString);
    }
}
