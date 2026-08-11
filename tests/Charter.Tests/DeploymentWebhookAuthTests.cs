using System.Net;
using System.Text;
using System.Text.Json;
using Charter.Configuration;
using Charter.Data;
using Charter.Deployments;
using Charter.Domain;
using Charter.GitHub;
using Microsoft.EntityFrameworkCore;

namespace Charter.Tests;

/// <summary>
/// Who is allowed to write a deployment report.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint's only admission rule used to be the head commit SHA in its path, and the reasoning
/// for that — "an unguessable 40-character key that already exists" — has the shape of the defect
/// section 16.3 is about. The execution plane authors that SHA, so it holds the value before the
/// control plane does, and everybody who can see the pull request holds it afterwards: forks, CI logs,
/// notification emails, the pull request page itself. What the endpoint accepts is a URL Charter
/// fetches on a loop from inside its own network and shows a non-engineer as a safe link.
/// </para>
/// <para>
/// Every test here fails without the admission check: the endpoint answered 202 or 404 on the merits
/// of the SHA alone, whoever asked.
/// </para>
/// </remarks>
public class DeploymentWebhookAuthTests
{
    private const string DatabaseUrlVariable = "CHARTER_TEST_DATABASE_URL";

    private static readonly Secret Configured = Secret.From("f9e3c1a7b2d84e60a5c9137fbe024d81")!;

    [Fact]
    public void TheRightSecretIsAdmitted()
    {
        Assert.Equal(
            DeploymentWebhookAdmission.Allowed,
            DeploymentWebhookAuthentication.Check(Configured, Configured.Reveal()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("f9e3c1a7b2d84e60a5c9137fbe024d80")]
    [InlineData("f9e3c1a7b2d84e60a5c9137fbe024d8")]
    [InlineData("f9e3c1a7b2d84e60a5c9137fbe024d811")]
    public void AnythingElseIsRefused(string? presented)
    {
        Assert.Equal(
            DeploymentWebhookAdmission.Refused,
            DeploymentWebhookAuthentication.Check(Configured, presented));
    }

    [Fact]
    public void AnInstanceWithNoSecretAdmitsNobodyRatherThanFallingBackToTheSha()
    {
        // Fail closed. A default that is safe only until somebody notices is not a default, and the
        // SHA is the thing that was never a credential.
        Assert.Equal(
            DeploymentWebhookAdmission.NotConfigured,
            DeploymentWebhookAuthentication.Check(null, "anything at all"));
    }

    [Theory]
    [InlineData("Bearer the-secret", null, null)]
    [InlineData("bearer the-secret", null, null)]
    [InlineData(null, "the-secret", null)]
    [InlineData(null, null, "the-secret")]
    public void EachCarrierIsRead(string? authorization, string? header, string? query)
    {
        // Three ways in because platforms differ in what they will send: a header where one can be
        // set, and the query parameter for a post-deploy hook that is a URL field and nothing else.
        Assert.Equal("the-secret", DeploymentWebhookAuthentication.Presented(authorization, header, query));
    }

    [Fact]
    public void ACallerDoesNotGetThreeAttemptsAtTheSecret()
    {
        // First carrier present wins outright. Otherwise one request could try three values, and the
        // constant-time comparison below would be protecting nothing worth protecting.
        Assert.Equal("first", DeploymentWebhookAuthentication.Presented("Bearer first", "second", "third"));
        Assert.Equal("second", DeploymentWebhookAuthentication.Presented(null, "second", "third"));
    }

    [Fact]
    public void AnAuthorizationHeaderThatIsNotBearerIsIgnoredRatherThanMisread()
    {
        Assert.Null(DeploymentWebhookAuthentication.Presented("Basic aGk6dGhlcmU=", null, null));
    }

    [Fact]
    public void ASecretShorterThanTheMinimumStopsStartup()
    {
        var options = DeploymentOptions.Parse(name =>
            name == "CHARTER_DEPLOYMENT_WEBHOOK_SECRET" ? "too-short" : null);

        var problem = Assert.Single(options.Errors);

        Assert.Equal("CHARTER_DEPLOYMENT_WEBHOOK_SECRET", problem.Variable);
        Assert.Contains("openssl rand", problem.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ASecretOfUsableLengthParsesAndIsNotPrintable()
    {
        var options = DeploymentOptions.Parse(name =>
            name == "CHARTER_DEPLOYMENT_WEBHOOK_SECRET" ? Configured.Reveal() : null);

        Assert.Empty(options.Errors);
        Assert.NotNull(options.WebhookSecret);
        Assert.Equal(Configured.Reveal(), options.WebhookSecret.Reveal());
        Assert.DoesNotContain(Configured.Reveal(), options.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnauthenticatedPostIsRefusedByTheRunningInstance()
    {
        var databaseUrl = Environment.GetEnvironmentVariable(DatabaseUrlVariable);
        if (string.IsNullOrWhiteSpace(databaseUrl))
        {
            Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the deployment webhook tests.");
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;

        await using var database = await ThrowawayDatabase.CreateAsync(databaseUrl, cancellationToken);

        var values = ConfigTestEnvironment.Required();
        values["DATABASE_URL"] = database.Url.ToString();
        values["CHARTER_DEPLOYMENT_WEBHOOK_SECRET"] = Configured.Reveal();

        Func<string, string?> read = name => values.GetValueOrDefault(name);

        var parsed = CharterConfigParser.Parse(read);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Errors.Select(problem => problem.Text)));

        await using var charter = await BootedCharter.StartAsync(parsed.Config!, read, cancellationToken);

        // After boot, because boot is what applies the migrations the users table comes from.
        await ClaimAsync(database, cancellationToken);

        var path = $"/api/deployments/{Sha}";
        var body = """{"url":"https://myapp-pr-142.onrender.com","state":"ready","provider":"render"}""";

        // No secret at all: the SHA in the path is exactly what an attacker reads off a public pull
        // request, and on its own it now buys nothing.
        var anonymous = await charter.Client.PostAsync(path, Json(body), cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        // A wrong secret is refused with the same answer, so the response cannot be used to tell
        // "missing" from "wrong".
        var wrong = await Post(charter, path, body, "Bearer " + new string('0', 32), cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(
            await anonymous.Content.ReadAsStringAsync(cancellationToken),
            await wrong.Content.ReadAsStringAsync(cancellationToken));

        // The right secret gets through admission and is then judged on its merits: no change request
        // in this instance carries that commit, so the binder answers 404 (section 18).
        var admitted = await Post(charter, path, body, "Bearer " + Configured.Reveal(), cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, admitted.StatusCode);

        // The same secret through the query parameter, for a platform whose post-deploy hook is a URL
        // field and nothing else.
        var byQuery = await charter.Client.PostAsync(
            $"{path}?token={Configured.Reveal()}",
            Json(body),
            cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, byQuery.StatusCode);

        // And nothing was written by any of the refused calls.
        await using var db = Connect(database);
        Assert.Empty(await db.Deployments.ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task AnInstanceWithNoSecretConfiguredRefusesAndSaysWhichVariableToSet()
    {
        var databaseUrl = Environment.GetEnvironmentVariable(DatabaseUrlVariable);
        if (string.IsNullOrWhiteSpace(databaseUrl))
        {
            Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the deployment webhook tests.");
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;

        await using var database = await ThrowawayDatabase.CreateAsync(databaseUrl, cancellationToken);

        var values = ConfigTestEnvironment.Required();
        values["DATABASE_URL"] = database.Url.ToString();

        Func<string, string?> read = name => values.GetValueOrDefault(name);

        var parsed = CharterConfigParser.Parse(read);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Errors.Select(problem => problem.Text)));

        await using var charter = await BootedCharter.StartAsync(parsed.Config!, read, cancellationToken);

        // After boot, because boot is what applies the migrations the users table comes from.
        await ClaimAsync(database, cancellationToken);

        var response = await charter.Client.PostAsync(
            $"/api/deployments/{Sha}",
            Json("""{"url":"https://myapp-pr-142.onrender.com","state":"ready","provider":"render"}"""),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

        Assert.Contains(
            "CHARTER_DEPLOYMENT_WEBHOOK_SECRET",
            document.RootElement.GetProperty("error").GetString() ?? string.Empty,
            StringComparison.Ordinal);
    }

    private const string Sha = "9f2c41b7d8e05a3c6b12f4a7e8d0c5b3a16d47e2";

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<HttpResponseMessage> Post(
        BootedCharter charter,
        string path,
        string body,
        string authorization,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = Json(body) };
        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        return await charter.Client.SendAsync(request, cancellationToken);
    }

    private static CharterDbContext Connect(ThrowawayDatabase database)
    {
        var options = new DbContextOptionsBuilder<CharterDbContext>();
        DataServiceCollectionExtensions.ConfigureNpgsql(options, database.ConnectionString);

        return new CharterDbContext(options.Options);
    }

    private static async Task ClaimAsync(ThrowawayDatabase database, CancellationToken cancellationToken)
    {
        // Section 30.1 gates every /api/* route with 503 until the instance has a user, which would
        // hide the difference between refused and admitted.
        await using var db = Connect(database);

        db.Users.Add(User.Create(
            "first@example.com",
            "First Admin",
            TeachingLevel.SkipTheBasics,
            DateTimeOffset.UtcNow));

        await db.SaveChangesAsync(cancellationToken);
    }
}
