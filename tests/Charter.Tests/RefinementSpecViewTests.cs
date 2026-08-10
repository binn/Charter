using System.Reflection;
using Charter.Refinement;

namespace Charter.Tests;

/// <summary>
/// Section 10b: the structured spec is the single source of truth and the two renderings cannot
/// drift. These tests exist to make that a property of the code rather than of anyone's discipline.
/// </summary>
public class RefinementSpecViewTests
{
    private const string SecretApproach =
        "Add a nullable DerateFactor column to QuoteLine and backfill it in a data migration.";

    private static SpecDocument Sample() => SpecDocument.Create(
        "Show the derate factor on a quote",
        "Every quote line will show the derate factor next to the rated output.",
        ["Open any quote and each line shows a derate percentage.", "Printing a quote includes it."],
        SecretApproach,
        SpecScope.Of(["src/Features/Quotes/QuoteLine.razor"], ["src/Features/Quotes/**"]),
        ["The backfill touches every historic quote."],
        openQuestions: null);

    [Fact]
    public void RequesterViewHasNoMemberThatCouldExposeTheTechnicalApproach()
    {
        var members = typeof(RequesterSpecView)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Select(static member => member.Name)
            .ToList();

        // Not filtered, not nulled — simply not a member of the type, so it does not compile.
        Assert.DoesNotContain(members, static name =>
            name.Contains("Technical", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Approach", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Risk", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Scope", StringComparison.OrdinalIgnoreCase)
            || name.Contains("OpenQuestion", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RequesterRenderingNeverContainsTheTechnicalApproach()
    {
        var spec = Sample();

        var requester = spec.ForRequester().Render().Markdown;
        var engineer = spec.ForEngineer().Render().Markdown;

        Assert.DoesNotContain(SecretApproach, requester, StringComparison.Ordinal);
        Assert.DoesNotContain("QuoteLine.razor", requester, StringComparison.Ordinal);
        Assert.DoesNotContain("backfill", requester, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(SecretApproach, engineer, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRequesterViewIsAProjectionNotACopy()
    {
        var spec = Sample();

        var requester = spec.ForRequester();
        var engineer = spec.ForEngineer();

        // The same list instance, not an equal one: there is no second copy to fall out of step.
        Assert.Same(spec.AcceptanceCriteria, requester.AcceptanceCriteria);
        Assert.Same(spec.AcceptanceCriteria, engineer.AcceptanceCriteria);
        Assert.Same(requester.AcceptanceCriteria, engineer.AcceptanceCriteria);
    }

    [Fact]
    public void AcceptanceCriteriaAreByteIdenticalInBothRenderings()
    {
        var spec = Sample();

        var requester = spec.ForRequester().Render().Markdown;
        var engineer = spec.ForEngineer().Render().Markdown;
        var block = SpecRenderer.AcceptanceCriteriaBlock(spec.AcceptanceCriteria);

        Assert.Contains(block, requester, StringComparison.Ordinal);
        Assert.Contains(block, engineer, StringComparison.Ordinal);
        Assert.Equal(
            System.Text.Encoding.UTF8.GetBytes(ExtractCriteria(requester)),
            System.Text.Encoding.UTF8.GetBytes(ExtractCriteria(engineer)));
    }

    [Fact]
    public void ARequesterViewCannotBeBuiltFromSeparatelyAuthoredText()
    {
        var constructors = typeof(RequesterSpecView)
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        var single = Assert.Single(constructors);
        var parameter = Assert.Single(single.GetParameters());

        Assert.Equal(typeof(SpecDocument), parameter.ParameterType);
        Assert.False(single.IsPublic);
    }

    [Fact]
    public void EditingTheSpecRegeneratesBothViews()
    {
        var original = Sample();
        var requesterBefore = original.ForRequester().Render();
        var engineerBefore = original.ForEngineer().Render();

        var edited = original
            .WithAcceptanceCriteria(
            [
                "Open any quote and each line shows a derate percentage.",
                "Printing a quote includes it.",
                "The derate percentage is shown to one decimal place.",
            ])
            .WithTechnicalApproach("Render the factor from the existing calculation; no schema change.");

        var requesterAfter = edited.ForRequester().Render();
        var engineerAfter = edited.ForEngineer().Render();

        Assert.Contains("one decimal place", requesterAfter.Markdown, StringComparison.Ordinal);
        Assert.Contains("one decimal place", engineerAfter.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("one decimal place", requesterBefore.Markdown, StringComparison.Ordinal);

        // Still byte-identical after the edit.
        Assert.Equal(ExtractCriteria(requesterAfter.Markdown), ExtractCriteria(engineerAfter.Markdown));

        // And a rendering taken before the edit knows it is stale rather than quietly disagreeing.
        Assert.True(requesterBefore.Matches(original));
        Assert.False(requesterBefore.Matches(edited));
        Assert.False(engineerBefore.Matches(edited));
        Assert.True(requesterAfter.Matches(edited));
    }

    [Fact]
    public void TheRequesterViewAlwaysTracksTheDocumentItProjects()
    {
        var spec = Sample();
        var view = spec.ForRequester();

        Assert.Equal(spec.ContentHash, view.SourceContentHash);
        Assert.Equal(spec.Title, view.Title);
        Assert.Equal(spec.Outcome, view.Outcome);
    }

    [Fact]
    public void ASpecWithoutAcceptanceCriteriaIsNotASpec()
    {
        Assert.Throws<ArgumentException>(() =>
            SpecDocument.Create("Title", "Outcome", []));

        Assert.Throws<ArgumentException>(() =>
            SpecDocument.Create("Title", "Outcome", ["   "]));
    }

    [Fact]
    public void TheStoredRowRegeneratesItsBodyFromTheStructuredSpec()
    {
        var spec = Sample();
        var request = Guid.CreateVersion7();

        var row = SpecDocumentMapper.ToDraft(spec, request);

        Assert.Equal(spec.ForEngineer().Render().Markdown, row.BodyMd);

        // Reading back ignores the derived body entirely, so the structure stays authoritative.
        var roundTripped = SpecDocumentMapper.ToDocument(row);

        Assert.Equal(spec.ContentHash, roundTripped.ContentHash);
        Assert.Equal(spec.AcceptanceCriteria, roundTripped.AcceptanceCriteria);
        Assert.Equal(spec.TechnicalApproach, roundTripped.TechnicalApproach);
    }

    /// <summary>Pulls the acceptance-criteria block out of a rendering, by its heading.</summary>
    private static string ExtractCriteria(string markdown)
    {
        var heading = markdown.Contains(SpecRenderer.RequesterCriteriaHeading, StringComparison.Ordinal)
            ? SpecRenderer.RequesterCriteriaHeading
            : SpecRenderer.EngineerCriteriaHeading;

        var lines = markdown.Split('\n').Select(static line => line.TrimEnd('\r')).ToList();
        var start = lines.FindIndex(line => string.Equals(line, heading, StringComparison.Ordinal));
        Assert.True(start >= 0, $"No '{heading}' heading in the rendering.");

        var block = lines
            .Skip(start + 1)
            .SkipWhile(static line => line.Length == 0)
            .TakeWhile(static line => line.StartsWith("- ", StringComparison.Ordinal));

        return string.Join("\n", block);
    }
}
