using Charter.Api.Credentials;
using Charter.Auth.Authorization;
using Charter.Data;
using Charter.Domain;
using Charter.Models;
using Charter.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Charter.Tests;

/// <summary>
/// Settings → Credentials, over the host's own graph and a real Postgres.
/// </summary>
/// <remarks>
/// Half of "the documented install cannot make a model call" was that no route in the application
/// could create a <c>credential_grant</c>: the section 20b.3 chain resolved against a table only a
/// hand-written <c>INSERT</c> could populate. These tests are about the routes that close that, and
/// about the two rules section 20b.2 attaches to them — the secret goes in and never comes back, and
/// only engineers and administrators may look.
/// </remarks>
public class CredentialApiTests
{
    private const string DatabaseUrlVariable = "CHARTER_TEST_DATABASE_URL";

    private static async Task<CredentialWorld?> StartAsync(CancellationToken cancellationToken)
    {
        var databaseUrl = Environment.GetEnvironmentVariable(DatabaseUrlVariable);
        if (string.IsNullOrWhiteSpace(databaseUrl))
        {
            Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the credential API tests.");
            return null;
        }

        var database = await ThrowawayDatabase.CreateAsync(databaseUrl, cancellationToken);

        return await CredentialWorld.StartAsync(
            database,
            new RecordingRefinementClient(),
            cancellationToken,
            ("ANTHROPIC_API_KEY", null),
            ("OPENROUTER_API_KEY", "sk-or-v1-not-a-real-key"));
    }

    [Fact]
    public async Task LinkingACredentialStoresCiphertextAndReturnsNoSecret()
    {
        var token = TestContext.Current.CancellationToken;

        await using var charter = await StartAsync(token);
        if (charter is null)
        {
            return;
        }

        const string Secret = "sk-ant-api03-THIS-IS-THE-SECRET";

        var created = await charter.WithServiceAsync(service => service.CreateAsync(
            MemberSnapshot.From(charter.Member),
            new CreateCredentialBody
            {
                Kind = ApiCredentialKind.AnthropicApiKey,
                Secret = Secret,
            },
            token));

        Assert.True(created.Outcome.Succeeded, created.Outcome.Reason);
        Assert.NotNull(created.Created);
        Assert.Null(created.Created!.Warning);

        // Section 20b.2: no reveal, ever. The response type has no property that could carry one, so
        // this is a check on the serialised shape rather than on a field name.
        var rendered = System.Text.Json.JsonSerializer.Serialize(created.Created);
        Assert.DoesNotContain("THIS-IS-THE-SECRET", rendered, StringComparison.Ordinal);

        // And the column holds ciphertext that the dedicated key, and only it, can read back.
        var stored = await charter.ReadAsync(async db => await db.CredentialGrants.SingleAsync(token));

        Assert.NotEmpty(stored.SecretEncrypted);
        Assert.DoesNotContain(
            Secret,
            System.Text.Encoding.UTF8.GetString(stored.SecretEncrypted),
            StringComparison.Ordinal);

        var protector = charter.Resolve<ICredentialProtector>();
        Assert.Equal(Secret, protector.Unprotect(stored.SecretEncrypted));

        // Section 7.3, guardrail 5: attributable to a named human.
        var audit = await charter.ReadAsync(async db => await db.AuditLogs
            .AsNoTracking()
            .Where(row => row.Action == CredentialAuditActions.Linked)
            .ToListAsync(token));

        Assert.Single(audit);
    }

    [Fact]
    public async Task ALinkedGrantIsWhatTheChainThenResolves()
    {
        var token = TestContext.Current.CancellationToken;

        await using var charter = await StartAsync(token);
        if (charter is null)
        {
            return;
        }

        // Created through the API, resolved through the real chain: the two halves of the defect,
        // joined. Before this route existed there was no way to reach tiers 1 to 4 at all.
        var created = await charter.WithServiceAsync(service => service.CreateAsync(
            MemberSnapshot.From(charter.Member),
            new CreateCredentialBody
            {
                Kind = ApiCredentialKind.OpenRouterKey,
                Secret = "sk-or-v1-linked-by-the-api",
            },
            token));

        Assert.True(created.Outcome.Succeeded, created.Outcome.Reason);

        var resolution = await charter.ResolveCredentialAsync("openrouter/anthropic/claude-sonnet-5", token);

        Assert.True(resolution.Resolved);
        Assert.Equal(created.Created!.Credential.Id, resolution.Credential!.Credential.Id);

        // Revocation is immediate: the next resolution falls through to the environment key.
        var revoked = await charter.WithServiceAsync(service => service.RevokeAsync(
            MemberSnapshot.From(charter.Member),
            Guid.Parse(created.Created.Credential.Id),
            token));

        Assert.True(revoked.Succeeded, revoked.Reason);

        var afterRevocation = await charter.ResolveCredentialAsync("openrouter/anthropic/claude-sonnet-5", token);

        Assert.StartsWith(
            InstanceModelCredentials.IdPrefix,
            afterRevocation.Credential!.Credential.Id,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheListShowsTheEnvironmentKeyAndWhetherTheConfiguredModelsCanBeServed()
    {
        var token = TestContext.Current.CancellationToken;

        await using var charter = await StartAsync(token);
        if (charter is null)
        {
            return;
        }

        var listed = await charter.WithServiceAsync(service => service.ListAsync(
            MemberSnapshot.From(charter.Member),
            token));

        Assert.True(listed.Outcome.Succeeded);

        var environment = Assert.Single(
            listed.List!.Credentials,
            credential => credential.Source == ApiCredentialSource.Environment);

        Assert.Equal("OPENROUTER_API_KEY", environment.Variable);
        Assert.Equal(ApiCredentialStatus.Active, environment.Status);

        // The screen answers the question an operator actually has, which is not "is a key set" but
        // "will a request work".
        Assert.All(listed.List.Models, model => Assert.True(model.Servable, model.Remedy));
        Assert.False(listed.List.SharedPoolAllowed);
    }

    [Fact]
    public async Task ARequesterMayNotSeeOrLinkCredentials()
    {
        var token = TestContext.Current.CancellationToken;

        await using var charter = await StartAsync(token);
        if (charter is null)
        {
            return;
        }

        // Section 7.4: enforced on the server, and the same refusal for reading as for writing — a
        // credentials list is a map of what an organisation pays with.
        var requester = MemberSnapshot.From(charter.Member) with { Roles = [MemberRole.Requester] };

        var listed = await charter.WithServiceAsync(service => service.ListAsync(requester, token));
        Assert.False(listed.Outcome.Succeeded);
        Assert.Null(listed.List);

        var created = await charter.WithServiceAsync(service => service.CreateAsync(
            requester,
            new CreateCredentialBody { Kind = ApiCredentialKind.OpenRouterKey, Secret = "sk-or-v1-nope" },
            token));

        Assert.False(created.Outcome.Succeeded);
        Assert.Equal(0, await charter.CountAsync(db => db.CredentialGrants.CountAsync(token)));
    }

    [Fact]
    public async Task OptingIntoASharedPoolCarriesTheTermsOfServiceCaution()
    {
        var token = TestContext.Current.CancellationToken;

        await using var charter = await StartAsync(token);
        if (charter is null)
        {
            return;
        }

        // Section 20b.7: Charter does not make this call on the operator's behalf, and it does not
        // make it silently either.
        var created = await charter.WithServiceAsync(service => service.CreateAsync(
            MemberSnapshot.From(charter.Member),
            new CreateCredentialBody
            {
                Kind = ApiCredentialKind.AnthropicOauth,
                Secret = "oauth-access-token",
                Scope = ApiCredentialScope.SharedPool,
            },
            token));

        Assert.True(created.Outcome.Succeeded, created.Outcome.Reason);
        Assert.NotNull(created.Created!.Warning);
        Assert.Contains("terms", created.Created.Warning!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ACustomEndpointCredentialIsRefusedWithoutABaseUrl()
    {
        var token = TestContext.Current.CancellationToken;

        await using var charter = await StartAsync(token);
        if (charter is null)
        {
            return;
        }

        // There is no public endpoint to fall back to, so accepting this would store a credential
        // that resolves and then throws at the first call.
        var created = await charter.WithServiceAsync(service => service.CreateAsync(
            MemberSnapshot.From(charter.Member),
            new CreateCredentialBody
            {
                Kind = ApiCredentialKind.CustomOpenAiCompatible,
                Secret = "gateway-token",
            },
            token));

        Assert.False(created.Outcome.Succeeded);
        Assert.Contains("base URL", created.Outcome.Reason, StringComparison.Ordinal);
    }
}
