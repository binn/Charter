using System.Text.Json;
using Charter.Api.Requests;
using Charter.Domain;
using Charter.Recaps;
using Charter.Refinement;

namespace Charter.Tests;

/// <summary>
/// Section 14's engineer recap, read back out of the row the composer wrote.
/// </summary>
/// <remarks>
/// Every test here composes a <em>real</em> recap through <see cref="RecapComposer"/> and
/// <see cref="RecapFileRiskRanker"/> before projecting it. That is the point: the projection reads
/// back a markdown structure the composer owns, and a fixture that wrote its own markdown would let
/// the two drift apart while staying green. If somebody renames a heading in the composer, these
/// fail — which is the coupling being made visible rather than hidden.
/// </remarks>
public class ApiRecapTests
{
    [Fact]
    public void TheFileListArrivesRiskRankedRatherThanAlphabetical()
    {
        // Section 14's specific complaint: alphabetical ordering puts `src/Auth/TokenIssuer.cs` below
        // `docs/README.md` for no reason anyone can defend. The client does not re-sort, so the order
        // on the wire is the only ordering a reviewer ever sees.
        var recap = Compose(
        [
            new RecapFileChange("docs/README.md") { LinesAdded = 3 },
            new RecapFileChange("tests/Quotes/WizardTests.cs") { LinesAdded = 40 },
            new RecapFileChange("src/Auth/TokenIssuer.cs") { LinesAdded = 9, LinesRemoved = 2 },
            new RecapFileChange("src/Data/Migrations/0002_AddPreference.cs") { LinesAdded = 34 },
        ]);

        var card = RecapProjection.Card(recap, Session(), changeRequestUrl: null, changeRequestTerm: null);

        Assert.NotNull(card);

        var paths = card.Files.Select(file => file.Path).ToList();

        // Auth and the migration float, in whichever order the ranker's weights put them.
        Assert.Equal(
            ["src/Auth/TokenIssuer.cs", "src/Data/Migrations/0002_AddPreference.cs"],
            paths[..2].OrderBy(path => path, StringComparer.Ordinal));

        // Tests and documentation sink, and they sink below everything.
        Assert.Equal(["tests/Quotes/WizardTests.cs", "docs/README.md"], paths[^2..]);

        // Alphabetical would have put docs first. This is the assertion that the ranking survived.
        Assert.NotEqual(paths.OrderBy(path => path, StringComparer.Ordinal), paths);
    }

    [Fact]
    public void EveryRankedFileExplainsItself()
    {
        // Section 14's ranking is only useful if the reviewer can see the reasoning; an unexplained
        // "high" is noise.
        var recap = Compose([new RecapFileChange("src/Auth/TokenIssuer.cs") { LinesAdded = 9 }]);
        var card = RecapProjection.Card(recap, Session(), null, null);

        var file = Assert.Single(card!.Files);

        Assert.Equal(Charter.Api.Contracts.ApiChangeRisk.High, file.Risk);
        Assert.NotNull(file.RiskReasons);
        Assert.Contains(file.RiskReasons, reason => reason.Contains("identity", StringComparison.Ordinal));
    }

    [Fact]
    public void TheReviewOrderIsTheRankersAndStartsWhereTheRiskIs()
    {
        var recap = Compose(
        [
            new RecapFileChange("docs/README.md"),
            new RecapFileChange("src/Billing/InvoiceTotals.cs") { LinesAdded = 20 },
        ]);

        var card = RecapProjection.Card(recap, Session(), null, null);

        Assert.Equal("src/Billing/InvoiceTotals.cs", card!.ReviewOrder[0]);
    }

    [Fact]
    public void TheDeviationsSurviveTheRoundTripWithTheDistinctionThatMatters()
    {
        // "The spec said X and it did Y" and "the spec was silent and it chose Y" need different
        // amounts of scrutiny, so the second must not arrive with an invented `specSaid`.
        var card = RecapProjection.Card(
            Compose([new RecapFileChange("src/Quotes/Wizard.cs")]),
            Session(),
            null,
            null);

        Assert.Equal(2, card!.Deviations.Count);

        var quoted = card.Deviations[0];
        Assert.Equal("Keyed the preference on the auth user id", quoted.AgentDid[..40]);
        Assert.Equal("The remembered choice is yours alone", quoted.SpecSaid);
        Assert.Equal("src/Auth/LoginController.cs", quoted.Path);

        // Nothing was quoted for the second, so there is no key at all.
        Assert.Null(card.Deviations[1].SpecSaid);
        Assert.Contains("Cached the resolved user id", card.Deviations[1].AgentDid, StringComparison.Ordinal);

        // The reason the transcript gave is kept rather than dropped on the floor.
        Assert.Contains("Reason given:", quoted.AgentDid, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatCouldNotBeVerifiedComesBackAsItemsRatherThanAParagraph()
    {
        var card = RecapProjection.Card(Compose([new RecapFileChange("src/A.cs")]), Session(), null, null);

        var note = Assert.Single(card!.CouldNotVerify);
        Assert.Equal("No test covers two tabs picking different verticals.", note.Text);
        Assert.NotEmpty(note.Id);
    }

    [Fact]
    public void AnAutoDispatchedSessionLeadsWithItAndCarriesTheSpecInFull()
    {
        // Section 7.5: nobody vetted the spec, and the engineer must know that before reading a line
        // of the diff. A summary of an unreviewed specification is not reviewable.
        var recap = Compose([new RecapFileChange("src/A.cs")], autoDispatched: true);
        var card = RecapProjection.Card(recap, Session(autoDispatched: true), null, null);

        Assert.True(card!.AutoDispatched);
        Assert.NotNull(card.SpecMd);
        Assert.Contains("Remember the last selected vertical", card.SpecMd, StringComparison.Ordinal);
    }

    [Fact]
    public void AVettedSessionCarriesNoSpecBodyAtAll()
    {
        var card = RecapProjection.Card(Compose([new RecapFileChange("src/A.cs")]), Session(), null, null);

        Assert.False(card!.AutoDispatched);
        Assert.Null(card.SpecMd);
    }

    [Fact]
    public async Task TheCardCarriesNoVerdictShapedFieldForAClientToRender()
    {
        // Section 14: "it must never say 'looks good'". The generation guard enforces that in the
        // prose; this is the structural half — there is nothing on the object to render as a badge.
        var body = await ApiPayloads.RenderAsync(
            RecapProjection.Card(Compose([new RecapFileChange("src/A.cs")]), Session(), null, null));

        var keys = ApiPayloads.Keys(body);

        foreach (var verdict in new[] { "verdict", "score", "passed", "approved", "quality", "rating", "grade" })
        {
            Assert.DoesNotContain(verdict, keys);
        }
    }

    [Fact]
    public void WhereItWasPostedIsAbsentWhenThereWasNowhereToPostIt()
    {
        // Section 14: "post it as a change request comment where the provider has one, and in the
        // session view where it does not". Absent means this view is the only copy.
        var recap = Compose([new RecapFileChange("src/A.cs")]);

        var nowhere = RecapProjection.Card(recap, Session(), changeRequestUrl: null, changeRequestTerm: "pull request");
        Assert.Null(nowhere!.PostedToUrl);
        Assert.Null(nowhere.PostedToTerm);

        var posted = RecapProjection.Card(
            recap,
            Session(),
            "https://github.com/northbeam/quote-tool/pull/142",
            "pull request");

        Assert.Equal("https://github.com/northbeam/quote-tool/pull/142", posted!.PostedToUrl);

        // The provider supplies the noun; the UI never assumes GitHub's vocabulary.
        Assert.Equal("pull request", posted.PostedToTerm);
    }

    [Fact]
    public void ARowWrittenByAnotherBuildDegradesRatherThanFailingTheWholeDetail()
    {
        // The risk items are jsonb. A shape this build cannot read must cost the file list, not the
        // request detail the card is embedded in.
        var recap = Recap.Generate(Guid.CreateVersion7(), "## Session recap\n", "{\"not\":\"an array\"}", 0m);
        var card = RecapProjection.Card(recap, Session(), null, null);

        Assert.NotNull(card);
        Assert.Empty(card.Files);
        Assert.Empty(card.ReviewOrder);
    }

    [Fact]
    public async Task TheProjectedCardOmitsWhatItHasNoAnswerFor()
    {
        var body = await ApiPayloads.RenderAsync(
            RecapProjection.Card(Compose([new RecapFileChange("src/A.cs")]), Session(), null, null));

        using var document = JsonDocument.Parse(body);

        Assert.False(document.RootElement.TryGetProperty("specMd", out _));
        Assert.False(document.RootElement.TryGetProperty("postedToUrl", out _));
        Assert.False(document.RootElement.TryGetProperty("postedToTerm", out _));
    }

    private static Session Session(bool autoDispatched = false)
        => Charter.Domain.Session.Queue(
            Guid.CreateVersion7(),
            RunnerKind.GitHubActions,
            "anthropic/claude-opus-5",
            autoDispatched: autoDispatched);

    /// <summary>Composes a genuine recap row: real ranker, real composer, real markdown.</summary>
    private static Recap Compose(IReadOnlyList<RecapFileChange> files, bool autoDispatched = false)
    {
        var document = SpecDocument.Create(
            "Remember the last selected vertical",
            "When you start a new quote, the vertical you chose last time is already selected.",
            ["Starting a new quote pre-selects the vertical you chose on your previous quote."],
            "Add a per-user preference row and read it on quote creation.");

        var payload = new RecapPayload
        {
            WhatAndWhy = "The wizard read the vertical off the quote row, so this adds a per-user preference.",
            Deviations =
            [
                new RecapDeviationPayload
                {
                    What = "Keyed the preference on the auth user id",
                    SpecSaid = "The remembered choice is yours alone",
                    Why = "the wizard only has the auth id in scope",
                    Where = "src/Auth/LoginController.cs",
                },
                new RecapDeviationPayload { What = "Cached the resolved user id on the accessor" },
            ],
            CouldNotVerify = ["No test covers two tabs picking different verticals."],
        };

        var sessionId = Guid.CreateVersion7();

        var (body, riskItems, recapDocument, _) = RecapComposer.Compose(
            new RecapEvidence { SessionId = sessionId, Spec = document, AutoDispatched = autoDispatched },
            RecapFileRiskRanker.Rank(files),
            payload,
            new RecapOptions());

        return Recap.Generate(sessionId, body, riskItems, 1.42m, payloadJson: recapDocument.ToJson());
    }
}
