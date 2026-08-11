using System.Runtime.CompilerServices;
using System.Text.Json;
using Charter.Api.Contracts;
using Charter.Api.Requests;
using Charter.Auth.Authorization;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Charter.Hosting;
using Charter.Models;
using Charter.Refinement;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelIdentifier = Charter.Models.ModelIdentifier;

namespace Charter.Tests;

/// <summary>
/// The defect, as the test that would have caught it: a real instance, a real Postgres, a real queue,
/// and nothing configured but an environment key.
/// </summary>
/// <remarks>
/// <para>
/// Every existing suite passed while the documented default install could not make a single model
/// call, and the reason is visible in <c>ApiPhaseOneLoopTests</c>: it stubs
/// <see cref="ICredentialResolver"/>, so the one thing that was broken was the one thing it could not
/// see. These tests stub exactly one collaborator — the model client, so no tokens are spent — and
/// resolve credentials through the host's own graph: <c>AddCharterCredentials</c>, the EF store, the
/// instance-key decorator and <see cref="CredentialResolver"/>, in the composition
/// <c>Program.cs</c> builds.
/// </para>
/// <para>
/// The queue is the application's own hosted <c>QueueDispatcher</c>. Nothing here calls a handler; a
/// request is filed and the instance is watched until it does something, which is the only way to
/// tell a loop that works from a loop that defers forever.
/// </para>
/// </remarks>
public class CredentialEndToEndTests
{
    private const string DatabaseUrlVariable = "CHARTER_TEST_DATABASE_URL";

    /// <summary>The canned refinement turn: one spec, inside the repository's allowed scope.</summary>
    private static string SpecTurn => JsonSerializer.Serialize(new
    {
        resolution = "spec",
        message = "Here's what I've understood.",
        spec = new
        {
            title = "Remember the last selected vertical",
            outcome = "When you start a new quote, the vertical you chose last time is already selected.",
            acceptance_criteria = new[]
            {
                "Starting a new quote pre-selects the vertical you chose on your previous quote.",
                "A person who has never created a quote still starts on Solar.",
            },
            technical_approach = "Add a per-user preference row and read it on quote creation.",
            scope = new
            {
                files = new[] { "src/Features/Quotes/QuoteWizard.cs" },
                paths = new[] { "src/Features/Quotes/**" },
            },
            risks = new[] { "Touching the wizard's session state could affect in-flight quotes." },
            open_questions = Array.Empty<string>(),
        },
    });

    /// <summary>
    /// The exact configuration <c>.env.example</c> and <c>docs/getting-started.md</c> produce: one
    /// key in the environment, no credential grant anywhere, and the section 4.2 default models.
    /// </summary>
    [Fact]
    public async Task AnInstanceWithNothingButAnEnvironmentKeyRefinesARequest()
    {
        var databaseUrl = Environment.GetEnvironmentVariable(DatabaseUrlVariable);
        if (string.IsNullOrWhiteSpace(databaseUrl))
        {
            Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the credential boot tests.");
            return;
        }

        var token = TestContext.Current.CancellationToken;

        await using var database = await ThrowawayDatabase.CreateAsync(databaseUrl, token);

        var model = new RecordingRefinementClient().Enqueue(SpecTurn);

        // OPENROUTER_API_KEY alone, CHARTER_MODEL_REFINE left at its openrouter/-qualified default.
        // No credential_grants row exists in this database and no code below creates one.
        await using var charter = await CredentialWorld.StartAsync(
            database,
            model,
            token,
            ("ANTHROPIC_API_KEY", null),
            ("OPENROUTER_API_KEY", "sk-or-v1-not-a-real-key"));

        Assert.Equal(0, await charter.CountAsync(db => db.CredentialGrants.CountAsync(token)));

        var requestId = await charter.FileRequestAsync(token);

        var status = await charter.AwaitStatusAsync(
            requestId,
            reached => reached is RequestStatus.SpecReady or RequestStatus.Failed or RequestStatus.Rejected,
            token);

        // The whole point. Before the instance keys were a tier in the section 20b.3 chain this sat
        // in Refining until the test timed out, with nothing written anywhere saying why.
        Assert.Equal(RequestStatus.SpecReady, status);
        Assert.Equal(1, await charter.CountAsync(db => db.Specs.CountAsync(token)));

        // And it ran on the environment key, at OpenRouter's tier, rather than on anything else.
        var used = Assert.Single(model.Credentials);
        Assert.Equal(InstanceModelCredentials.IdPrefix + "OPENROUTER_API_KEY", used.Id);
        Assert.Equal(ModelCredentialKind.OpenRouterKey, used.Kind);
    }

    /// <summary>
    /// The other half: a session that cannot resolve a credential ends, loudly, in a state the
    /// requester can read — rather than deferring forever.
    /// </summary>
    /// <remarks>
    /// The instance holds an <c>ANTHROPIC_API_KEY</c>, which is why it boots at all, and is pointed at
    /// a Google model no key it holds can serve. That is the same shape as the case an operator
    /// reaches by revoking a key: a credential exists, and none of it can serve this call.
    /// </remarks>
    [Fact]
    public async Task ARequestThatCannotResolveACredentialFailsLoudlyInsteadOfStalling()
    {
        var databaseUrl = Environment.GetEnvironmentVariable(DatabaseUrlVariable);
        if (string.IsNullOrWhiteSpace(databaseUrl))
        {
            Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the credential boot tests.");
            return;
        }

        var token = TestContext.Current.CancellationToken;

        await using var database = await ThrowawayDatabase.CreateAsync(databaseUrl, token);

        var model = new RecordingRefinementClient().Enqueue(SpecTurn);

        await using var charter = await CredentialWorld.StartAsync(
            database,
            model,
            token,
            ("ANTHROPIC_API_KEY", "sk-ant-not-a-real-key"),
            ("OPENROUTER_API_KEY", null),
            ("CHARTER_MODEL_REFINE", "google/gemini-2.5-pro"));

        var requestId = await charter.FileRequestAsync(token);

        var status = await charter.AwaitStatusAsync(
            requestId,
            reached => reached is RequestStatus.Failed or RequestStatus.SpecReady,
            token);

        // Section 6: terminal, and not the requester's fault. Deferring instead is the silent stall.
        Assert.Equal(RequestStatus.Failed, status);

        // No model was called at all, so nothing was spent discovering this.
        Assert.Empty(model.Credentials);

        // The requester's thread says something rather than nothing, in plain language, with no
        // environment variable in it — that half goes to the operator.
        var thread = await charter.ReadAsync(async db => await db.ConversationTurns
            .AsNoTracking()
            .Where(turn => turn.Kind == ConversationTurnKind.Refusal)
            .ToListAsync(token));

        var refusal = Assert.Single(thread);
        Assert.Contains("could not reach a model", refusal.AuthoredText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("API_KEY", refusal.AuthoredText, StringComparison.Ordinal);

        // And the operator's half is on the job row, naming the variable to set.
        var errors = await charter.ReadAsync(async db => await db.Jobs
            .AsNoTracking()
            .Where(job => job.Type == JobType.Refine && job.LastError != null)
            .Select(job => job.LastError!)
            .ToListAsync(token));

        Assert.Contains(errors, error => error.Contains("OPENROUTER_API_KEY", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("google/gemini-2.5-pro", StringComparison.Ordinal));
    }
}

/// <summary>
/// A booted Charter over a throwaway database, with one collaborator replaced.
/// </summary>
/// <remarks>
/// The boot sequence is <c>Program.cs</c>'s, for the reason <c>BootEndToEndTests</c> gives: a suite
/// that assembles its own service collection is testing a graph nobody deploys, and the defect this
/// world exists for lived precisely in the difference.
/// </remarks>
internal sealed class CredentialWorld : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly ThrowawayDatabase database;

    private CredentialWorld(WebApplication app, ThrowawayDatabase database, Guid orgId, Guid repoId, Member member)
    {
        this.app = app;
        this.database = database;
        OrgId = orgId;
        RepoId = repoId;
        Member = member;
    }

    public Guid OrgId { get; }

    public Guid RepoId { get; }

    public Member Member { get; }

    public static async Task<CredentialWorld> StartAsync(
        ThrowawayDatabase database,
        IModelClient model,
        CancellationToken cancellationToken,
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

        var config = parsed.Config!;
        var builder = CharterHost.CreateBuilder([], config, config.ToStartupOptions(), read);

        builder.WebHost.UseUrls("http://127.0.0.1:0");

        // The one substitution. Everything about credentials — the store, the decorator, the resolver,
        // the encryption — is the host's own.
        builder.Services.RemoveAll<IModelClientFactory>();
        builder.Services.AddSingleton<IModelClientFactory>(new RefinementStubClientFactory(model));

        var app = builder.Build();

        await CharterHost.MigrateAsync(app, cancellationToken);
        CharterHost.ConfigurePipeline(app);
        await app.StartAsync(cancellationToken);

        var (orgId, repoId, member) = await SeedAsync(app, cancellationToken);

        return new CredentialWorld(app, database, orgId, repoId, member);
    }

    /// <summary>Files a request through the real intake path, which queues the refine job.</summary>
    public async Task<Guid> FileRequestAsync(CancellationToken cancellationToken)
    {
        await using var scope = app.Services.CreateAsyncScope();

        var commands = scope.ServiceProvider.GetRequiredService<RequestCommandService>();

        var (outcome, requestId) = await commands.CreateAsync(
            MemberSnapshot.From(Member),
            new CreateRequestBody
            {
                ProjectId = RepoId.ToString(),
                RawText = "every time i start a new quote it makes me pick solar again",
            },
            cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Reason);

        return requestId;
    }

    /// <summary>
    /// Watches the instance until the request leaves <c>Refining</c>, or gives up.
    /// </summary>
    /// <remarks>
    /// Polling rather than driving the dispatcher by hand, deliberately. The question is whether a
    /// running Charter gets a filed request refined without anybody helping it, and a test that calls
    /// the handler itself cannot answer that — it is the question the defect answered "no" to for
    /// every instance ever deployed.
    /// </remarks>
    public async Task<RequestStatus> AwaitStatusAsync(
        Guid requestId,
        Func<RequestStatus, bool> settled,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(90);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await ReadAsync(async db => await db.Requests
                .AsNoTracking()
                .Where(row => row.Id == requestId)
                .Select(row => (RequestStatus?)row.Status)
                .FirstOrDefaultAsync(cancellationToken));

            if (status is { } reached && settled(reached))
            {
                return reached;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        // Reported as the state it was stuck in, because "still Refining after ninety seconds" is
        // exactly the symptom this suite exists for and the assertion reads better than a timeout.
        return await ReadAsync(async db => await db.Requests
            .AsNoTracking()
            .Where(row => row.Id == requestId)
            .Select(row => row.Status)
            .FirstAsync(cancellationToken));
    }

    public async Task<T> ReadAsync<T>(Func<CharterDbContext, Task<T>> read)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<CharterDbContext>());
    }

    /// <summary>Runs one call against the credentials service the endpoints resolve.</summary>
    public async Task<T> WithServiceAsync<T>(Func<Charter.Api.Credentials.CredentialsService, Task<T>> call)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await call(scope.ServiceProvider.GetRequiredService<Charter.Api.Credentials.CredentialsService>());
    }

    /// <summary>A singleton from the running instance's container.</summary>
    public T Resolve<T>()
        where T : notnull
        => app.Services.GetRequiredService<T>();

    /// <summary>Walks the section 20b.3 chain exactly as a refine job would.</summary>
    public async Task<ModelCredentialResolution> ResolveCredentialAsync(
        string model,
        CancellationToken cancellationToken)
    {
        await using var scope = app.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<ICredentialResolver>().ResolveAsync(
            new ModelCredentialQuery(ModelIdentifier.Parse(model), Member.UserId.ToString(), OrgId.ToString()),
            cancellationToken);
    }

    public Task<int> CountAsync(Func<CharterDbContext, Task<int>> count) => ReadAsync(count);

    public async ValueTask DisposeAsync()
    {
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        try
        {
            await app.StopAsync(shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            // The dispatcher may still be draining; the database is about to be dropped either way.
        }
        finally
        {
            await app.DisposeAsync();
        }

        GC.KeepAlive(database);
    }

    private static async Task<(Guid OrgId, Guid RepoId, Member Member)> SeedAsync(
        WebApplication app,
        CancellationToken cancellationToken)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharterDbContext>();

        var now = DateTimeOffset.UtcNow.AddMinutes(-30);
        var tag = Guid.CreateVersion7().ToString("N");

        var organization = Organization.Create("Northbeam Solar", OrganizationMode.Organization, now);
        var user = User.Create($"ayesha+{tag}@example.test", "Ayesha Rahman", TeachingLevel.SkipTheBasics, now);

        // Engineer as well as requester, so the same member can drive the credentials routes.
        var member = Member.Create(
            organization.Id,
            user.Id,
            [MemberRole.Requester, MemberRole.Approver, MemberRole.Engineer, MemberRole.Admin],
            now: now);

        var repo = Repo.Connect(organization.Id, 42, "northbeam/quote-tool", "main", now);
        repo.TransitionTo(RepoStatus.Ready, now);
        repo.RecordConfigSnapshot(
            """
            {
              "project": { "name": "Quote tool", "description": "The internal quoting wizard." },
              "limits": { "max_session_usd": 3.4 },
              "scopes": {
                "allow": ["src/Features/**", "src/Web/Components/**"],
                "deny": ["src/Auth/**", "**/Migrations/**", ".github/**", "infra/**"]
              },
              "glossary": { "vertical": "The kind of installation a quote is for." }
            }
            """,
            now);

        db.Organizations.Add(organization);
        db.Users.Add(user);
        db.Members.Add(member);
        db.Repos.Add(repo);
        db.RepoScopes.Add(RepoScope.ForRole(repo.Id, MemberRole.Requester, canRequest: true, now));

        await db.SaveChangesAsync(cancellationToken);

        return (organization.Id, repo.Id, member);
    }
}

/// <summary>
/// A refinement stub that also records which credential it was handed.
/// </summary>
/// <remarks>
/// The recording is the assertion that matters: "the request refined" would pass on an instance that
/// resolved a hand-inserted grant, and the claim under test is specifically that the environment key
/// is what served it.
/// </remarks>
internal sealed class RecordingRefinementClient : IModelClient
{
    private readonly Queue<string> responses = new();

    public List<ModelCredential> Credentials { get; } = [];

    public ModelProvider Provider => ModelProvider.OpenRouter;

    public IReadOnlyCollection<ModelProvider> SupportedProviders => Enum.GetValues<ModelProvider>();

    public bool Supports(ModelIdentifier model) => true;

    public RecordingRefinementClient Enqueue(string json)
    {
        responses.Enqueue(json);
        return this;
    }

    public Task<ModelCompletion> CompleteAsync(
        ModelRequest request,
        ModelCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Credentials.Add(credential);

        if (responses.Count == 0)
        {
            throw new InvalidOperationException("The refinement stub ran out of canned responses.");
        }

        var json = responses.Dequeue();

        return Task.FromResult(new ModelCompletion
        {
            Model = request.Model,
            Text = json,
            StructuredJson = json,
            StopReason = ModelStopReason.EndTurn,
            Usage = new ModelUsage { InputTokens = 120, OutputTokens = 40 },
            Charge = ModelCharge.None,
        });
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        ModelCredential credential,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var completion = await CompleteAsync(request, credential, cancellationToken);
        yield return new ModelStreamEvent.Completed(completion);
    }
}
