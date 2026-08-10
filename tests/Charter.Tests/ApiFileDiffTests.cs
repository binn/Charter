using System.Text.Json;
using Charter.Api.Changes;

namespace Charter.Tests;

/// <summary>
/// Pane 3 (section 12): one file's before and after, and what happens when there is not one.
/// </summary>
/// <remarks>
/// The interesting cases here are the honest failures. A binary file and a 40 MB generated bundle
/// both have to arrive as something the pane can say a true sentence about — section 27.4's
/// <em>do not pretend parity</em> is the same rule in a different pane, and silently sending the
/// first quarter of a file while Monaco presents it as the whole thing is exactly the pretence.
/// </remarks>
public class ApiFileDiffTests
{
    public static TheoryData<string, string> Languages => new()
    {
        { "src/Quotes/QuoteWizard.cs", "csharp" },
        { "src/Quotes/QuoteWizard.razor", "razor" },
        { "ClientApp/src/api/client.ts", "typescript" },
        { "ClientApp/src/App.tsx", "typescript" },
        { "docs/runners.md", "markdown" },
        { "docker-compose.yml", "yaml" },
        { "Charter.csproj", "xml" },
        { "Dockerfile", "dockerfile" },
        { ".gitignore", "ignore" },
        { "migrations/0001_init.sql", "sql" },
        { "assets/logo.png", "plaintext" },
        { "LICENSE", "plaintext" },
    };

    [Theory]
    [MemberData(nameof(Languages))]
    public void TheLanguageIsResolvedFromThePathOnTheServer(string path, string expected)
        => Assert.Equal(expected, MonacoLanguages.For(path));

    [Fact]
    public void AnUnrecognisedFileIsPlaintextRatherThanAGuess()
    {
        // Highlighting an unknown file as whatever the last extension in the table was is worse than
        // not highlighting it: it makes a reviewer distrust the colours on the files that matter.
        Assert.Equal(MonacoLanguages.PlainText, MonacoLanguages.For("weird/thing.qqq"));
        Assert.Equal(MonacoLanguages.PlainText, MonacoLanguages.For(string.Empty));
    }

    [Fact]
    public void AnAddedFileIsOneHunkOverTheWholeThing()
    {
        var hunks = TextDiff.Hunks(string.Empty, "one\ntwo\nthree\n");

        var hunk = Assert.Single(hunks);
        Assert.Equal("hunk-0", hunk.Id);
        Assert.Equal(0, hunk.OriginalStartLine);
        Assert.Equal(1, hunk.ModifiedStartLine);
    }

    [Fact]
    public void TwoSeparatedEditsAreTwoHunksSoPaneTwoCanPointAtEither()
    {
        // Section 12: "clicking a file-write event in pane 2 opens pane 3 at that hunk". That needs a
        // stable index per contiguous run, which is the whole reason this list exists.
        var original = string.Join('\n', Enumerable.Range(1, 40).Select(line => $"line {line}"));
        var modified = original
            .Replace("line 3\n", "line 3 changed\n", StringComparison.Ordinal)
            .Replace("line 30\n", "line 30 changed\n", StringComparison.Ordinal);

        var hunks = TextDiff.Hunks(original, modified);

        Assert.Equal(2, hunks.Count);
        Assert.Equal(["hunk-0", "hunk-1"], hunks.Select(hunk => hunk.Id));
        Assert.Equal(3, hunks[0].ModifiedStartLine);
        Assert.Equal(30, hunks[1].ModifiedStartLine);
        Assert.StartsWith("@@ -", hunks[0].Header, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnchangedFileHasNoHunksAtAll()
        => Assert.Empty(TextDiff.Hunks("same\ntext\n", "same\ntext\n"));

    [Fact]
    public void AVeryLargeFileIsOneHunkRatherThanASlowAnswer()
    {
        var original = string.Join('\n', Enumerable.Range(1, TextDiff.MaxLinesForLcs + 10).Select(line => $"{line}"));
        var modified = original + "\nextra";

        // Above the ceiling the quadratic table is not worth the memory, and nobody diffing a
        // forty-thousand-line generated file is looking for the third hunk.
        Assert.Single(TextDiff.Hunks(original, modified));
    }

    [Fact]
    public void LineCountsComeOutOfTheSameWalkAsTheHunks()
    {
        var (additions, deletions) = TextDiff.Counts("a\nb\nc\n", "a\nB\nc\nd\n");

        Assert.Equal(2, additions);
        Assert.Equal(1, deletions);
    }

    [Fact]
    public void ABinaryFileSaysSoAndCarriesNoText()
    {
        // A PNG rendered as a string is a screenful of replacement characters that looks like a
        // corrupted file. The pane says there is nothing to show instead.
        var classified = GitHubRepositoryFileText.Classify("PNG\0binary");

        Assert.True(classified.Binary);
        Assert.False(classified.Truncated);
        Assert.Empty(classified.Text);

        var diff = FileDiffService.Compose("assets/logo.png", FileText.Absent, classified);

        Assert.True(diff.Binary);
        Assert.Empty(diff.ModifiedText);
        Assert.Empty(diff.Hunks);
    }

    [Fact]
    public void AnOversizedFileIsMarkedTruncatedRatherThanQuietlyCut()
    {
        var huge = string.Join('\n', Enumerable.Range(0, 60_000).Select(line => $"line {line} of a generated file"));
        var classified = GitHubRepositoryFileText.Classify(huge);

        Assert.True(classified.Truncated);
        Assert.False(classified.Binary);
        Assert.True(classified.Text.Length < huge.Length);

        var diff = FileDiffService.Compose("dist/bundle.js", FileText.Absent, classified);

        Assert.True(diff.Truncated);

        // No hunks over a prefix: they would point at line numbers the reader cannot reach.
        Assert.Empty(diff.Hunks);
    }

    [Fact]
    public void AnOrdinaryFileIsNeitherBinaryNorTruncated()
    {
        var classified = GitHubRepositoryFileText.Classify("namespace Quotes;\n\npublic class Wizard;\n");

        Assert.False(classified.Binary);
        Assert.False(classified.Truncated);
    }

    [Fact]
    public void AFileThatDidNotExistBeforeComesBackWithAnEmptyOriginal()
    {
        var diff = FileDiffService.Compose(
            "src/Quotes/UserQuotePreference.cs",
            before: null,
            after: new FileText("namespace Quotes;\n\npublic sealed class UserQuotePreference;\n"));

        Assert.Empty(diff.OriginalText);
        Assert.NotEmpty(diff.ModifiedText);
        Assert.Equal("csharp", diff.Language);
        Assert.NotEmpty(diff.Hunks);
    }

    [Fact]
    public async Task TheDiffBodyCarriesEveryKeyTheClientReadsAndNoNulls()
    {
        var body = await ApiPayloads.RenderAsync(FileDiffService.Compose(
            "src/Quotes/QuoteWizard.cs",
            new FileText("one\n"),
            new FileText("one\ntwo\n")));

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        foreach (var required in new[] { "path", "language", "originalText", "modifiedText", "hunks", "binary", "truncated" })
        {
            Assert.True(root.TryGetProperty(required, out _), $"`{required}` is part of FileDiff.");
        }

        // Both flags are always written, because the client reads them as booleans rather than
        // testing for presence — the opposite of section 7.4's fields, and deliberately so.
        Assert.False(root.GetProperty("binary").GetBoolean());
        Assert.False(root.GetProperty("truncated").GetBoolean());
    }
}
