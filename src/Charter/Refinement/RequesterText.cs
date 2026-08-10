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
/// <see cref="RequesterText"/>, and the only way to get characters back out of it is
/// <see cref="RevealForRefinementPrompt"/> — assembly-internal, named for the one legitimate
/// consumer, and called from exactly one place (<see cref="RefinementPromptBuilder"/>).
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
