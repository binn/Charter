namespace Charter.Refinement;

/// <summary>Who a rendering is for (section 10b).</summary>
public enum SpecAudience
{
    /// <summary>Title, outcome, acceptance criteria. Nothing else.</summary>
    Requester,

    /// <summary>Everything.</summary>
    Engineer,
}

/// <summary>
/// The requester rendering of a <see cref="SpecDocument"/> — a <em>projection</em>, never a copy.
/// </summary>
/// <remarks>
/// <para>
/// This type holds one field: a reference to the source document. Every member below reads through
/// it, so there is nothing here that could be edited independently and nothing that could go stale.
/// The engineer-facing fields are not hidden, filtered or nulled — they are simply <em>not members
/// of this type</em>, so reaching <c>technical_approach</c> through a requester view does not
/// compile.
/// </para>
/// <para>
/// The constructor is internal and takes a <see cref="SpecDocument"/>, so a requester view cannot be
/// constructed from separately-authored text. That is the whole anti-drift mechanism: there is no
/// second place for the plain-language rendering to live.
/// </para>
/// </remarks>
public sealed class RequesterSpecView
{
    private readonly SpecDocument _spec;

    internal RequesterSpecView(SpecDocument spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        _spec = spec;
    }

    /// <summary>The spec title.</summary>
    public string Title => _spec.Title;

    /// <summary>Plain language: what the requester will see change.</summary>
    public string Outcome => _spec.Outcome;

    /// <summary>
    /// The contract, verbatim. This is the <em>same list instance</em> the engineer view returns —
    /// not an equal copy — so the two renderings cannot disagree about what was agreed.
    /// </summary>
    public IReadOnlyList<string> AcceptanceCriteria => _spec.AcceptanceCriteria;

    /// <summary>The fingerprint of the document this view is projecting.</summary>
    public string SourceContentHash => _spec.ContentHash;

    /// <summary>Renders the plain-language view, generated from the structured spec on every call.</summary>
    public RenderedSpec Render() => SpecRenderer.Render(_spec, SpecAudience.Requester);
}

/// <summary>
/// The engineer rendering of a <see cref="SpecDocument"/> — the same projection mechanism as
/// <see cref="RequesterSpecView"/>, over the same instance, exposing every field.
/// </summary>
public sealed class EngineerSpecView
{
    private readonly SpecDocument _spec;

    internal EngineerSpecView(SpecDocument spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        _spec = spec;
    }

    /// <summary>The spec title.</summary>
    public string Title => _spec.Title;

    /// <summary>Plain language: what the requester will see change.</summary>
    public string Outcome => _spec.Outcome;

    /// <summary>The contract, verbatim — the same list instance the requester view returns.</summary>
    public IReadOnlyList<string> AcceptanceCriteria => _spec.AcceptanceCriteria;

    /// <summary>How the change is to be made.</summary>
    public string? TechnicalApproach => _spec.TechnicalApproach;

    /// <summary>Where the change is expected to land.</summary>
    public SpecScope Scope => _spec.Scope;

    /// <summary>Known risks.</summary>
    public IReadOnlyList<string> Risks => _spec.Risks;

    /// <summary>Anything still unresolved.</summary>
    public IReadOnlyList<string> OpenQuestions => _spec.OpenQuestions;

    /// <summary>The fingerprint of the document this view is projecting.</summary>
    public string SourceContentHash => _spec.ContentHash;

    /// <summary>The document itself, for callers that need the structure rather than the prose.</summary>
    public SpecDocument Document => _spec;

    /// <summary>Renders the engineer view, generated from the structured spec on every call.</summary>
    public RenderedSpec Render() => SpecRenderer.Render(_spec, SpecAudience.Engineer);
}

/// <summary>
/// A rendering of a spec, stamped with the fingerprint of the document it came from.
/// </summary>
/// <remarks>
/// Renderings are produced on demand and are not meant to be stored. Where one is cached anyway —
/// a status thread post, a notification email — <see cref="Matches"/> answers whether it still
/// describes the current spec, so a stale rendering is detectable rather than silently wrong.
/// </remarks>
/// <param name="Audience">Who the rendering is for.</param>
/// <param name="Markdown">The plain-language text, generated from the structured spec.</param>
/// <param name="SourceContentHash">The fingerprint of the spec it was generated from.</param>
public sealed record RenderedSpec(SpecAudience Audience, string Markdown, string SourceContentHash)
{
    /// <summary>Whether this rendering was generated from the given spec as it now stands.</summary>
    public bool Matches(SpecDocument spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return string.Equals(SourceContentHash, spec.ContentHash, StringComparison.Ordinal);
    }
}

/// <summary>
/// Generates the plain-language renderings <em>from</em> the structured spec (section 10b).
/// </summary>
/// <remarks>
/// Both renderings call <see cref="AcceptanceCriteriaBlock"/> with the same list instance, so the
/// acceptance-criteria text is byte-identical in the two views by construction rather than by
/// convention. There is no second code path that could format them differently.
/// </remarks>
public static class SpecRenderer
{
    /// <summary>The heading the requester's acceptance criteria appear under.</summary>
    public const string RequesterCriteriaHeading = "## What you'll be able to check";

    /// <summary>The heading the engineer's acceptance criteria appear under.</summary>
    public const string EngineerCriteriaHeading = "## Acceptance criteria";

    /// <summary>
    /// The one and only formatting of the acceptance criteria. Both audiences receive this exact
    /// text.
    /// </summary>
    public static string AcceptanceCriteriaBlock(IReadOnlyList<string> acceptanceCriteria)
    {
        ArgumentNullException.ThrowIfNull(acceptanceCriteria);
        return string.Join("\n", acceptanceCriteria.Select(static criterion => $"- {criterion}"));
    }

    /// <summary>Renders a spec for one audience.</summary>
    public static RenderedSpec Render(SpecDocument spec, SpecAudience audience)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var markdown = audience switch
        {
            SpecAudience.Requester => RequesterMarkdown(spec),
            SpecAudience.Engineer => EngineerMarkdown(spec),
            _ => throw new ArgumentOutOfRangeException(nameof(audience), audience, "Unknown audience."),
        };

        return new RenderedSpec(audience, markdown, spec.ContentHash);
    }

    private static string RequesterMarkdown(SpecDocument spec)
    {
        var builder = new System.Text.StringBuilder()
            .Append("# ").AppendLine(spec.Title)
            .AppendLine()
            .AppendLine(spec.Outcome)
            .AppendLine()
            .AppendLine(RequesterCriteriaHeading)
            .AppendLine()
            .AppendLine(AcceptanceCriteriaBlock(spec.AcceptanceCriteria));

        return builder.ToString().TrimEnd() + "\n";
    }

    private static string EngineerMarkdown(SpecDocument spec)
    {
        var builder = new System.Text.StringBuilder()
            .Append("# ").AppendLine(spec.Title)
            .AppendLine()
            .AppendLine("## Outcome")
            .AppendLine()
            .AppendLine(spec.Outcome)
            .AppendLine()
            .AppendLine(EngineerCriteriaHeading)
            .AppendLine()
            .AppendLine(AcceptanceCriteriaBlock(spec.AcceptanceCriteria));

        if (spec.TechnicalApproach is { Length: > 0 } approach)
        {
            builder.AppendLine()
                .AppendLine("## Technical approach")
                .AppendLine()
                .AppendLine(approach);
        }

        if (!spec.Scope.IsEmpty)
        {
            builder.AppendLine().AppendLine("## Scope").AppendLine();
            AppendPathList(builder, "Files", spec.Scope.Files);
            AppendPathList(builder, "Paths", spec.Scope.Paths);
        }

        AppendBulletSection(builder, "## Risks", spec.Risks);
        AppendBulletSection(builder, "## Open questions", spec.OpenQuestions);

        return builder.ToString().TrimEnd() + "\n";
    }

    private static void AppendPathList(
        System.Text.StringBuilder builder,
        string label,
        IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        builder.Append(label).AppendLine(":");
        foreach (var value in values)
        {
            builder.Append("- `").Append(value).AppendLine("`");
        }

        builder.AppendLine();
    }

    private static void AppendBulletSection(
        System.Text.StringBuilder builder,
        string heading,
        IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        builder.AppendLine().AppendLine(heading).AppendLine();
        foreach (var value in values)
        {
            builder.Append("- ").AppendLine(value);
        }
    }
}
