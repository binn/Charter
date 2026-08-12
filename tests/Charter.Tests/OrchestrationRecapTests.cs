using Charter.Data;
using Charter.Domain;
using Charter.Models;
using Charter.Orchestration;
using Charter.Recaps;
using Charter.VersionControl;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>
/// Section 14's engineer recap, from the trigger that had never fired to the ledger line it settles.
/// </summary>
/// <remarks>
/// <c>Charter.Recaps</c> was built and tested in isolation and had no caller, so no session ever got
/// a recap. The trigger is the reconciliation pass — a change request opening is what says the run is
/// over — and the work itself is a queued job, because a model pass belongs somewhere it can be
/// retried, deferred for capacity, and survive the container it started in (section 2.3).
/// </remarks>
public class OrchestrationRecapTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AFinishedSessionIsRecappedPublishedAndSettledExactlyOnce()
    {
        await using var database = await OrchestrationDatabase.CreateAsync(Token);
        if (database is null)
        {
            return;
        }

        await using var instance = ControlPlaneInstance.Create(database);

        var seed = await instance.SeedApprovedSpecAsync(Token);
        var sessionId = await SeedFinishedSessionAsync(instance, seed);

        var generator = new StubRecapGenerator();
        var publisher = new StubRecapPublisher();

        var first = await HandleAsync(instance, generator, publisher, sessionId);

        Assert.Equal(JobHandling.Completed, first.Handling);
        Assert.Equal(1, generator.Calls);

        var recap = await instance.InScopeAsync(async provider => await provider
            .GetRequiredService<CharterDbContext>()
            .Recaps
            .AsNoTracking()
            .SingleAsync(row => row.SessionId == sessionId, Token));

        // Section 14: the body stored is the body published, fallback notice and all. A recap that
        // reads differently in Charter from the way it reads on the change request is a recap two
        // people quote at each other.
        Assert.Equal(publisher.LastBody, recap.BodyMd);
        Assert.Equal(StubRecapGenerator.CostUsd, recap.CostUsd);

        // It was posted where engineers actually review, against the change request the session opened.
        Assert.Equal(StubRecapGenerator.ChangeRequestNumber, publisher.LastNumber);

        var ledger = await instance.InScopeAsync(async provider => await provider
            .GetRequiredService<CharterDbContext>()
            .LedgerEntries
            .AsNoTracking()
            .SingleAsync(row => row.SessionId == sessionId, Token));

        Assert.Equal(LedgerCategory.Recap, ledger.Category);
        Assert.Equal(LedgerState.Settled, ledger.State);
        Assert.Equal(StubRecapGenerator.CostUsd, ledger.Usd);
        Assert.Equal(seed.OrganizationId, ledger.OrgId);

        // A second pass over the same session must not spend a second model call on it.
        var second = await HandleAsync(instance, generator, publisher, sessionId);

        Assert.Equal(JobHandling.Completed, second.Handling);
        Assert.Equal(1, generator.Calls);
    }

    [Fact]
    public async Task OpeningTheChangeRequestIsWhatQueuesTheRecapAndItQueuesOne()
    {
        await using var database = await OrchestrationDatabase.CreateAsync(Token);
        if (database is null)
        {
            return;
        }

        await using var instance = ControlPlaneInstance.Create(database);

        var seed = await instance.SeedApprovedSpecAsync(Token);
        var sessionId = await SeedFinishedSessionAsync(instance, seed);

        // Two passes of the sweep. It runs every fifteen seconds for as long as the session is live,
        // so queueing one recap per pass would be a queue that fills up on its own.
        await instance.Orchestrator.ReconcileAsync(startup: true, Token);
        await instance.Orchestrator.ReconcileAsync(startup: false, Token);

        var jobs = await instance.JobsAsync(Token);
        var recap = Assert.Single(jobs, job => job.Type == JobType.Recap);

        Assert.Equal(sessionId, RecapJobPayload.TryParse(recap.Payload)!.SessionId);
    }

    [Fact]
    public void ARecapPayloadIsReadInEitherSpelling()
    {
        var session = Guid.CreateVersion7();

        Assert.Equal(
            session,
            RecapJobPayload.TryParse($$"""{"session_id":"{{session:D}}"}""")!.SessionId);

        // What the "Works" button writes: a request and a spec, because the API does not know which
        // session was the one that worked.
        var fromFeedback = RecapJobPayload.TryParse(
            $$"""{"requestId":"{{session:D}}","verdict":"works"}""");

        Assert.NotNull(fromFeedback);
        Assert.Null(fromFeedback.SessionId);
        Assert.Equal(session, fromFeedback.RequestId);

        Assert.Null(RecapJobPayload.TryParse("""{"nothing":"useful"}"""));
    }

    /// <summary>A session that ran, ended cleanly, and had a change request opened for it.</summary>
    private static async Task<Guid> SeedFinishedSessionAsync(ControlPlaneInstance instance, SpendGateSeed seed)
        => await instance.InScopeAsync(async provider =>
        {
            var db = provider.GetRequiredService<CharterDbContext>();

            var session = Session.Queue(seed.SpecId, RunnerKind.GitHubActions, "anthropic/claude-opus-5");
            session.Start("f00dcafe1234567890abcdef1234567890abcdef");
            session.TransitionTo(SessionStatus.PrOpen);

            db.Sessions.Add(session);
            db.Events.Add(Event.Append(
                session.Id,
                1,
                EventTypes.FileWrite,
                """{"path":"src/Features/Quotes/QuoteWizard.cs"}"""));
            db.Events.Add(Event.Append(session.Id, 2, EventTypes.SessionEnded, """{"state":"completed"}"""));

            db.ChangeRequests.Add(ChangeRequest.Open(
                session.Id,
                StubRecapGenerator.ChangeRequestNumber,
                "https://github.com/acme/spectra/pull/7",
                "f00dcafe1234567890abcdef1234567890abcdef",
                ChangeRequestState.Open));

            await db.SaveChangesAsync(Token);
            return session.Id;
        });

    private static async Task<JobHandlingResult> HandleAsync(
        ControlPlaneInstance instance,
        IRecapGenerator generator,
        IRecapPublisher publisher,
        Guid sessionId)
        => await instance.InScopeAsync(async provider =>
        {
            var handler = new RecapJobHandler(
                provider.GetRequiredService<CharterDbContext>(),
                generator,
                publisher,
                new AlwaysResolvesCredential(),
                new RecapOptions(),
                provider.GetRequiredService<OrchestrationOptions>(),
                CharterTime.System,
                NullLogger<RecapJobHandler>.Instance);

            var job = new ClaimedJob(
                Guid.CreateVersion7(),
                JobType.Recap,
                new RecapJobPayload { SessionId = sessionId }.ToJson(),
                1,
                3,
                "test",
                DateTimeOffset.UtcNow.AddMinutes(5),
                []);

            return await handler.HandleAsync(job, Token);
        });

    private sealed class StubRecapGenerator : IRecapGenerator
    {
        public const int ChangeRequestNumber = 7;

        public const decimal CostUsd = 0.0412m;

        public int Calls { get; private set; }

        public Task<RecapResult> GenerateAsync(
            RecapEvidence evidence,
            ModelCredential credential,
            CancellationToken cancellationToken = default)
        {
            Calls++;

            return Task.FromResult(new RecapResult
            {
                SessionId = evidence.SessionId,
                BodyMarkdown = "## What was asked for\nThe wizard remembers the last vertical.",
                RankedFiles = [],
                RiskItemsJson = "[]",

                // The same content as the body, before it was rendered. The handler stores it beside
                // the prose so the API reads section 14's sections as data rather than parsing
                // headings back out of markdown.
                Document = new RecapDocument
                {
                    SummaryMd = "The wizard remembers the last vertical.",
                },
                Usage = new ModelUsage { InputTokens = 900, OutputTokens = 300 },
                Charge = new ModelCharge
                {
                    Unit = ModelChargeUnit.Usd,
                    CostUsd = CostUsd,
                    NotionalCostUsd = CostUsd,
                    Basis = ModelCostBasis.ProviderReported,
                },
            });
        }
    }

    private sealed class StubRecapPublisher : IRecapPublisher
    {
        public string? LastBody { get; private set; }

        public int? LastNumber { get; private set; }

        public Task<RecapPublication> PublishAsync(
            RecapResult recap,
            Repo repo,
            int? changeRequestNumber,
            CancellationToken cancellationToken = default)
        {
            LastBody = recap.BodyMarkdown;
            LastNumber = changeRequestNumber;

            return Task.FromResult(new RecapPublication(
                RecapSurface.ProviderComment,
                recap.BodyMarkdown,
                Reason: null));
        }
    }
}
