using System.Security.Cryptography;
using System.Text;

namespace Charter.Refinement;

/// <summary>The files and directories a spec expects to touch (section 10b, <c>scope { files, paths }</c>).</summary>
public sealed record SpecScope
{
    /// <summary>An empty scope. A spec that names nothing touches nothing.</summary>
    public static SpecScope Empty { get; } = new();

    /// <summary>Individual files the change is expected to touch.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>Directories or globs the change is expected to stay inside.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>Every path named by the scope, files and directories together.</summary>
    public IEnumerable<string> All => Files.Concat(Paths);

    /// <summary>Whether the scope names nothing at all.</summary>
    public bool IsEmpty => Files.Count == 0 && Paths.Count == 0;

    /// <summary>Builds a scope, trimming and dropping blanks.</summary>
    public static SpecScope Of(IEnumerable<string>? files, IEnumerable<string>? paths) => new()
    {
        Files = SpecText.CleanList(files),
        Paths = SpecText.CleanList(paths),
    };
}

/// <summary>
/// The structured specification of section 10b — <strong>the single source of truth</strong>.
/// </summary>
/// <remarks>
/// <para>
/// There are two renderings and no second copy. <see cref="ForRequester"/> and
/// <see cref="ForEngineer"/> return <em>projections</em> over this instance: they store no text of
/// their own, they read every field through a reference to this document, and they hand back the
/// very same <see cref="AcceptanceCriteria"/> list instance. There is therefore no place for a
/// requester-facing copy to live, and so no way for the two views to drift. If they could drift,
/// <em>"the spec said X"</em> would stop meaning anything and the accountability model of section 10
/// would collapse.
/// </para>
/// <para>
/// The document is immutable and its constructor is private. Edits go through the <c>With*</c>
/// methods, each of which returns a new document — which every projection then reflects, because a
/// projection has nothing cached to go stale. <see cref="ContentHash"/> is likewise computed on
/// demand rather than stored, so a copied document can never carry a hash belonging to its
/// predecessor.
/// </para>
/// </remarks>
public sealed record SpecDocument
{
    private SpecDocument()
    {
    }

    /// <summary>A short, plain-language name for the change.</summary>
    public string Title { get; private init; } = string.Empty;

    /// <summary>Plain language: what the requester will see change.</summary>
    public string Outcome { get; private init; } = string.Empty;

    /// <summary>
    /// The contract. Authored in plain language and shared <em>verbatim</em> by both renderings —
    /// both views return this exact list instance, not a copy of it.
    /// </summary>
    public IReadOnlyList<string> AcceptanceCriteria { get; private init; } = [];

    /// <summary>Engineer-facing. Never reachable from the requester projection.</summary>
    public string? TechnicalApproach { get; private init; }

    /// <summary>Where the change is expected to land. Checked against the repo's deny list.</summary>
    public SpecScope Scope { get; private init; } = SpecScope.Empty;

    /// <summary>Engineer-facing risks.</summary>
    public IReadOnlyList<string> Risks { get; private init; } = [];

    /// <summary>
    /// Anything still unresolved. A document with any of these is by definition not ready, and
    /// <see cref="SpecConfirmationCard"/> refuses to confirm it (section 10).
    /// </summary>
    public IReadOnlyList<string> OpenQuestions { get; private init; } = [];

    /// <summary>Whether anything is still open.</summary>
    public bool HasOpenQuestions => OpenQuestions.Count > 0;

    private const char Separator = '\u001f';

    /// <summary>
    /// A stable fingerprint of every field, recomputed on every access. A rendering that carries a
    /// hash no longer matching its source is a rendering that was stored rather than projected.
    /// </summary>
    public string ContentHash
    {
        get
        {
            var canonical = new StringBuilder()
                .Append("title").Append(Title).Append(Separator)
                .Append("outcome").Append(Outcome).Append(Separator)
                .Append("criteria").AppendJoin(Separator, AcceptanceCriteria).Append(Separator)
                .Append("approach").Append(TechnicalApproach ?? string.Empty).Append(Separator)
                .Append("files").AppendJoin(Separator, Scope.Files).Append(Separator)
                .Append("paths").AppendJoin(Separator, Scope.Paths).Append(Separator)
                .Append("risks").AppendJoin(Separator, Risks).Append(Separator)
                .Append("open").AppendJoin(Separator, OpenQuestions)
                .ToString();

            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        }
    }

    /// <summary>
    /// Builds a spec. Title, outcome and at least one acceptance criterion are mandatory: a document
    /// missing any of them is not something a requester could meaningfully approve.
    /// </summary>
    /// <exception cref="ArgumentException">A mandatory field is missing.</exception>
    public static SpecDocument Create(
        string title,
        string outcome,
        IEnumerable<string> acceptanceCriteria,
        string? technicalApproach = null,
        SpecScope? scope = null,
        IEnumerable<string>? risks = null,
        IEnumerable<string>? openQuestions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        ArgumentNullException.ThrowIfNull(acceptanceCriteria);

        var criteria = SpecText.CleanList(acceptanceCriteria);
        if (criteria.Count == 0)
        {
            throw new ArgumentException(
                "A spec must carry at least one acceptance criterion — it is the contract the "
                + "requester approves.",
                nameof(acceptanceCriteria));
        }

        return new SpecDocument
        {
            Title = title.Trim(),
            Outcome = outcome.Trim(),
            AcceptanceCriteria = criteria,
            TechnicalApproach = SpecText.CleanOrNull(technicalApproach),
            Scope = scope ?? SpecScope.Empty,
            Risks = SpecText.CleanList(risks),
            OpenQuestions = SpecText.CleanList(openQuestions),
        };
    }

    /// <summary>The requester rendering: title, outcome, acceptance criteria. Nothing else.</summary>
    public RequesterSpecView ForRequester() => new(this);

    /// <summary>The engineer rendering: everything.</summary>
    public EngineerSpecView ForEngineer() => new(this);

    /// <summary>Returns a copy with a new title.</summary>
    public SpecDocument WithTitle(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return this with { Title = title.Trim() };
    }

    /// <summary>Returns a copy with a new outcome.</summary>
    public SpecDocument WithOutcome(string outcome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        return this with { Outcome = outcome.Trim() };
    }

    /// <summary>Returns a copy with new acceptance criteria. Both views follow automatically.</summary>
    /// <exception cref="ArgumentException">The replacement list is empty.</exception>
    public SpecDocument WithAcceptanceCriteria(IEnumerable<string> criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        var cleaned = SpecText.CleanList(criteria);
        if (cleaned.Count == 0)
        {
            throw new ArgumentException(
                "A spec must carry at least one acceptance criterion.",
                nameof(criteria));
        }

        return this with { AcceptanceCriteria = cleaned };
    }

    /// <summary>Returns a copy with a new technical approach.</summary>
    public SpecDocument WithTechnicalApproach(string? technicalApproach) =>
        this with { TechnicalApproach = SpecText.CleanOrNull(technicalApproach) };

    /// <summary>Returns a copy with a new scope.</summary>
    public SpecDocument WithScope(SpecScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return this with { Scope = scope };
    }

    /// <summary>Returns a copy with new risks.</summary>
    public SpecDocument WithRisks(IEnumerable<string>? risks) =>
        this with { Risks = SpecText.CleanList(risks) };

    /// <summary>Returns a copy with new open questions.</summary>
    public SpecDocument WithOpenQuestions(IEnumerable<string>? openQuestions) =>
        this with { OpenQuestions = SpecText.CleanList(openQuestions) };

    /// <summary>Two documents are the same document when every field matches.</summary>
    public bool Equals(SpecDocument? other) =>
        other is not null
        && string.Equals(ContentHash, other.ContentHash, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => ContentHash.GetHashCode(StringComparison.Ordinal);
}

/// <summary>Trimming helpers shared by the spec types.</summary>
internal static class SpecText
{
    public static IReadOnlyList<string> CleanList(IEnumerable<string>? values) =>
        values is null
            ? []
            : [.. values
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())];

    public static string? CleanOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
