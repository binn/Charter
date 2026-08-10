using Charter.Domain;

namespace Charter.Refinement;

/// <summary>
/// Untrusted text as a person typed it. The type exists so that raw requester input cannot be
/// mistaken for something an agent may be told (section 16).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Refinement is a sanitisation boundary.</strong> The agent never sees raw requester text;
/// it sees the model-authored, human-approved <see cref="SpecDocument"/>. This type is the near half
/// of that boundary: requester input is not a <see cref="string"/> anywhere in Charter, it is a
/// <see cref="RequesterText"/>, and the only ways to get characters back out of it are four
/// assembly-internal methods, each named for its single legitimate consumer and each called from
/// exactly one place:
/// </para>
/// <list type="bullet">
/// <item><see cref="RevealForRefinementPrompt"/> — the refiner (<see cref="RefinementPromptBuilder"/>).</item>
/// <item><see cref="RevealForScanning"/> — the injection scanner.</item>
/// <item><see cref="RevealForPersistence"/> — writing a turn to its column.</item>
/// <item><see cref="RevealForRequesterEcho"/> — showing a person their own words back.</item>
/// </list>
/// <para>
/// Keeping that list short is the point. "Who can read untrusted text" has to stay a question
/// somebody can answer by reading one file.
/// </para>
/// <para>
/// The far half is that nothing on the dispatch path accepts one. <see cref="ApprovedSpec"/> and
/// <see cref="AgentBriefing"/> have no constructor, factory or property that takes a
/// <see cref="RequesterText"/>, a <see cref="Request"/> or a bare <see cref="string"/> — the only
/// way to obtain a briefing is from a confirmed spec. Raw text therefore has no route to a build
/// that a compiler would accept, which is what section 16 means by structural.
/// </para>
/// <para>
/// <see cref="ToString"/> deliberately returns a placeholder. Untrusted text that lands in a log
/// line, an exception message or an interpolated prompt by accident is exactly the failure this type
/// exists to prevent.
/// </para>
/// </remarks>
public readonly struct RequesterText : IEquatable<RequesterText>
{
    /// <summary>What <see cref="ToString"/> yields instead of the text itself.</summary>
    public const string Placeholder = "[requester text — not for agent consumption]";

    private readonly string? _value;

    private RequesterText(string value) => _value = value;

    /// <summary>Whether the text is absent or blank.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(_value);

    /// <summary>How many characters were submitted. Safe to log.</summary>
    public int Length => _value?.Length ?? 0;

    /// <summary>Wraps text a person submitted.</summary>
    public static RequesterText From(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return new RequesterText(raw);
    }

    /// <summary>Takes the untrusted text off a filed request.</summary>
    public static RequesterText FromRequest(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new RequesterText(request.RawText);
    }

    /// <summary>Nothing submitted.</summary>
    public static RequesterText Empty => default;

    /// <summary>
    /// Hands the characters to the refiner, and only to the refiner: the refinement prompt is the
    /// single place in Charter where untrusted text is legitimately read, because refinement is what
    /// turns it into something model-authored.
    /// </summary>
    internal string RevealForRefinementPrompt() => _value ?? string.Empty;

    /// <summary>
    /// Hands the characters to the injection scanner, which reads them to classify them rather than
    /// to act on them (section 16).
    /// </summary>
    internal string RevealForScanning() => _value ?? string.Empty;

    /// <summary>
    /// Hands the characters to the storage layer, so a turn can be written to a column and read back
    /// into a <see cref="RequesterText"/> unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Section 2.3 requires the conversation to be resumable from Postgres alone, and characters that
    /// cannot leave the type cannot be persisted. This is the third and last reveal site, it is
    /// assembly-internal like the other two, and it is named for the one thing it is for: a round
    /// trip through a column, ending in <see cref="From"/> on the way back.
    /// </para>
    /// <para>
    /// Deliberately <em>not</em> a general accessor. Keeping the count of reveal sites small is what
    /// makes "who can read untrusted text" an auditable question — three call sites, each named after
    /// its single legitimate consumer.
    /// </para>
    /// </remarks>
    internal string RevealForPersistence() => _value ?? string.Empty;

    /// <summary>
    /// Shows the characters back to the person who typed them.
    /// </summary>
    /// <remarks>
    /// The requester's own thread renders their own words; a conversation surface that replaced them
    /// with a placeholder would be unusable. This is the one audience for whom the text was never
    /// untrusted, and it is a different act from handing the text to a model — which is why it is a
    /// separately named site rather than a second use of
    /// <see cref="RevealForRefinementPrompt"/>. It is assembly-internal like the rest, and its only
    /// caller is the request projection.
    /// </remarks>
    internal string RevealForRequesterEcho() => _value ?? string.Empty;

    /// <inheritdoc />
    public bool Equals(RequesterText other) =>
        string.Equals(_value, other._value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RequesterText other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value?.GetHashCode(StringComparison.Ordinal) ?? 0;

    /// <summary>Equality.</summary>
    public static bool operator ==(RequesterText left, RequesterText right) => left.Equals(right);

    /// <summary>Inequality.</summary>
    public static bool operator !=(RequesterText left, RequesterText right) => !left.Equals(right);

    /// <summary>Returns <see cref="Placeholder"/>, never the text.</summary>
    public override string ToString() => Placeholder;
}
