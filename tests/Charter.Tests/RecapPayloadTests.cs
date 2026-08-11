using System.Text.Json;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Charter.Recaps;
using Microsoft.EntityFrameworkCore;

namespace Charter.Tests;

/// <summary>
/// The structured recap on the row (section 14).
/// </summary>
/// <remarks>
/// <c>body_md</c> is what gets posted as a change request comment, so it stays. <c>payload</c> is
/// the same content as data, and it exists so nothing downstream has to parse section headings back
/// out of markdown to serve the recap card — a coupling that made renaming
/// <c>### 2. Where this deviated from the specification</c> an undeclared API change.
/// </remarks>
public class RecapPayloadTests
{
    private const string DatabaseUrlVariable = "CHARTER_TEST_DATABASE_URL";

    [Fact]
    public void TheDocumentCarriesTheFourSectionsTheApiUsedToParseOutOfTheProse()
    {
        var (_, _, document, _) = Compose(new RecapPayload
        {
            WhatAndWhy = "Added a derate column to quote lines.",
            Deviations =
            [
                new RecapDeviationPayload
                {
                    What = "Stored the derate as a fraction",
                    SpecSaid = "a percentage",
                    Why = "the rest of the table is fractional",
                    Where = "src/Quotes/QuoteLine.cs",
                },
                new RecapDeviationPayload { What = "Chose an index name the spec did not cover" },
            ],
            CouldNotVerify = ["No test covers a zero-quantity line."],
        });

        Assert.Equal(RecapDocument.CurrentVersion, document.Version);
        Assert.Equal("Added a derate column to quote lines.", document.SummaryMd);

        Assert.Equal(2, document.Deviations.Count);
        Assert.Equal("Stored the derate as a fraction", document.Deviations[0].What);
        Assert.Equal("a percentage", document.Deviations[0].SpecSaid);
        Assert.Equal("src/Quotes/QuoteLine.cs", document.Deviations[0].Where);

        // The distinction that matters: "the spec said X and it did Y" versus "the spec was silent
        // and it chose Y" need different amounts of scrutiny, so an absent clause stays absent
        // rather than becoming an empty string.
        Assert.Null(document.Deviations[1].SpecSaid);
        Assert.Null(document.Deviations[1].Why);

        Assert.Equal(["No test covers a zero-quantity line."], document.CouldNotVerify);
    }

    [Fact]
    public void TheSpecificationTravelsWithAnAutoDispatchedRecapAndWithNoOther()
    {
        // Section 7.5: nobody vetted the spec, so the recap leads with that and carries the
        // specification in full rather than summarised.
        var (body, _, auto, _) = Compose(
            new RecapPayload { WhatAndWhy = "Built it." },
            autoDispatched: true);

        Assert.True(auto.AutoDispatched);
        Assert.NotNull(auto.SpecMd);
        Assert.Contains(auto.SpecMd, body, StringComparison.Ordinal);

        var (_, _, reviewed, _) = Compose(new RecapPayload { WhatAndWhy = "Built it." });

        Assert.False(reviewed.AutoDispatched);
        Assert.Null(reviewed.SpecMd);
    }

    [Fact]
    public void AQualityJudgementIsScrubbedOutOfTheStructureAsWellAsOutOfTheProse()
    {
        // Section 14: it must never say "looks good". Serving the payload instead of the markdown
        // must not hand a reader a sentence the guard already took out of the body.
        var (body, _, document, removed) = Compose(new RecapPayload
        {
            WhatAndWhy = "Added the column. The implementation looks good.",
            CouldNotVerify = ["This all looks good to me."],
        });

        Assert.True(removed > 0);
        Assert.DoesNotContain("looks good", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("looks good", document.SummaryMd, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            document.CouldNotVerify,
            note => note.Contains("looks good", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(removed, document.VerdictsRemoved);
    }

    [Fact]
    public void TheRiskItemsCarryLineCountsWhereTheSourceReportedThem()
    {
        var ranked = RecapFileRiskRanker.Rank(
        [
            new RecapFileChange("src/Auth/TokenIssuer.cs") { LinesAdded = 184, LinesRemoved = 6 },
            new RecapFileChange("docs/README.md") { Kind = RecapFileChangeKind.Added },
        ]);

        var (_, riskItemsJson, _, _) = RecapComposer.Compose(
            RecapStubs.Evidence(),
            ranked,
            new RecapPayload { WhatAndWhy = "Rotated the signing key." },
            new RecapOptions());

        var items = JsonSerializer.Deserialize<List<RecapRiskItem>>(
            riskItemsJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var auth = items.Single(item => item.Path == "src/Auth/TokenIssuer.cs");
        Assert.Equal(184, auth.Additions);
        Assert.Equal(6, auth.Deletions);
        Assert.True(auth.Counted);

        // Zero means nobody counted, not that nothing changed, and Counted is how a renderer tells
        // the two apart instead of printing "+0 -0" beside a new file.
        var readme = items.Single(item => item.Path == "docs/README.md");
        Assert.Equal(0, readme.Additions);
        Assert.False(readme.Counted);
        Assert.Equal("added", readme.Kind);
    }

    [Fact]
    public void LineCountsAreReadFromTheTranscriptWhereTheAgentReportedThem()
    {
        var sessionId = Guid.CreateVersion7();
        var at = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

        var files = RecapEventReader.ReadFileChanges(
        [
            Event.Append(sessionId, 1, EventTypes.FileWrite, """{"path":"src/A.cs","additions":10,"deletions":2}""", at),
            Event.Append(sessionId, 2, EventTypes.FileWrite, """{"path":"src/A.cs","additions":5,"deletions":1}""", at),
            Event.Append(sessionId, 3, EventTypes.FileWrite, """{"path":"src/B.cs"}""", at),
        ]);

        // An agent that edits one file four times has changed four runs of lines in it.
        var a = files.Single(file => file.Path == "src/A.cs");
        Assert.Equal(15, a.LinesAdded);
        Assert.Equal(3, a.LinesRemoved);

        // Nothing reported a count for B, so it keeps zeroes rather than acquiring an invented one.
        Assert.Equal(0, files.Single(file => file.Path == "src/B.cs").LinesChanged);
    }

    [Fact]
    public void ACountIsNotSpreadAcrossSeveralFilesInOneEvent()
    {
        var sessionId = Guid.CreateVersion7();
        var at = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

        var files = RecapEventReader.ReadFileChanges(
        [
            Event.Append(
                sessionId,
                1,
                EventTypes.FileWrite,
                """{"paths":["src/A.cs","src/B.cs"],"additions":42}""",
                at),
        ]);

        // Attributing "+42" to both would invent one of the two numbers.
        Assert.All(files, file => Assert.Equal(0, file.LinesChanged));
    }

    [Fact]
    public void AnUnreadablePayloadReadsAsAbsentRatherThanThrowing()
    {
        // A row written by a build that spelled the payload differently is not a reason to fail a
        // whole request detail.
        Assert.Same(RecapDocument.Empty, RecapDocument.Parse(null));
        Assert.Same(RecapDocument.Empty, RecapDocument.Parse("{"));
        Assert.Equal(string.Empty, RecapDocument.Parse("{}").SummaryMd);
    }

    [Fact]
    public async Task ThePayloadAndItsLineCountsSurviveARoundTripThroughPostgres()
    {
        var url = Environment.GetEnvironmentVariable(DatabaseUrlVariable);
        if (string.IsNullOrWhiteSpace(url))
        {
            Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the recap round trip.");
            return;
        }

        var options = new DbContextOptionsBuilder<CharterDbContext>();
        DataServiceCollectionExtensions.ConfigureNpgsql(options, DatabaseUrl.ToNpgsql(url));

        await using var db = new CharterDbContext(options.Options);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var tag = Guid.CreateVersion7().ToString("N");
        var organization = Organization.Create($"recap-payload-{tag}");
        var user = User.Create($"requester-{tag}@charter.invalid", "Requester");
        var repo = Repo.Connect(organization.Id, 606, $"charter/recap-{tag}");
        var request = Request.File(organization.Id, repo.Id, user.Id, "Totals are wrong.");
        var spec = Spec.Draft(request.Id, 1, "Fix totals", "Totals are right", "body", "[]");
        var session = Session.Queue(spec.Id, RunnerKind.Agent, "anthropic/claude-opus-5");

        db.Organizations.Add(organization);
        db.Users.Add(user);
        db.Repos.Add(repo);
        db.Requests.Add(request);
        db.Specs.Add(spec);
        db.Sessions.Add(session);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ranked = RecapFileRiskRanker.Rank(
        [
            new RecapFileChange("src/Auth/TokenIssuer.cs") { LinesAdded = 184, LinesRemoved = 6 },
        ]);

        var composed = RecapComposer.Compose(
            RecapStubs.Evidence(),
            ranked,
            new RecapPayload
            {
                WhatAndWhy = "Rotated the signing key.",
                Deviations = [new RecapDeviationPayload { What = "Kept the old key valid for an hour" }],
                CouldNotVerify = ["Nothing exercises the rotation path under load."],
            },
            new RecapOptions());

        db.Recaps.Add(Domain.Recap.Generate(
            session.Id,
            composed.BodyMarkdown,
            composed.RiskItemsJson,
            costUsd: 0.04m,
            payloadJson: composed.Document.ToJson()));

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // A different context entirely, which is what a restarted container has.
        await using var restarted = new CharterDbContext(options.Options);
        var row = await restarted.Recaps
            .AsNoTracking()
            .SingleAsync(candidate => candidate.SessionId == session.Id, TestContext.Current.CancellationToken);

        // jsonb, so Postgres normalises the document on the way in - which is why this reads the
        // value back rather than comparing the bytes that went out.
        var document = RecapDocument.Parse(row.Payload);

        Assert.Equal("Rotated the signing key.", document.SummaryMd);
        Assert.Equal("Kept the old key valid for an hour", Assert.Single(document.Deviations).What);
        Assert.Equal("Nothing exercises the rotation path under load.", Assert.Single(document.CouldNotVerify));

        var items = JsonSerializer.Deserialize<List<RecapRiskItem>>(
            row.RiskItems,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(184, Assert.Single(items).Additions);
        Assert.Equal(6, items[0].Deletions);

        // And the prose is still there, because it is what gets posted as a provider comment.
        Assert.Contains("### 1. What changed, and why", row.BodyMd, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARecapWrittenWithoutAPayloadStoresAnEmptyObjectRatherThanNull()
    {
        var url = Environment.GetEnvironmentVariable(DatabaseUrlVariable);
        if (string.IsNullOrWhiteSpace(url))
        {
            Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the recap round trip.");
            return;
        }

        var options = new DbContextOptionsBuilder<CharterDbContext>();
        DataServiceCollectionExtensions.ConfigureNpgsql(options, DatabaseUrl.ToNpgsql(url));

        await using var db = new CharterDbContext(options.Options);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var tag = Guid.CreateVersion7().ToString("N");
        var organization = Organization.Create($"recap-empty-{tag}");
        var user = User.Create($"requester-{tag}@charter.invalid", "Requester");
        var repo = Repo.Connect(organization.Id, 605, $"charter/recap-empty-{tag}");
        var request = Request.File(organization.Id, repo.Id, user.Id, "Totals are wrong.");
        var spec = Spec.Draft(request.Id, 1, "Fix totals", "Totals are right", "body", "[]");
        var session = Session.Queue(spec.Id, RunnerKind.Agent, "anthropic/claude-opus-5");

        db.Organizations.Add(organization);
        db.Users.Add(user);
        db.Repos.Add(repo);
        db.Requests.Add(request);
        db.Specs.Add(spec);
        db.Sessions.Add(session);
        db.Recaps.Add(Domain.Recap.Generate(session.Id, "## Session recap", "[]", 0m));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var restarted = new CharterDbContext(options.Options);
        var row = await restarted.Recaps
            .AsNoTracking()
            .SingleAsync(candidate => candidate.SessionId == session.Id, TestContext.Current.CancellationToken);

        Assert.Equal("{}", row.Payload);
        Assert.Equal(string.Empty, RecapDocument.Parse(row.Payload).SummaryMd);
    }

    private static RecapComposer.RecapComposition Compose(RecapPayload payload, bool autoDispatched = false)
        => RecapComposer.Compose(
            RecapStubs.Evidence() with { AutoDispatched = autoDispatched },
            RecapFileRiskRanker.Rank([new RecapFileChange("src/Quotes/QuoteLine.cs")]),
            payload,
            new RecapOptions());
}
