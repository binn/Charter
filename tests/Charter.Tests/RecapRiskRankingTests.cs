using Charter.Recaps;

namespace Charter.Tests;

/// <summary>
/// Section 14, part 3: the file list is <strong>risk-ranked, not alphabetical</strong>. Auth,
/// migrations, money maths, external calls and denylist-adjacent paths float; tests and formatting
/// sink. None of this involves a model, which is the point — the ordering an engineer acts on is
/// computed by Charter and is therefore assertable.
/// </summary>
public class RecapRiskRankingTests
{
    [Fact]
    public void AMigrationAndAnAuthFileOutrankAFormattingChange()
    {
        var ranked = RecapFileRiskRanker.Rank(
        [
            new RecapFileChange("src/Web/Components/StatusTable.razor") { FormattingOnly = true },
            new RecapFileChange("src/Auth/TokenIssuer.cs") { LinesAdded = 12 },
            new RecapFileChange("src/Data/Migrations/20260810_AddVertical.cs") { LinesAdded = 40 },
        ]);

        Assert.Equal("src/Data/Migrations/20260810_AddVertical.cs", ranked[0].Path);
        Assert.Equal("src/Auth/TokenIssuer.cs", ranked[1].Path);
        Assert.Equal("src/Web/Components/StatusTable.razor", ranked[2].Path);

        Assert.Contains(RecapRiskFactor.Migration, ranked[0].Factors);
        Assert.Contains(RecapRiskFactor.Auth, ranked[1].Factors);
        Assert.Contains(RecapRiskFactor.Formatting, ranked[2].Factors);
        Assert.Equal(RecapRiskBand.Minimal, ranked[2].Band);
    }

    [Fact]
    public void TheOrderIsNeverAlphabetical()
    {
        // Alphabetically this is exactly the order given. By risk it is close to reversed, which is
        // the failure section 14 names: an alphabetical list buries a migration under a README.
        var files = new[]
        {
            new RecapFileChange("docs/README.md"),
            new RecapFileChange("src/Billing/InvoiceTotals.cs"),
            new RecapFileChange("tests/Charter.Tests/RenderingTests.cs"),
            new RecapFileChange("zz/Migrations/0002_DropColumn.cs"),
        };

        var ranked = RecapFileRiskRanker.Rank(files);
        var paths = ranked.Select(static file => file.Path).ToList();

        Assert.NotEqual(paths.Order(StringComparer.Ordinal).ToList(), paths);
        Assert.Equal("zz/Migrations/0002_DropColumn.cs", paths[0]);
        Assert.Equal("src/Billing/InvoiceTotals.cs", paths[1]);

        // Tests and documentation both sink below the ordinary baseline.
        Assert.All(ranked.TakeLast(2), file => Assert.Equal(RecapRiskBand.Minimal, file.Band));
    }

    [Fact]
    public void MoneyMathsAndExternalCallsFloatAboveOrdinaryCode()
    {
        var ranked = RecapFileRiskRanker.Rank(
        [
            new RecapFileChange("src/Features/Dashboard/StatusPanel.razor"),
            new RecapFileChange("src/Integrations/StripeClient.cs"),
            new RecapFileChange("src/Pricing/TaxCalculator.cs"),
        ]);

        Assert.Equal("src/Pricing/TaxCalculator.cs", ranked[0].Path);
        Assert.Contains(RecapRiskFactor.MoneyMath, ranked[0].Factors);

        Assert.Equal("src/Integrations/StripeClient.cs", ranked[1].Path);
        Assert.Contains(RecapRiskFactor.ExternalCall, ranked[1].Factors);

        Assert.Equal("src/Features/Dashboard/StatusPanel.razor", ranked[2].Path);
        Assert.Empty(ranked[2].Factors);
    }

    [Fact]
    public void ADenylistedPathOutranksEverythingAndASiblingOfOneIsFlaggedAdjacent()
    {
        var deny = new[] { "src/Auth/**", "**/Migrations/**" };

        var ranked = RecapFileRiskRanker.Rank(
        [
            new RecapFileChange("src/Features/Dashboard/StatusPanel.razor"),
            new RecapFileChange("src/AuthShared/ClaimsMap.cs"),
            new RecapFileChange("src/Auth/Cookies.cs"),
        ],
            deny);

        Assert.Equal("src/Auth/Cookies.cs", ranked[0].Path);
        Assert.Contains(RecapRiskFactor.Denylisted, ranked[0].Factors);
        Assert.Equal(RecapRiskBand.Critical, ranked[0].Band);

        var adjacent = ranked.Single(file => file.Path == "src/AuthShared/ClaimsMap.cs");
        Assert.Contains(RecapRiskFactor.DenylistAdjacent, adjacent.Factors);

        // A file that merely lives under the same top-level folder is not "adjacent" to anything.
        var ordinary = ranked.Single(file => file.Path == "src/Features/Dashboard/StatusPanel.razor");
        Assert.DoesNotContain(RecapRiskFactor.DenylistAdjacent, ordinary.Factors);
    }

    [Fact]
    public void TiesBreakOnTheSizeOfTheChangeAndThenOnTheOrderGiven()
    {
        var ranked = RecapFileRiskRanker.Rank(
        [
            new RecapFileChange("src/Features/A.cs") { LinesAdded = 2 },
            new RecapFileChange("src/Features/B.cs") { LinesAdded = 200 },
            new RecapFileChange("src/Features/C.cs") { LinesAdded = 2 },
        ]);

        Assert.Equal("src/Features/B.cs", ranked[0].Path);
        Assert.Equal("src/Features/A.cs", ranked[1].Path);
        Assert.Equal("src/Features/C.cs", ranked[2].Path);
    }

    [Fact]
    public void EveryRankedFileCarriesTheReasonItScored()
    {
        var ranked = RecapFileRiskRanker.Rank([new RecapFileChange("src/Auth/Passwords.cs")]);

        Assert.Single(ranked);
        Assert.NotEmpty(ranked[0].Reasons);
        Assert.Equal(ranked[0].Factors.Count, ranked[0].Reasons.Count);
    }
}
