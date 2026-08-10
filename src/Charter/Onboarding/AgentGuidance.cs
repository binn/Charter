using System.Text;

namespace Charter.Onboarding;

/// <summary>The existing agent-guidance files recon found in the target repository.</summary>
/// <param name="ClaudeMarkdown">The repository's <c>CLAUDE.md</c>, if it has one.</param>
/// <param name="AgentsMarkdown">The repository's <c>AGENTS.md</c>, if it has one.</param>
public sealed record ExistingAgentGuidance(string? ClaudeMarkdown, string? AgentsMarkdown)
{
    /// <summary>Neither file exists.</summary>
    public static ExistingAgentGuidance None { get; } = new(null, null);

    /// <summary>Whether the repository already tells agents how to behave.</summary>
    public bool Any => !string.IsNullOrWhiteSpace(ClaudeMarkdown) || !string.IsNullOrWhiteSpace(AgentsMarkdown);

    /// <summary>The names of the files that exist, for the pull request body.</summary>
    public IReadOnlyList<string> FileNames
    {
        get
        {
            var names = new List<string>(2);

            if (!string.IsNullOrWhiteSpace(ClaudeMarkdown))
            {
                names.Add("CLAUDE.md");
            }

            if (!string.IsNullOrWhiteSpace(AgentsMarkdown))
            {
                names.Add("AGENTS.md");
            }

            return names;
        }
    }
}

/// <summary>
/// Section 9, step 2: <em>if <c>CLAUDE.md</c> or <c>AGENTS.md</c> exists, import and extend — never
/// overwrite.</em>
/// </summary>
/// <remarks>
/// <para>
/// "Never overwrite" is taken literally here, and in two ways at once.
/// </para>
/// <para>
/// First, the onboarding pull request does not touch <c>CLAUDE.md</c> or <c>AGENTS.md</c> at all.
/// Those files are the repository's own instructions to its own tooling; a tool that rewrites them on
/// the way in has replaced the team's conventions with its own on day one, which is precisely the
/// adoption failure section 9 is written to avoid.
/// </para>
/// <para>
/// Second, what Charter <em>does</em> write — <c>.charter/conventions.md</c> — layers on the existing
/// file rather than duplicating it (section 8). It points at it, states what Charter adds on top, and
/// carries none of its content. Copying the rules would fork them, and a forked rule set diverges the
/// first time somebody edits one copy.
/// </para>
/// <para>
/// <see cref="ExtendInPlace"/> exists for the case where an operator genuinely wants a Charter
/// section inside an existing file. It appends, or replaces only its own previously-appended section,
/// and asserts that everything the file said before is still there afterwards.
/// </para>
/// </remarks>
public static class AgentGuidance
{
    /// <summary>The heading Charter owns inside a file it did not write.</summary>
    public const string SectionHeading = "## Charter";

    /// <summary>The marker that makes Charter's own section findable on a second pass.</summary>
    public const string SectionMarker = "<!-- charter:begin -->";

    private const string SectionEndMarker = "<!-- charter:end -->";

    /// <summary>
    /// Drafts <c>.charter/conventions.md</c>, importing whatever guidance the repository already has
    /// by reference rather than by copy.
    /// </summary>
    /// <param name="existing">What recon found.</param>
    /// <param name="charterNotes">
    /// What recon learned that the existing files do not already say — detected stack, how to run the
    /// tests, anything an agent would otherwise have to rediscover.
    /// </param>
    public static string DraftConventions(ExistingAgentGuidance existing, string? charterNotes = null)
    {
        ArgumentNullException.ThrowIfNull(existing);

        var builder = new StringBuilder();

        builder.AppendLine("# Conventions for Charter sessions");
        builder.AppendLine();

        if (existing.Any)
        {
            var names = existing.FileNames;
            var list = names.Count == 1 ? $"`{names[0]}`" : $"`{names[0]}` and `{names[1]}`";

            builder.Append("This repository already has ").Append(list).AppendLine(". Those files are the");
            builder.AppendLine("authority and Charter reads them as written — this file does not repeat them, and");
            builder.AppendLine("Charter does not edit them. What follows is what Charter adds on top.");
        }
        else
        {
            builder.AppendLine("This repository has no `CLAUDE.md` or `AGENTS.md`, so this file is the only agent");
            builder.AppendLine("guidance there is. If you add one later, move the durable rules there and leave");
            builder.AppendLine("only the Charter-specific ones here.");
        }

        builder.AppendLine();
        builder.AppendLine("## What Charter adds");
        builder.AppendLine();
        builder.AppendLine("- Work only inside the paths `scopes.allow` names in `.charter/config.yml`. The");
        builder.AppendLine("  runner enforces this; treating it as advice wastes a session.");
        builder.AppendLine("- Every check in `.charter/config.yml` must pass before the work is finished.");
        builder.AppendLine("- Charter cannot merge. Open the pull request and stop; a human decides the rest.");
        builder.AppendLine("- A destructive migration halts the session rather than being written (section 15).");

        if (!string.IsNullOrWhiteSpace(charterNotes))
        {
            builder.AppendLine();
            builder.AppendLine("## Notes from recon");
            builder.AppendLine();
            builder.AppendLine(charterNotes.Trim());
        }

        return builder.ToString();
    }

    /// <summary>
    /// Adds or refreshes Charter's own section inside an existing file, preserving everything else.
    /// </summary>
    /// <remarks>
    /// The original text is never rewritten: on a first pass the section is appended, and on a later
    /// pass only the region between Charter's own markers is replaced. A file with no markers is only
    /// ever grown.
    /// </remarks>
    public static string ExtendInPlace(string? existing, string charterSection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(charterSection);

        var section = BuildSection(charterSection);

        if (string.IsNullOrWhiteSpace(existing))
        {
            return section;
        }

        var start = existing.IndexOf(SectionMarker, StringComparison.Ordinal);

        if (start < 0)
        {
            return existing.TrimEnd() + "\n\n" + section;
        }

        var end = existing.IndexOf(SectionEndMarker, start, StringComparison.Ordinal);

        return end < 0
            ? existing[..start].TrimEnd() + "\n\n" + section
            : existing[..start].TrimEnd() + "\n\n" + section + existing[(end + SectionEndMarker.Length)..].TrimEnd();
    }

    /// <summary>
    /// Whether <paramref name="updated"/> still contains everything <paramref name="original"/> said
    /// outside Charter's own section.
    /// </summary>
    /// <remarks>
    /// The guard behind "never overwrite". Cheap enough to assert on every write, and it catches the
    /// one failure that would matter: an extend that turned into a replace.
    /// </remarks>
    public static bool PreservesOriginal(string? original, string updated)
    {
        ArgumentNullException.ThrowIfNull(updated);

        if (string.IsNullOrWhiteSpace(original))
        {
            return true;
        }

        var before = StripCharterSection(original);

        return before
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(line => updated.Contains(line, StringComparison.Ordinal));
    }

    private static string BuildSection(string body)
    {
        var builder = new StringBuilder();

        builder.AppendLine(SectionMarker);
        builder.AppendLine(SectionHeading);
        builder.AppendLine();
        builder.AppendLine(body.Trim());
        builder.AppendLine();
        builder.Append(SectionEndMarker);

        return builder.ToString();
    }

    private static string StripCharterSection(string text)
    {
        var start = text.IndexOf(SectionMarker, StringComparison.Ordinal);

        if (start < 0)
        {
            return text;
        }

        var end = text.IndexOf(SectionEndMarker, start, StringComparison.Ordinal);

        return end < 0 ? text[..start] : text[..start] + text[(end + SectionEndMarker.Length)..];
    }
}
