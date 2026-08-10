using Charter.Domain;

namespace Charter.Refinement;

/// <summary>Who or what authored a turn in a refinement conversation.</summary>
public enum ConversationTurnKind
{
    /// <summary>Untrusted. What a person typed, in their own words.</summary>
    RequesterMessage,

    /// <summary>A question the refiner asked because something was still ambiguous.</summary>
    ClarifyingQuestion,

    /// <summary>An answer, in Chat mode. Produces nothing and dispatches nothing.</summary>
    Answer,

    /// <summary>A refusal, with a plain-English reason.</summary>
    Refusal,

    /// <summary>A spec the refiner proposed.</summary>
    SpecProposed,

    /// <summary>The requester confirmed the spec's acceptance criteria.</summary>
    SpecConfirmed,

    /// <summary>The conversation moved up a mode.</summary>
    ModePromoted,

    /// <summary>An engineer said something, or cleared a flag.</summary>
    EngineerNote,
}

/// <summary>
/// One turn. History survives promotion, which is what makes the three modes one surface rather
/// than three apps (section 10b).
/// </summary>
/// <remarks>
/// A turn holds either untrusted requester text or model-authored text, never both, and the two are
/// reachable through different members. <see cref="AuthoredText"/> throws on a requester turn, so
/// the concatenate-the-transcript-into-a-prompt mistake does not compile into existence quietly —
/// it fails loudly the first time it runs.
/// </remarks>
public sealed class ConversationTurn
{
    private readonly string _authored;
    private readonly RequesterText _requester;

    private ConversationTurn(
        ConversationTurnKind kind,
        InteractionMode mode,
        string authored,
        RequesterText requester,
        DateTimeOffset at)
    {
        Kind = kind;
        Mode = mode;
        _authored = authored;
        _requester = requester;
        At = at;
    }

    /// <summary>What kind of turn this is.</summary>
    public ConversationTurnKind Kind { get; }

    /// <summary>The mode the conversation was in when the turn happened.</summary>
    public InteractionMode Mode { get; }

    /// <summary>When it happened.</summary>
    public DateTimeOffset At { get; }

    /// <summary>Whether this turn carries untrusted text.</summary>
    public bool IsUntrusted => Kind == ConversationTurnKind.RequesterMessage;

    /// <summary>The model- or Charter-authored text.</summary>
    /// <exception cref="InvalidOperationException">This is a requester turn.</exception>
    public string AuthoredText => IsUntrusted
        ? throw new InvalidOperationException(
            "This turn holds untrusted requester text. Read it as RequesterText — it is not "
            + "model-authored and must never be handed onward as if it were (section 16).")
        : _authored;

    /// <summary>The untrusted text.</summary>
    /// <exception cref="InvalidOperationException">This is not a requester turn.</exception>
    public RequesterText RequesterText => IsUntrusted
        ? _requester
        : throw new InvalidOperationException("This turn was not authored by a requester.");

    /// <summary>Records something a person typed.</summary>
    public static ConversationTurn FromRequester(
        RequesterText text,
        InteractionMode mode,
        DateTimeOffset at) =>
        new(ConversationTurnKind.RequesterMessage, mode, string.Empty, text, at);

    /// <summary>Records something Charter or the refiner produced.</summary>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is a requester turn.</exception>
    public static ConversationTurn FromCharter(
        ConversationTurnKind kind,
        string text,
        InteractionMode mode,
        DateTimeOffset at)
    {
        if (kind == ConversationTurnKind.RequesterMessage)
        {
            throw new ArgumentException(
                "A requester turn must be recorded with FromRequester so its text stays typed as "
                + "untrusted.",
                nameof(kind));
        }

        ArgumentNullException.ThrowIfNull(text);
        return new ConversationTurn(kind, mode, text, Refinement.RequesterText.Empty, at);
    }

    /// <summary>
    /// Rebuilds a turn that was read back out of Postgres.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately an <c>internal static</c> method rather than a constructor. Section 16's
    /// structural guarantee is checked by reflection over non-private constructors and public statics
    /// on the dispatch-path types, and a resume path is not a reason to widen that surface — an
    /// assembly-internal factory is reachable from <c>Charter.Refinement</c> and from nowhere else.
    /// </para>
    /// <para>
    /// The either/or invariant is restored, not bypassed: a requester kind carries the
    /// <see cref="RequesterText"/> and nothing in <c>_authored</c>, every other kind carries the
    /// string and nothing in <c>_requester</c>. A round-tripped requester turn therefore still throws
    /// from <see cref="AuthoredText"/>, which is the property the boundary actually rests on.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="kind"/> disagrees with which text was supplied.
    /// </exception>
    internal static ConversationTurn Restore(
        ConversationTurnKind kind,
        InteractionMode mode,
        string? authored,
        RequesterText requester,
        DateTimeOffset at)
    {
        if (kind == ConversationTurnKind.RequesterMessage)
        {
            if (!string.IsNullOrEmpty(authored))
            {
                throw new ArgumentException(
                    "A restored requester turn carries its text as RequesterText, never as a string: "
                    + "storing it in both places is how untrusted text ends up read as model-authored "
                    + "(section 16).",
                    nameof(authored));
            }

            return new ConversationTurn(kind, mode, string.Empty, requester, at);
        }

        if (!requester.IsEmpty)
        {
            throw new ArgumentException(
                "Only a requester turn carries RequesterText.",
                nameof(requester));
        }

        ArgumentNullException.ThrowIfNull(authored);
        return new ConversationTurn(kind, mode, authored, Refinement.RequesterText.Empty, at);
    }
}

/// <summary>
/// One conversation surface across the three modes of section 10b.
/// </summary>
/// <remarks>
/// <para>
/// The conversation owns the promotion rule, so <c>chat → build</c> is refused by the aggregate
/// rather than by a caller that remembered to check. It also owns the section 16 posture: requester
/// turns are stored as <see cref="RequesterText"/>, the spec is stored as a structured
/// <see cref="SpecDocument"/>, and the only thing that leaves for a build is an
/// <see cref="ApprovedSpec"/>.
/// </para>
/// <para>
/// Promotion to Build additionally requires a confirmed spec. There is no path from a raw request to
/// a running agent that does not pass through refinement and a human confirmation.
/// </para>
/// </remarks>
public sealed class RefinementConversation
{
    private readonly List<ConversationTurn> _turns = [];
    private readonly List<InjectionSignal> _flags = [];

    private RefinementConversation(
        Guid id,
        Guid? requestId,
        InteractionMode mode,
        DateTimeOffset startedAt)
    {
        Id = id;
        RequestId = requestId;
        Mode = mode;
        StartedAt = startedAt;
    }

    /// <summary>The conversation identifier.</summary>
    public Guid Id { get; }

    /// <summary>The request this conversation belongs to, once one exists.</summary>
    public Guid? RequestId { get; private set; }

    /// <summary>The mode the conversation is currently in.</summary>
    public InteractionMode Mode { get; private set; }

    /// <summary>When it started.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>Every turn, oldest first. Promotion never truncates this.</summary>
    public IReadOnlyList<ConversationTurn> Turns => _turns;

    /// <summary>Instruction-shaped language found in submitted text (section 16).</summary>
    public IReadOnlyList<InjectionSignal> Flags => _flags;

    /// <summary>Whether an engineer has looked at the flags.</summary>
    public bool FlagsCleared { get; private set; }

    /// <summary>Whether an engineer must look before anything is dispatched.</summary>
    public bool RequiresEngineerReview => _flags.Count > 0 && !FlagsCleared;

    /// <summary>The current structured spec, if the refiner has produced one.</summary>
    public SpecDocument? Spec { get; private set; }

    /// <summary>The confirmed spec, if the requester has confirmed it.</summary>
    public ApprovedSpec? Approved { get; private set; }

    /// <summary>Whether this conversation may write to the repository. Only Build may.</summary>
    public bool AllowsRepoWrite => ModePromotion.AllowsRepoWrite(Mode);

    /// <summary>Opens a conversation.</summary>
    public static RefinementConversation Start(
        InteractionMode mode = InteractionMode.Chat,
        Guid? requestId = null,
        DateTimeOffset? now = null,
        Guid? id = null) =>
        new(id ?? Guid.CreateVersion7(), requestId, mode, DomainTime.Resolve(now));

    /// <summary>
    /// Rebuilds a conversation that was read back out of Postgres.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Section 2.3: the container restarts mid-session and there is no in-memory orchestration state,
    /// so the aggregate has to be reconstructible from rows. This is that path, and it is an
    /// <c>internal static</c> method rather than a constructor for the reason
    /// <see cref="ConversationTurn.Restore"/> gives — the section 16 reflection tests read
    /// constructors and public statics, and a resume path has no business widening either.
    /// </para>
    /// <para>
    /// Restoring is deliberately not re-running the aggregate's own recorders. Replaying
    /// <see cref="RecordRequesterMessage"/> would rescan every turn for instruction-shaped language
    /// and could raise flags an engineer had already cleared, so the stored flags and the stored
    /// cleared bit are taken as read. What is <em>not</em> taken as read is the promotion rule: a
    /// restored conversation in Build mode with nothing approved is refused here rather than
    /// materialising as a dispatchable conversation nobody confirmed.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The stored rows describe a state the aggregate forbids.</exception>
    internal static RefinementConversation Restore(
        Guid id,
        Guid? requestId,
        InteractionMode mode,
        DateTimeOffset startedAt,
        IEnumerable<ConversationTurn> turns,
        IEnumerable<InjectionSignal> flags,
        bool flagsCleared,
        SpecDocument? spec,
        ApprovedSpec? approved)
    {
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(flags);

        var conversation = new RefinementConversation(id, requestId, mode, startedAt)
        {
            FlagsCleared = flagsCleared,
            Spec = spec,
            Approved = approved,
        };

        conversation._turns.AddRange(turns);
        conversation._flags.AddRange(flags);

        if (mode == InteractionMode.Build && approved is null)
        {
            throw new ArgumentException(
                "A stored conversation in Build mode with nothing confirmed cannot be restored: "
                + "nothing gets built until someone has confirmed what it should do (section 10).",
                nameof(mode));
        }

        if (approved is not null && spec is null)
        {
            throw new ArgumentException(
                "A confirmed conversation has to carry the spec that was confirmed.",
                nameof(spec));
        }

        return conversation;
    }

    /// <summary>
    /// Records something the requester typed, scanning it for instruction-shaped language on the way
    /// in (section 16).
    /// </summary>
    public IReadOnlyList<InjectionSignal> RecordRequesterMessage(
        RequesterText text,
        DateTimeOffset? now = null)
    {
        var at = DomainTime.Resolve(now);
        _turns.Add(ConversationTurn.FromRequester(text, Mode, at));

        var signals = InstructionShapedTextDetector.Scan(text);
        if (signals.Count > 0)
        {
            _flags.AddRange(signals);
            FlagsCleared = false;
        }

        return signals;
    }

    /// <summary>Records something Charter produced.</summary>
    public void RecordCharterTurn(ConversationTurnKind kind, string text, DateTimeOffset? now = null) =>
        _turns.Add(ConversationTurn.FromCharter(kind, text, Mode, DomainTime.Resolve(now)));

    /// <summary>
    /// Attaches a refined spec. Replacing it regenerates both renderings, because both are
    /// projections over whatever document is current.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called in Chat mode, which produces nothing.</exception>
    public void ProposeSpec(SpecDocument spec, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (Mode == InteractionMode.Chat)
        {
            throw new InvalidOperationException(
                "Chat mode produces nothing. Promote the conversation to Plan before proposing a spec.");
        }

        Spec = spec;
        Approved = null;
        RecordCharterTurn(ConversationTurnKind.SpecProposed, spec.ForRequester().Render().Markdown, now);
    }

    /// <summary>An engineer has read the flags and is content for this to proceed.</summary>
    public void ClearFlags(Guid engineerId, string note, DateTimeOffset? now = null)
    {
        FlagsCleared = true;
        RecordCharterTurn(
            ConversationTurnKind.EngineerNote,
            $"Reviewed by {engineerId}: {note}",
            now);
    }

    /// <summary>Builds the confirmation card the requester approves (section 10).</summary>
    /// <exception cref="InvalidOperationException">There is no spec yet.</exception>
    public SpecConfirmationCard ConfirmationCard() =>
        Spec is null
            ? throw new InvalidOperationException("There is no spec to confirm yet.")
            : new SpecConfirmationCard(this, Spec);

    /// <summary>Records a confirmation produced by <see cref="SpecConfirmationCard.Confirm"/>.</summary>
    internal void Accept(ApprovedSpec approved, DateTimeOffset at)
    {
        Approved = approved;
        _turns.Add(ConversationTurn.FromCharter(
            ConversationTurnKind.SpecConfirmed,
            approved.Spec.ForRequester().Render().Markdown,
            Mode,
            at));
    }

    /// <summary>Attaches the request this conversation produced.</summary>
    public void BindRequest(Guid requestId) => RequestId = requestId;

    /// <summary>
    /// Moves the conversation up a mode, keeping every turn.
    /// </summary>
    /// <exception cref="ModePromotionException">
    /// The promotion is not one of <c>chat → plan</c> or <c>plan → build</c>, or Build was asked for
    /// without a confirmed spec.
    /// </exception>
    public void PromoteTo(InteractionMode mode, DateTimeOffset? now = null)
    {
        if (!ModePromotion.IsPermitted(Mode, mode))
        {
            throw new ModePromotionException(Mode, mode, ModePromotion.Explain(Mode, mode));
        }

        if (mode == InteractionMode.Build && Approved is null)
        {
            throw new ModePromotionException(
                Mode,
                mode,
                "Nothing gets built until someone has confirmed what it should do. Confirm the "
                + "spec first.");
        }

        if (mode == InteractionMode.Build && RequiresEngineerReview)
        {
            throw new ModePromotionException(
                Mode,
                mode,
                "This request was flagged for an engineer to read before anything runs.");
        }

        var at = DomainTime.Resolve(now);
        var from = Mode;
        Mode = mode;
        _turns.Add(ConversationTurn.FromCharter(
            ConversationTurnKind.ModePromoted,
            $"{from} -> {mode}",
            mode,
            at));
    }
}
