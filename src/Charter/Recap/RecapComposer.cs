using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Charter.Refinement;

namespace Charter.Recaps;

/// <summary>One row of the risk-ranked file list, as it is stored in <c>Recap.risk_items</c>.</summary>
public sealed record RecapRiskItem
{
    /// <summary>Repository-relative path.</summary>
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    /// <summary>The computed risk score.</summary>
    [JsonPropertyName("score")]
    public required int Score { get; init; }

    /// <summary>The band the score falls in.</summary>
    [JsonPropertyName("band")]
    public required string Band { get; init; }

    /// <summary>Why it scored that way.</summary>
    [JsonPropertyName("reasons")]
    public IReadOnlyList<string> Reasons { get; init; } = [];

    /// <summary>The model's factual note on the file, where it gave one.</summary>
    [JsonPropertyName("note")]
    public string? Note { get; init; }

    /// <summary>Position in the suggested review order, 1-based, or null if beyond the cut.</summary>
    [JsonPropertyName("review_order")]
    public int? ReviewOrder { get; init; }
}

/// <summary>
/// Assembles the recap body. Section 14's five sections, in section 14's order, every time.
/// </summary>
/// <remarks>
/// Charter owns the structure rather than asking the model for it. A model asked for five headings
/// gives four on a bad day, and the section reviewers most often skip — deviations — is the one that
/// goes missing. Here the heading is always present; what varies is what sits underneath it.
/// </remarks>
public static class RecapComposer
{
    private static readonly JsonSerializerOptions RiskItemJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The sentence an auto-dispatched session leads with (section 7.5).</summary>
    public const string UnreviewedSpecLead =
        "> **No human approved this specification before the build.** This session was "
        + "auto-dispatched, so you are the first person to read what the agent was asked to do. The "
        + "specification is reproduced in full below rather than summarised.";

    /// <summary>Builds the markdown body and the risk items together, so they cannot disagree.</summary>
    public static (string BodyMarkdown, string RiskItemsJson, int VerdictsRemoved) Compose(
        RecapEvidence evidence,
        IReadOnlyList<RecapRankedFile> rankedFiles,
        RecapPayload payload,
        RecapOptions options)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(rankedFiles);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(options);

        var removed = 0;
        var notes = BuildNotes(payload, ref removed);

        var whatAndWhy = Scrub(payload.WhatAndWhy, ref removed);
        var deviations = RenderDeviations(payload, ref removed);
        var unverified = ScrubAll(payload.CouldNotVerify, ref removed);

        var reviewOrder = rankedFiles.Take(Math.Max(1, options.ReviewOrderLength)).ToList();
        var builder = new StringBuilder();

        builder.AppendLine("## Session recap");
        builder.AppendLine();

        if (evidence.AutoDispatched)
        {
            // Section 7.5: lead with it. Charter-authored, so the verdict guard never touches it and
            // it cannot be softened by whatever the model decided to write.
            builder.AppendLine(UnreviewedSpecLead);
            builder.AppendLine();
            builder.AppendLine("### The specification, in full");
            builder.AppendLine();
            builder.AppendLine(RecapPromptBuilder.RenderSpec(evidence.Spec));
            builder.AppendLine();
        }

        builder.AppendLine("### 1. What changed, and why");
        builder.AppendLine();
        builder.AppendLine(
            whatAndWhy.Length > 0
                ? whatAndWhy
                : "The recap could not establish what this session set out to do from the transcript.");
        builder.AppendLine();

        builder.AppendLine("### 2. Where this deviated from the specification");
        builder.AppendLine();
        if (deviations.Count == 0)
        {
            builder.AppendLine(
                "Nothing in the transcript shows the agent departing from the specification. That "
                + "is a statement about the transcript, not about the change — a deviation the "
                + "agent never mentioned would not appear here.");
        }
        else
        {
            foreach (var deviation in deviations)
            {
                builder.Append("- ").AppendLine(deviation);
            }
        }

        builder.AppendLine();

        builder.AppendLine("### 3. Files, ranked by risk");
        builder.AppendLine();
        if (rankedFiles.Count == 0)
        {
            builder.AppendLine("No file changes were recorded for this session.");
        }
        else
        {
            builder.AppendLine("Ordered by risk, not alphabetically or by directory.");
            builder.AppendLine();
            foreach (var file in rankedFiles)
            {
                builder.Append("- `").Append(file.Path).Append("` — **")
                    .Append(RecapFileRiskRanker.DescribeBand(file.Band)).Append("**");

                if (file.Reasons.Count > 0)
                {
                    builder.Append(": ").AppendJoin("; ", file.Reasons);
                }

                if (notes.TryGetValue(GlobPattern.Normalise(file.Path), out var note) && note.Length > 0)
                {
                    builder.Append(". ").Append(note);
                }

                builder.AppendLine();
            }
        }

        builder.AppendLine();

        builder.AppendLine("### 4. What could not be verified");
        builder.AppendLine();
        if (unverified.Count == 0)
        {
            builder.AppendLine(
                "The session recorded nothing it left unverified. Read that as the absence of a "
                + "note, not as evidence that everything was checked.");
        }
        else
        {
            foreach (var item in unverified)
            {
                builder.Append("- ").AppendLine(item);
            }
        }

        builder.AppendLine();

        builder.AppendLine("### 5. Suggested review order");
        builder.AppendLine();
        if (reviewOrder.Count == 0 || rankedFiles.Count == 0)
        {
            builder.AppendLine("There is nothing to order — no file changes were recorded.");
        }
        else
        {
            builder.AppendLine("Start where the risk is.");
            builder.AppendLine();
            for (var index = 0; index < reviewOrder.Count; index++)
            {
                var file = reviewOrder[index];
                builder.Append(index + 1).Append(". `").Append(file.Path).Append('`');
                if (file.Reasons.Count > 0)
                {
                    builder.Append(" — ").Append(file.Reasons[0]);
                }

                builder.AppendLine();
            }

            if (rankedFiles.Count > reviewOrder.Count)
            {
                builder
                    .AppendLine()
                    .Append("Then the remaining ")
                    .Append(rankedFiles.Count - reviewOrder.Count)
                    .AppendLine(" file(s), in the order listed above.");
            }
        }

        builder.AppendLine();
        builder.AppendLine(RecapVerdictGuard.Disclaimer);

        var riskItems = BuildRiskItems(rankedFiles, notes, reviewOrder.Count);

        return (
            builder.ToString().TrimEnd() + "\n",
            JsonSerializer.Serialize(riskItems, RiskItemJson),
            removed);
    }

    private static Dictionary<string, string> BuildNotes(RecapPayload payload, ref int removed)
    {
        var notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var note in payload.FileNotes ?? [])
        {
            if (string.IsNullOrWhiteSpace(note.Path) || string.IsNullOrWhiteSpace(note.Note))
            {
                continue;
            }

            var scrub = RecapVerdictGuard.Scrub(note.Note);
            removed += scrub.Removed;

            var text = scrub.Text.Trim();
            if (text.Length > 0 && !string.Equals(text, RecapVerdictGuard.Redaction, StringComparison.Ordinal))
            {
                notes[GlobPattern.Normalise(note.Path)] = text;
            }
        }

        return notes;
    }

    private static IReadOnlyList<string> RenderDeviations(RecapPayload payload, ref int removed)
    {
        var rendered = new List<string>();
        foreach (var deviation in payload.Deviations ?? [])
        {
            // Each field is scrubbed before it is rendered rather than after. Removing a sentence
            // from an already-assembled line would leave the markdown around it — an unclosed bold
            // marker, a dangling "Reason given:" — and the guard's job is to be invisible.
            var what = Scrub(deviation.What, ref removed);
            if (what.Length == 0 || string.Equals(what, RecapVerdictGuard.Redaction, StringComparison.Ordinal))
            {
                continue;
            }

            var specSaid = Scrub(deviation.SpecSaid, ref removed);
            var why = Scrub(deviation.Why, ref removed);

            var builder = new StringBuilder("**").Append(what).Append("**");
            if (!string.IsNullOrWhiteSpace(deviation.Where))
            {
                builder.Append(" (").Append(deviation.Where!.Trim()).Append(')');
            }

            if (specSaid.Length > 0)
            {
                builder.Append(" The specification asked for: ").Append(specSaid);
                if (!specSaid.EndsWith('.'))
                {
                    builder.Append('.');
                }
            }

            if (why.Length > 0)
            {
                builder.Append(" Reason given: ").Append(why);
                if (!why.EndsWith('.'))
                {
                    builder.Append('.');
                }
            }

            rendered.Add(builder.ToString());
        }

        return rendered;
    }

    private static string Scrub(string? text, ref int removed)
    {
        var scrub = RecapVerdictGuard.Scrub(text);
        removed += scrub.Removed;
        return scrub.Text.Trim();
    }

    private static IReadOnlyList<string> ScrubAll(IReadOnlyList<string>? values, ref int removed)
    {
        var kept = new List<string>();
        foreach (var value in values ?? [])
        {
            var scrub = RecapVerdictGuard.Scrub(value);
            removed += scrub.Removed;

            var text = scrub.Text.Trim();
            if (text.Length > 0 && !string.Equals(text, RecapVerdictGuard.Redaction, StringComparison.Ordinal))
            {
                kept.Add(text);
            }
        }

        return kept;
    }

    private static IReadOnlyList<RecapRiskItem> BuildRiskItems(
        IReadOnlyList<RecapRankedFile> rankedFiles,
        IReadOnlyDictionary<string, string> notes,
        int reviewOrderLength)
        =>
        [
            .. rankedFiles.Select((file, index) => new RecapRiskItem
            {
                Path = file.Path,
                Score = file.Score,
                Band = file.Band.ToString().ToLowerInvariant(),
                Reasons = file.Reasons,
                Note = notes.TryGetValue(GlobPattern.Normalise(file.Path), out var note) ? note : null,
                ReviewOrder = index < reviewOrderLength ? index + 1 : null,
            }),
        ];
}
