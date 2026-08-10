using System.Text;
using System.Text.RegularExpressions;

namespace Charter.Teaching;

/// <summary>What the tone guard removed.</summary>
/// <param name="Text">The text with course-ware furniture removed.</param>
/// <param name="Removed">How many lines were removed.</param>
/// <param name="Examples">The removed lines, for the log and for tests.</param>
public sealed record TeachingToneScrub(string Text, int Removed, IReadOnlyList<string> Examples);

/// <summary>
/// Section 13's trap, enforced: <strong>no quizzes, no progress bars, no streaks.</strong>
/// </summary>
/// <remarks>
/// <para>
/// The moment teaching feels like corporate training, adoption dies — and it dies quietly, because
/// nobody files a complaint about a tab they stopped opening. A model asked to teach will reach for
/// the shapes it has seen in teaching material: a comprehension question at the end, a completion
/// percentage, a note about how many sessions in a row you have read. Every one of those turns a
/// colleague into a trainee.
/// </para>
/// <para>
/// Stated in the prompt, then removed here regardless, for the same reason the recap's verdict guard
/// exists: an instruction is a request and a filter is a guarantee.
/// </para>
/// </remarks>
public static partial class TeachingToneGuard
{
    private static readonly Regex[] Patterns =
    [
        QuizHeading(),
        NumberedQuestion(),
        Progress(),
        Streak(),
        Gamification(),
    ];

    /// <summary>Removes course-ware furniture, line by line.</summary>
    public static TeachingToneScrub Scrub(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TeachingToneScrub(string.Empty, 0, []);
        }

        var removed = new List<string>();
        var builder = new StringBuilder();

        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.Trim().Length > 0 && Patterns.Any(pattern => pattern.IsMatch(line)))
            {
                removed.Add(line.Trim());
                continue;
            }

            builder.Append(line).Append('\n');
        }

        return new TeachingToneScrub(builder.ToString().TrimEnd('\n'), removed.Count, removed);
    }

    /// <summary>The prompt text that states the rule. Asserted in the tests so it cannot be dropped.</summary>
    public static string PromptRule =>
        """
        This is not a course and the reader is not a student.

        - No quizzes, no comprehension questions, no "check your understanding", no exercises.
        - No progress bars, percentages complete, levels, streaks, badges, points or scores.
        - Never congratulate the reader on reading, and never tell them what to read next.
        - Never describe the reader's ability, and never use words like beginner, novice or
          non-technical about them. They asked for a level of detail; that is all you know.
        """;

    [GeneratedRegex(
        @"^\s*(?:[-*#>\d.)\s]*)?\**\s*(quiz|exercise|practice|check\s+your\s+understanding|test\s+your\s+(knowledge|understanding)|review\s+questions?|knowledge\s+check)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuizHeading();

    [GeneratedRegex(
        @"^\s*(?:[-*]\s*)?\**\s*question\s*\d+\b|^\s*\d+\.\s*(?:what|which|why|how)\b[^.]*\?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberedQuestion();

    [GeneratedRegex(
        @"\bprogress\s*(bar|:|\s+\d)|\b\d{1,3}\s*%\s*(complete|through|done)\b|\b(lesson|step|module)\s+\d+\s+of\s+\d+\b|\byou(?:'ve| have)\s+completed\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Progress();

    [GeneratedRegex(
        @"\b(streak|day\s+\d+\s+in\s+a\s+row|keep\s+it\s+up|you(?:'re| are)\s+on\s+a\s+roll)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Streak();

    [GeneratedRegex(
        @"\b(badge|badges|points\s+earned|you(?:'ve| have)\s+earned|level\s+up|levelled\s+up|leaderboard|xp\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Gamification();
}
