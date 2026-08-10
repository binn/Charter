using Charter.Domain;

namespace Charter.Teaching;

/// <summary>
/// The per-walkthrough override of section 13: <em>more detail</em> / <em>less detail</em>, always
/// visible, regenerating without changing the reader's default.
/// </summary>
public enum TeachingDetail
{
    /// <summary>Use the reader's own calibration.</summary>
    AsSet,

    /// <summary>One step more explanatory, for this rendering only.</summary>
    MoreDetail,

    /// <summary>One step terser, for this rendering only.</summary>
    LessDetail,
}

/// <summary>
/// Section 13's calibration: <strong>named for what the reader wants, never for what they lack.</strong>
/// </summary>
/// <remarks>
/// <para>
/// <c>explain_everything</c>, <c>skip_the_basics</c>, <c>just_the_decisions</c>. The naming is not
/// cosmetic. These labels appear in a UI the reader's colleagues can see, and labelling a colleague
/// "beginner" is the kind of thing that makes somebody stop using a tool and never say why. A
/// setting named for an appetite is one anybody can pick without it being a statement about them.
/// </para>
/// <para>
/// The three levels have to produce genuinely different documents, or the setting is decoration.
/// <c>explain_everything</c> defines vocabulary on first use; <c>skip_the_basics</c> assumes the
/// vocabulary and spends its budget on reasoning; <c>just_the_decisions</c> drops mechanics entirely
/// and reports trade-offs and alternatives. They differ in instruction and in length allowance.
/// </para>
/// </remarks>
public static class TeachingCalibration
{
    /// <summary>The stored, user-facing label. Matches the specification's own wording exactly.</summary>
    public static string Label(TeachingLevel level) => level switch
    {
        TeachingLevel.ExplainEverything => "explain_everything",
        TeachingLevel.SkipTheBasics => "skip_the_basics",
        TeachingLevel.JustTheDecisions => "just_the_decisions",
        _ => "explain_everything",
    };

    /// <summary>How the level reads in a sentence, for a picker or a heading.</summary>
    public static string Describe(TeachingLevel level) => level switch
    {
        TeachingLevel.ExplainEverything => "Explain everything — define each term the first time it comes up",
        TeachingLevel.SkipTheBasics => "Skip the basics — I know what a database and a deploy are; give me the reasoning",
        TeachingLevel.JustTheDecisions => "Just the decisions — trade-offs and alternatives, no mechanics",
        _ => "Explain everything",
    };

    /// <summary>Applies a per-walkthrough override without touching the stored default.</summary>
    public static TeachingLevel Apply(TeachingLevel level, TeachingDetail detail) => detail switch
    {
        TeachingDetail.MoreDetail => level switch
        {
            TeachingLevel.JustTheDecisions => TeachingLevel.SkipTheBasics,
            TeachingLevel.SkipTheBasics => TeachingLevel.ExplainEverything,
            _ => TeachingLevel.ExplainEverything,
        },
        TeachingDetail.LessDetail => level switch
        {
            TeachingLevel.ExplainEverything => TeachingLevel.SkipTheBasics,
            TeachingLevel.SkipTheBasics => TeachingLevel.JustTheDecisions,
            _ => TeachingLevel.JustTheDecisions,
        },
        _ => level,
    };

    /// <summary>The instruction block that makes the three levels produce different documents.</summary>
    public static string PromptText(TeachingLevel level) => level switch
    {
        TeachingLevel.ExplainEverything =>
            """
            Calibration: explain everything.

            - Assume no software vocabulary at all. The first time you use a technical term, define
              it in the same sentence, in plain words, using this project as the example.
            - Prefer concrete nouns from this project over general ones: name the actual screen, the
              actual table, the actual button.
            - Explain what a change means for the person using the product before explaining how it
              was made.
            - Never say a thing is simple, easy, or obvious.
            """,

        TeachingLevel.SkipTheBasics =>
            """
            Calibration: skip the basics.

            - The reader knows what a database, a deploy, a branch and a test are. Do not define
              them and do not explain that data is stored in tables.
            - Spend the space on reasoning instead: why this approach, what it interacts with, what
              had to change together and why.
            - Name mechanisms where they matter, but do not narrate every step the agent took.
            """,

        TeachingLevel.JustTheDecisions =>
            """
            Calibration: just the decisions.

            - Report decisions, trade-offs and alternatives only. No mechanics, no walkthrough of
              how anything works, no description of the tools used.
            - Each item: the decision, what else could have been done, and what it costs.
            - If the session made no real decisions, say so in one line rather than manufacturing
              some. A short honest answer is the correct output here.
            """,

        _ => PromptText(TeachingLevel.ExplainEverything),
    };

    /// <summary>
    /// The output allowance for a level. <c>just_the_decisions</c> is a materially shorter document,
    /// not the same document with a different preamble.
    /// </summary>
    public static int MaxOutputTokens(TeachingLevel level, TeachingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return level switch
        {
            TeachingLevel.ExplainEverything => options.MaxOutputTokens,
            TeachingLevel.SkipTheBasics => Math.Max(256, options.MaxOutputTokens * 2 / 3),
            TeachingLevel.JustTheDecisions => Math.Max(256, options.MaxOutputTokens / 3),
            _ => options.MaxOutputTokens,
        };
    }
}
