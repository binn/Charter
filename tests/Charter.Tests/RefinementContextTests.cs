using Charter.Refinement;

namespace Charter.Tests;

/// <summary>
/// Sections 8 and 26.3: the committed `.charter/` files and the organisation's standards are what
/// refinement is grounded in. Unknown keys warn; they never fail.
/// </summary>
public class RefinementContextTests
{
    [Fact]
    public void TheGlossaryIsParsedAndReachesThePrompt()
    {
        var glossary = GlossaryDocument.Parse(RefinementStubs.Glossary);

        Assert.Equal(2, glossary.Terms.Count);
        Assert.Contains("Bill of Quantities", glossary.Terms["BOQ"], StringComparison.Ordinal);
        Assert.Contains("BOQ", glossary.ToPromptText(), StringComparison.Ordinal);

        // The `version: 1` key is not a term.
        Assert.False(glossary.Terms.ContainsKey("version"));
    }

    [Fact]
    public void AGlossaryEntryThatIsNotADefinitionWarnsRatherThanFailing()
    {
        var warnings = new List<string>();

        var glossary = GlossaryDocument.Parse(
            """
            version: 1
            BOQ: "Bill of Quantities."
            nested:
              something: else
            """,
            warnings);

        Assert.Single(glossary.Terms);
        Assert.Single(warnings);
    }

    [Fact]
    public void MalformedGlossaryYamlIsIgnoredRatherThanBreakingRefinement()
    {
        var warnings = new List<string>();

        var glossary = GlossaryDocument.Parse("this: [is: not: yaml", warnings);

        Assert.True(glossary.IsEmpty);
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void StandardsAreFlattenedForInjectionAndPinTheirVersion()
    {
        var standards = StandardsDocument.Parse(RefinementStubs.Standards);

        Assert.Equal(3, standards.Version);

        var text = standards.ToPromptText();
        Assert.Contains("stacks.web.backend.runtime: dotnet", text, StringComparison.Ordinal);
        Assert.Contains("stacks.web.database.engine: postgres", text, StringComparison.Ordinal);
        Assert.Contains("services.ai.provider: openrouter", text, StringComparison.Ordinal);
        Assert.Contains(".charter/config.yml", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownStandardsKeysWarnRatherThanFail()
    {
        var warnings = new List<string>();

        var standards = StandardsDocument.Parse(
            """
            version: 4
            services:
              ai: { provider: "openrouter" }
            some_future_key:
              anything: here
            """,
            warnings);

        Assert.Equal(4, standards.Version);
        Assert.Contains(warnings, warning => warning.Contains("some_future_key", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEmptyContextRefusesEverythingByDefault()
    {
        var decision = RefinementContext.Bare.Scope.Evaluate(SpecScope.Of(["src/Features/A.cs"], null));

        Assert.False(decision.IsAllowed);
        Assert.Equal(ScopeViolationReason.OutsideAllowList, decision.Violations[0].Reason);
    }

    [Fact]
    public void ThePromptOmitsSectionsTheRepoHasNotWritten()
    {
        var builder = new RefinementPromptBuilder();

        var prompt = builder.BuildSystemPrompt(
            new RefinementContext { Scope = RefinementStubs.Scope },
            InteractionMode.Plan);

        Assert.DoesNotContain("What the words mean here", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Organisation standards", prompt, StringComparison.Ordinal);
        Assert.Contains("## Scope", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatModePromptSaysItProducesNothing()
    {
        var builder = new RefinementPromptBuilder();

        var prompt = builder.BuildSystemPrompt(RefinementStubs.Context(), InteractionMode.Chat);

        Assert.Contains("never propose a change", prompt, StringComparison.OrdinalIgnoreCase);
    }
}
