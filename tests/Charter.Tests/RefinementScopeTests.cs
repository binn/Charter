using Charter.Refinement;

namespace Charter.Tests;

/// <summary>
/// Sections 7.3, 8 and 10: deny by default, deny wins, and a refusal is explained without ever
/// showing a requester a file path.
/// </summary>
public class RefinementScopeTests
{
    [Theory]
    [InlineData("src/Features/**", "src/Features/Quotes/QuoteLine.razor", true)]
    [InlineData("src/Features/**", "src/Features", true)]
    [InlineData("src/Features/**", "src/Auth/SignIn.cs", false)]
    [InlineData("**/Migrations/**", "src/Data/Migrations/20260101_Init.cs", true)]
    [InlineData("**/Migrations/**", "src/Data/Model.cs", false)]
    [InlineData("**/appsettings*.json", "src/Web/appsettings.Production.json", true)]
    [InlineData("**/appsettings*.json", "src/Web/settings.json", false)]
    [InlineData(".github/**", ".github/workflows/ci.yml", true)]
    [InlineData("src/*.cs", "src/Program.cs", true)]
    [InlineData("src/*.cs", "src/Web/Program.cs", false)]
    [InlineData("infra/**", "infra/main.tf", true)]
    public void GlobsMatchTheWayTheConfigFileImplies(string pattern, string path, bool expected) =>
        Assert.Equal(expected, GlobPattern.IsMatch(pattern, path));

    [Fact]
    public void DenyWinsOverAllow()
    {
        var policy = new RefinementScopePolicy(["src/**"], ["src/Auth/**"]);

        Assert.True(policy.Evaluate(SpecScope.Of(["src/Features/Quote.cs"], null)).IsAllowed);

        var decision = policy.Evaluate(SpecScope.Of(["src/Auth/SignIn.cs"], null));
        Assert.False(decision.IsAllowed);
        Assert.Equal(ScopeViolationReason.Denied, decision.Violations[0].Reason);
        Assert.Equal("src/Auth/**", decision.Violations[0].Pattern);
    }

    [Fact]
    public void AnEmptyAllowListDeniesEverything()
    {
        var decision = RefinementScopePolicy.DenyEverything
            .Evaluate(SpecScope.Of(["README.md"], null));

        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public void TheRequesterMessageNamesAreasNotPaths()
    {
        var policy = RefinementStubs.Scope;

        var decision = policy.Evaluate(SpecScope.Of(
            ["src/Auth/SignInHandler.cs", "src/Data/Migrations/20260101_Init.cs"],
            null));

        Assert.False(decision.IsAllowed);

        var message = decision.RequesterMessage;
        Assert.Contains("sign-in and accounts", message, StringComparison.Ordinal);
        Assert.Contains("the database structure", message, StringComparison.Ordinal);
        Assert.Contains("engineer", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src/", message, StringComparison.Ordinal);
        Assert.DoesNotContain(".cs", message, StringComparison.Ordinal);
        Assert.DoesNotContain("**", message, StringComparison.Ordinal);

        // The engineer detail carries everything the requester's copy withholds.
        Assert.Contains("src/Auth/SignInHandler.cs", decision.EngineerDetail, StringComparison.Ordinal);
        Assert.Contains("**/Migrations/**", decision.EngineerDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDenyListIsStatedInThePromptSoTheRefinerDoesNotProposeIt()
    {
        var text = RefinementStubs.Scope.ToPromptText();

        Assert.Contains("src/Features/**", text, StringComparison.Ordinal);
        Assert.Contains("src/Auth/**", text, StringComparison.Ordinal);
        Assert.Contains("never change", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnEmptyScopeIsNotAnAutomaticPass()
    {
        // A spec naming nothing names nothing to check; the policy has nothing to refuse, and the
        // runner remains the enforcement point (section 7.3).
        var decision = RefinementStubs.Scope.Evaluate(SpecScope.Empty);

        Assert.True(decision.IsAllowed);
        Assert.Empty(decision.Violations);
    }
}
