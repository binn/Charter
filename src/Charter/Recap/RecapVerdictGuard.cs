using System.Text;
using System.Text.RegularExpressions;

namespace Charter.Recaps;

/// <summary>What the guard took out.</summary>
/// <param name="Text">The text with every verdict statement removed.</param>
/// <param name="Removed">How many statements were removed.</param>
/// <param name="Examples">The removed statements, for the log and for tests. Never rendered.</param>
public sealed record RecapVerdictScrub(string Text, int Removed, IReadOnlyList<string> Examples);

/// <summary>
/// Section 14's second non-negotiable rule: <strong>the recap must never say the code looks good.</strong>
/// </summary>
/// <remarks>
/// <para>
/// It is an orientation aid, not a verdict. The moment it editorialises on quality, reviewers start
/// trusting it instead of reading — and a reviewer who skims because a machine said the change was
/// fine is worse than no recap at all.
/// </para>
/// <para>
/// This is enforced twice, deliberately. The prompt forbids it, and then <em>this</em> deletes it
/// anyway, sentence by sentence, from whatever came back. A prompt instruction is a request; models
/// are agreeable and will congratulate an author unprompted. Only the second layer is a guarantee,
/// which is why the assertion in the test suite runs against a model stub that tries to praise.
/// </para>
/// <para>
/// Note what is <em>not</em> banned: the word "approved". Section 14's first section ties the change
/// back to <em>the approved spec</em>, and section 7.5's lead says <em>no human approved this
/// specification</em>. Both are statements about process, not quality. The patterns below match
/// judgements about the code.
/// </para>
/// </remarks>
public static partial class RecapVerdictGuard
{
    /// <summary>
    /// Stands in for a section the guard emptied. Says nothing about quality either.
    /// </summary>
    public const string Redaction =
        "(Charter removed a quality judgement here. This recap reports what happened; it does not "
        + "assess the change.)";

    /// <summary>
    /// The standing footer. Charter-authored, so it is never scrubbed and never drifts.
    /// </summary>
    public const string Disclaimer =
        "_Charter wrote this to orient a review, not to perform one. It describes what the session "
        + "did and where the risk sits; whether the change is correct is yours to determine._";

    private static readonly Regex[] Verdicts =
    [
        LooksGood(),
        SeemsFine(),
        WellWritten(),
        QualityAdjective(),
        NoIssues(),
        ReadyToShip(),
        Recommendation(),
        GoodWork(),
        CorrectlyDone(),
        NoFurtherReview(),
        SubjectIsGood(),
    ];

    /// <summary>Removes every verdict statement from model-authored text.</summary>
    public static RecapVerdictScrub Scrub(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new RecapVerdictScrub(string.Empty, 0, []);
        }

        var removed = new List<string>();
        var builder = new StringBuilder();
        var lines = text.ReplaceLineEndings("\n").Split('\n');

        foreach (var line in lines)
        {
            var (prefix, body) = SplitListMarker(line);
            if (body.Length == 0)
            {
                builder.Append(line).Append('\n');
                continue;
            }

            var kept = new List<string>();
            foreach (var sentence in Sentences(body))
            {
                if (IsVerdict(sentence))
                {
                    removed.Add(sentence.Trim());
                    continue;
                }

                kept.Add(sentence);
            }

            if (kept.Count == 0)
            {
                // The whole line was a verdict. A bullet with nothing left in it is dropped; a
                // paragraph is replaced, so the section does not silently become empty.
                if (prefix.Length == 0)
                {
                    builder.Append(Redaction).Append('\n');
                }

                continue;
            }

            builder.Append(prefix).Append(string.Concat(kept).Trim()).Append('\n');
        }

        var result = builder.ToString().TrimEnd('\n');
        if (result.Trim().Length == 0 && removed.Count > 0)
        {
            result = Redaction;
        }

        return new RecapVerdictScrub(result, removed.Count, removed);
    }

    /// <summary>Whether a single statement is a judgement on the change.</summary>
    public static bool IsVerdict(string? sentence)
        => !string.IsNullOrWhiteSpace(sentence)
        && Verdicts.Any(pattern => pattern.IsMatch(sentence));

    /// <summary>The prompt text that states the rule. Asserted in the tests so it cannot be dropped.</summary>
    public static string PromptRule =>
        """
        NEVER assess quality. This is an orientation aid, not a verdict, and it is read by an
        engineer who is about to review the change themselves.

        - Never say the change looks good, looks correct, looks clean, is well written, is high
          quality, is ready to merge, is safe to ship, or that you would approve it.
        - Never say there are no issues, no concerns, or nothing to worry about. You did not run the
          code and you cannot know that.
        - Never praise, and never reassure. A reviewer who skims because you said it was fine is
          worse off than one who had no recap at all.
        - Describe what happened, what changed, what was not verified, and where the risk sits.
          Where you are uncertain, say what you could not determine rather than guessing.
        """;

    private static (string Prefix, string Body) SplitListMarker(string line)
    {
        var match = ListMarker().Match(line);
        return match.Success
            ? (match.Value, line[match.Length..])
            : (string.Empty, line);
    }

    private static IEnumerable<string> Sentences(string body)
    {
        var start = 0;
        for (var index = 0; index < body.Length; index++)
        {
            if (body[index] is not ('.' or '!' or '?' or ';'))
            {
                continue;
            }

            var end = index + 1;
            while (end < body.Length && body[end] == ' ')
            {
                end++;
            }

            yield return body[start..end];
            start = end;
            index = end - 1;
        }

        if (start < body.Length)
        {
            yield return body[start..];
        }
    }

    [GeneratedRegex(@"^\s*(?:[-*+]\s+|\d+[.)]\s+|>\s*|#{1,6}\s+)", RegexOptions.CultureInvariant)]
    private static partial Regex ListMarker();

    [GeneratedRegex(
        @"\blooks?\s+(good|great|fine|correct|clean|solid|right|reasonable|sensible|sane|safe)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LooksGood();

    [GeneratedRegex(
        @"\b(seems|appears)\s+(to\s+be\s+)?(good|fine|correct|clean|solid|reasonable|safe|sound)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeemsFine();

    [GeneratedRegex(
        @"\b(well|nicely|cleanly|neatly|sensibly)[\s-]+(written|structured|implemented|factored|designed|done|tested|documented|organised|organized)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WellWritten();

    [GeneratedRegex(
        @"\b(high|good|excellent|great|decent)[\s-]+quality\b|\bidiomatic\b|\bbest\s+practice\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QualityAdjective();

    [GeneratedRegex(
        @"\bno\s+(obvious\s+|apparent\s+|major\s+|real\s+)?(issues|concerns|problems|bugs|red\s+flags|surprises)\b|\bnothing\s+(concerning|alarming|problematic|worrying|of\s+concern|to\s+worry\s+about|stands\s+out)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoIssues();

    [GeneratedRegex(
        @"\b(ready|safe|fine|good)\s+to\s+(merge|ship|land|go|release|approve)\b|\b(ship|merge)\s+it\b|\blgtm\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReadyToShip();

    [GeneratedRegex(
        @"\bi\s*(?:'d|\s+would)?\s*(approve|recommend\s+approving|have\s+no\s+objection)\b|\brecommend\s+(approving|merging|shipping|accepting)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Recommendation();

    [GeneratedRegex(
        @"\b(solid|great|good|excellent|nice|strong|tidy|clean|elegant|neat)\s+(work|job|change|changeset|implementation|refactor|refactoring|approach|design|patch)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GoodWork();

    [GeneratedRegex(
        @"\b(correctly|properly|appropriately|sensibly)\s+(implemented|handled|done|written|applied|scoped)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CorrectlyDone();

    [GeneratedRegex(
        @"\bno\s+further\s+(review|scrutiny|attention|changes)\s+(is\s+|are\s+)?(needed|required|necessary)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoFurtherReview();

    [GeneratedRegex(
        @"\bthe\s+(code|change|changes|implementation|diff|patch|work)\s+(is|are|all\s+)?\s*(good|correct|fine|clean|solid|safe|sound)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SubjectIsGood();
}
