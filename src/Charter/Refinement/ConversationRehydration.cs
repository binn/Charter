using System.Text.Json;
using System.Text.Json.Serialization;
using Charter.Domain;

namespace Charter.Refinement;

/// <summary>
/// The bridge between the <see cref="ConversationRecord"/> row and the live
/// <see cref="RefinementConversation"/> aggregate (section 2.3).
/// </summary>
/// <remarks>
/// <para>
/// Section 2.3 does not leave this optional: <em>"the container can restart mid-session; no in-memory
/// orchestration state; every session must be fully resumable from Postgres alone."</em> Rows without
/// a way back to the aggregate are a half-finished version of that — the conversation survives, and
/// the rules that make it safe do not.
/// </para>
/// <para>
/// It lives in <c>Charter.Refinement</c> rather than in <c>Charter.Domain</c> because the reverse
/// direction needs a serialiser, and the domain deliberately owns none: the jsonb columns are JSON
/// text and somebody outside the entity decides the spelling. This file is that somebody, so there is
/// exactly one spelling of a stored spec and one of a stored flag list.
/// </para>
/// <para>
/// The section 16 boundary is carried across, not around. Requester turns come back as
/// <see cref="RequesterText"/> — never as a string — and the restored aggregate's
/// <c>AuthoredText</c> still throws for them.
/// </para>
/// </remarks>
public static class ConversationRehydration
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>Rebuilds the live aggregate from a loaded row and its turns.</summary>
    /// <remarks>
    /// A stored confirmation is honoured only when the stored hash still matches the stored document.
    /// If a spec was edited behind a confirmation, this comes back unconfirmed rather than
    /// dispatchable — <em>"the spec said X"</em> has to keep meaning something across a restart
    /// (section 10b).
    /// </remarks>
    public static RefinementConversation ToConversation(ConversationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var spec = ReadSpec(record.Spec);
        var approved = ReadApproval(record, spec);

        return RefinementConversation.Restore(
            record.Id,
            record.RequestId,
            record.Mode,
            record.StartedAt,
            record.Turns.Select(ToTurn),
            ReadFlags(record.Flags),
            record.FlagsCleared,
            spec,
            approved);
    }

    /// <summary>Rebuilds one turn, keeping the either/or invariant the row stores it under.</summary>
    public static ConversationTurn ToTurn(ConversationTurnRecord turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        return turn.IsUntrusted
            ? ConversationTurn.Restore(turn.Kind, turn.Mode, authored: null, turn.RequesterText, turn.At)
            : ConversationTurn.Restore(turn.Kind, turn.Mode, turn.AuthoredText, RequesterText.Empty, turn.At);
    }

    /// <summary>The stored spelling of a structured spec, for <c>conversations.spec</c>.</summary>
    public static string WriteSpec(SpecDocument spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        return JsonSerializer.Serialize(
            new StoredSpec
            {
                Title = spec.Title,
                Outcome = spec.Outcome,
                AcceptanceCriteria = spec.AcceptanceCriteria,
                TechnicalApproach = spec.TechnicalApproach,
                Files = spec.Scope.Files,
                Paths = spec.Scope.Paths,
                Risks = spec.Risks,
                OpenQuestions = spec.OpenQuestions,
            },
            Options);
    }

    /// <summary>The stored spelling of the section 16 flags, for <c>conversations.flags</c>.</summary>
    public static string WriteFlags(IReadOnlyList<InjectionSignal> flags)
    {
        ArgumentNullException.ThrowIfNull(flags);
        return JsonSerializer.Serialize(flags, Options);
    }

    /// <summary>Reads a stored spec back, or <c>null</c> when the column holds nothing usable.</summary>
    /// <remarks>
    /// A document that cannot be read comes back as <c>null</c> rather than as a partially populated
    /// spec. Half a spec is worse than none: it would be renderable, confirmable, and wrong.
    /// </remarks>
    public static SpecDocument? ReadSpec(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        StoredSpec? stored;

        try
        {
            stored = JsonSerializer.Deserialize<StoredSpec>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (stored is null
            || string.IsNullOrWhiteSpace(stored.Title)
            || string.IsNullOrWhiteSpace(stored.Outcome)
            || stored.AcceptanceCriteria is not { Count: > 0 })
        {
            return null;
        }

        return SpecDocument.Create(
            stored.Title,
            stored.Outcome,
            stored.AcceptanceCriteria,
            stored.TechnicalApproach,
            SpecScope.Of(stored.Files, stored.Paths),
            stored.Risks,
            stored.OpenQuestions);
    }

    /// <summary>Reads the stored flags back. An unreadable document reads as no flags recorded.</summary>
    public static IReadOnlyList<InjectionSignal> ReadFlags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<InjectionSignal>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static ApprovedSpec? ReadApproval(ConversationRecord record, SpecDocument? spec)
    {
        if (spec is null
            || record.ConfirmedContentHash is not { Length: > 0 } hash
            || record.ConfirmedBy is not { } confirmedBy
            || record.ConfirmedAt is not { } confirmedAt)
        {
            return null;
        }

        // Section 10b: the fingerprint is recomputed from the document rather than trusted from the
        // column, so a spec edited after confirmation restores as unconfirmed.
        return string.Equals(spec.ContentHash, hash, StringComparison.Ordinal)
            ? ApprovedSpec.Restore(spec, confirmedBy, confirmedAt)
            : null;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        // Stored enums read the way the rest of the schema spells them: `role_override`, not `0`.
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        options.MakeReadOnly(populateMissingResolver: true);

        return options;
    }

    /// <summary>The shape <c>conversations.spec</c> holds.</summary>
    private sealed record StoredSpec
    {
        public string? Title { get; init; }

        public string? Outcome { get; init; }

        public IReadOnlyList<string>? AcceptanceCriteria { get; init; }

        public string? TechnicalApproach { get; init; }

        public IReadOnlyList<string>? Files { get; init; }

        public IReadOnlyList<string>? Paths { get; init; }

        public IReadOnlyList<string>? Risks { get; init; }

        public IReadOnlyList<string>? OpenQuestions { get; init; }
    }
}
