using System.Globalization;
using Charter.Api.Contracts;

namespace Charter.Api.Changes;

/// <summary>One file's before and after, as Charter read them.</summary>
/// <param name="Text">The content, or empty when there is none to show.</param>
/// <param name="Binary">No text to show at all.</param>
/// <param name="Truncated"><see cref="Text"/> is a prefix of a larger file.</param>
public sealed record FileText(string Text, bool Binary = false, bool Truncated = false)
{
    /// <summary>The file did not exist at this revision — added, or deleted.</summary>
    public static FileText Absent { get; } = new(string.Empty);
}

/// <summary>
/// A line diff, only as clever as pane 3 needs.
/// </summary>
/// <remarks>
/// <para>
/// Monaco computes and renders the diff itself from the two texts; what Charter has to produce is
/// the <em>hunk list</em>, because section 12 wants clicking a file-write event in pane 2 to open
/// pane 3 at a hunk, and that needs a stable index and a header per contiguous run of changes.
/// </para>
/// <para>
/// The algorithm is a longest-common-subsequence over lines, bounded: above
/// <see cref="MaxLinesForLcs"/> lines the quadratic table is not worth the memory, and the honest
/// fallback is one hunk covering the file rather than a slow answer or a wrong one. Anybody diffing
/// a forty-thousand-line generated file is not looking for the third hunk.
/// </para>
/// </remarks>
public static class TextDiff
{
    /// <summary>Above this many lines on either side, the whole file is reported as one hunk.</summary>
    public const int MaxLinesForLcs = 4_000;

    /// <summary>Unchanged lines kept either side of a run of changes, as a unified diff does.</summary>
    public const int Context = 3;

    /// <summary>The hunks between two texts, in file order.</summary>
    public static IReadOnlyList<DiffHunkResponse> Hunks(string original, string modified)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(modified);

        if (string.Equals(original, modified, StringComparison.Ordinal))
        {
            return [];
        }

        var left = Lines(original);
        var right = Lines(modified);

        if (left.Length == 0 && right.Length == 0)
        {
            return [];
        }

        if (left.Length > MaxLinesForLcs || right.Length > MaxLinesForLcs)
        {
            return [WholeFile(left.Length, right.Length)];
        }

        var runs = Runs(left, right);
        var hunks = new List<DiffHunkResponse>(runs.Count);

        for (var index = 0; index < runs.Count; index++)
        {
            var run = runs[index];

            var originalStart = Math.Max(1, run.OriginalStart + 1 - Context);
            var modifiedStart = Math.Max(1, run.ModifiedStart + 1 - Context);
            var originalCount = Math.Min(left.Length - originalStart + 1, run.OriginalLength + (2 * Context));
            var modifiedCount = Math.Min(right.Length - modifiedStart + 1, run.ModifiedLength + (2 * Context));

            hunks.Add(new DiffHunkResponse
            {
                Id = string.Create(CultureInfo.InvariantCulture, $"hunk-{index}"),
                Header = Header(originalStart, Math.Max(0, originalCount), modifiedStart, Math.Max(0, modifiedCount)),

                // Section 12: pane 3 reveals this line when the hunk is selected, so it points at the
                // first changed line rather than at the context above it.
                OriginalStartLine = run.OriginalLength == 0 ? Math.Max(0, run.OriginalStart) : run.OriginalStart + 1,
                ModifiedStartLine = run.ModifiedLength == 0 ? Math.Max(0, run.ModifiedStart) : run.ModifiedStart + 1,
            });
        }

        return hunks;
    }

    /// <summary>Lines added and removed, for the changed-file list of section 14.</summary>
    public static (int Additions, int Deletions) Counts(string original, string modified)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(modified);

        var left = Lines(original);
        var right = Lines(modified);

        if (left.Length > MaxLinesForLcs || right.Length > MaxLinesForLcs)
        {
            // Counting honestly here would mean the table this method just declined to build.
            return (right.Length, left.Length);
        }

        var additions = 0;
        var deletions = 0;

        foreach (var run in Runs(left, right))
        {
            additions += run.ModifiedLength;
            deletions += run.OriginalLength;
        }

        return (additions, deletions);
    }

    private static string[] Lines(string text)
        => text.Length == 0 ? [] : text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static DiffHunkResponse WholeFile(int originalLines, int modifiedLines) => new()
    {
        Id = "hunk-0",
        Header = Header(originalLines == 0 ? 0 : 1, originalLines, modifiedLines == 0 ? 0 : 1, modifiedLines),
        OriginalStartLine = originalLines == 0 ? 0 : 1,
        ModifiedStartLine = modifiedLines == 0 ? 0 : 1,
    };

    private static string Header(int originalStart, int originalCount, int modifiedStart, int modifiedCount)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"@@ -{originalStart},{originalCount} +{modifiedStart},{modifiedCount} @@");

    private sealed record Run(int OriginalStart, int OriginalLength, int ModifiedStart, int ModifiedLength);

    /// <summary>Contiguous runs of difference, found by walking the LCS table back.</summary>
    private static List<Run> Runs(string[] left, string[] right)
    {
        var table = Table(left, right);
        var runs = new List<Run>();

        var i = 0;
        var j = 0;

        while (i < left.Length || j < right.Length)
        {
            if (i < left.Length && j < right.Length && string.Equals(left[i], right[j], StringComparison.Ordinal))
            {
                i++;
                j++;
                continue;
            }

            var originalStart = i;
            var modifiedStart = j;

            while (i < left.Length || j < right.Length)
            {
                if (i < left.Length && j < right.Length && string.Equals(left[i], right[j], StringComparison.Ordinal))
                {
                    break;
                }

                // The table says which side to advance: taking from whichever direction keeps the
                // longer common subsequence is what makes a moved line read as one change, not two.
                if (j < right.Length && (i == left.Length || table[i][j + 1] >= table[i + 1][j]))
                {
                    j++;
                }
                else
                {
                    i++;
                }
            }

            runs.Add(new Run(originalStart, i - originalStart, modifiedStart, j - modifiedStart));
        }

        return runs;
    }

    private static int[][] Table(string[] left, string[] right)
    {
        var table = new int[left.Length + 1][];
        for (var i = 0; i <= left.Length; i++)
        {
            table[i] = new int[right.Length + 1];
        }

        for (var i = left.Length - 1; i >= 0; i--)
        {
            for (var j = right.Length - 1; j >= 0; j--)
            {
                table[i][j] = string.Equals(left[i], right[j], StringComparison.Ordinal)
                    ? table[i + 1][j + 1] + 1
                    : Math.Max(table[i + 1][j], table[i][j + 1]);
            }
        }

        return table;
    }
}
