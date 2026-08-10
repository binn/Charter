using Charter.Refinement;

namespace Charter.Domain;

/// <summary>
/// The durable form of a refinement conversation (sections 2.3, 10, 10b).
/// </summary>
/// <remarks>
/// <para>
/// <c>RefinementConversation</c> is the aggregate that owns the promotion rule and the section 16
/// posture; this is the row it lives in between requests. Section 2.3 is not negotiable about it:
/// <em>"the container can restart mid-session; no in-memory orchestration state; every session must
/// be fully resumable from Postgres alone"</em>. A refinement conversation held only in a field on a
/// singleton is exactly the state that constraint forbids, and a PaaS container restarts whenever it
/// likes.
/// </para>
/// <para>
/// The mode vocabulary is <see cref="InteractionMode"/> and <see cref="ConversationTurnKind"/> rather
/// than a Domain-local copy. Two enums spelling the same three modes would drift, and the drift would
/// show up as a conversation reloading into the wrong mode — which is a security question, because
/// Build is the only mode that dispatches an agent.
/// </para>
/// </remarks>
public sealed class ConversationRecord
{
    private readonly List<ConversationTurnRecord> _turns = [];

    private ConversationRecord()
    {
    }

    private ConversationRecord(Guid id, Guid orgId, InteractionMode mode, DateTimeOffset startedAt)
    {
        Id = id;
        OrgId = orgId;
        Mode = mode;
        StartedAt = startedAt;
        UpdatedAt = startedAt;
    }

    public Guid Id { get; private set; }

    public Guid OrgId { get; private set; }

    /// <summary>The request this conversation produced, once one exists. Chat mode produces none.</summary>
    public Guid? RequestId { get; private set; }

    /// <summary>The mode the conversation was in when it was last written.</summary>
    public InteractionMode Mode { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Every turn, oldest first. Promotion never truncates this (section 10b).</summary>
    /// <remarks>
    /// Sorted on the way out rather than trusted to arrive sorted. A relational load has no inherent
    /// order, and a transcript that comes back shuffled after a restart is worse than one that comes
    /// back empty — it reads as a real conversation that nobody had.
    /// </remarks>
    public IReadOnlyList<ConversationTurnRecord> Turns
    {
        get
        {
            for (var i = 1; i < _turns.Count; i++)
            {
                if (_turns[i - 1].Seq > _turns[i].Seq)
                {
                    _turns.Sort(static (left, right) => left.Seq.CompareTo(right.Seq));
                    break;
                }
            }

            return _turns;
        }
    }

    /// <summary>
    /// The instruction-shaped-language flags raised against this conversation, as jsonb.
    /// </summary>
    /// <remarks>
    /// JSON text rather than a mapped collection, for the same reason as <c>Spec.AcceptanceCriteria</c>
    /// and <c>Event.Payload</c>: the domain owns no serialiser. <see cref="FlagCount"/> is stored
    /// alongside so <see cref="RequiresEngineerReview"/> never needs the document parsed.
    /// </remarks>
    public string? Flags { get; private set; }

    /// <summary>How many flags were raised. Kept as a column so the review gate is a cheap predicate.</summary>
    public int FlagCount { get; private set; }

    /// <summary>Whether an engineer has read the flags.</summary>
    public bool FlagsCleared { get; private set; }

    /// <summary>Section 16: an engineer must look before anything is dispatched.</summary>
    public bool RequiresEngineerReview => FlagCount > 0 && !FlagsCleared;

    /// <summary>The current structured spec, as jsonb, if the refiner has produced one.</summary>
    public string? Spec { get; private set; }

    /// <summary>The content hash of the spec that was confirmed, if one was.</summary>
    public string? ConfirmedContentHash { get; private set; }

    /// <summary>Who confirmed it. The human accountable for the run (section 10).</summary>
    public Guid? ConfirmedBy { get; private set; }

    public DateTimeOffset? ConfirmedAt { get; private set; }

    /// <summary>Whether a human has confirmed the acceptance criteria.</summary>
    public bool IsConfirmed => ConfirmedContentHash is not null;

    /// <summary>Whether this conversation may write to the repository. Only Build may (section 10b).</summary>
    public bool AllowsRepoWrite => ModePromotion.AllowsRepoWrite(Mode);

    /// <summary>Opens a conversation.</summary>
    public static ConversationRecord Start(
        Guid orgId,
        InteractionMode mode = InteractionMode.Chat,
        Guid? requestId = null,
        DateTimeOffset? now = null,
        Guid? id = null)
        => new(id ?? Guid.CreateVersion7(), orgId, mode, DomainTime.Resolve(now))
        {
            RequestId = requestId,
        };

    /// <summary>
    /// Appends what a person typed. The raw characters go in and never come back out as anything but
    /// a <see cref="RequesterText"/> (section 16).
    /// </summary>
    public ConversationTurnRecord AppendRequesterMessage(string rawText, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(rawText);

        return Append(ConversationTurnRecord.ForRequester(Id, NextSeq(), rawText, Mode, now));
    }

    /// <summary>Appends something Charter or the refiner produced.</summary>
    public ConversationTurnRecord AppendCharterTurn(
        ConversationTurnKind kind,
        string text,
        DateTimeOffset? now = null)
        => Append(ConversationTurnRecord.ForCharter(Id, NextSeq(), kind, text, Mode, now));

    /// <summary>
    /// Records the flags a scan raised, as the JSON the caller serialised, and reopens engineer review.
    /// </summary>
    public void RecordFlags(string? flags, int flagCount, DateTimeOffset? now = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(flagCount);

        Flags = string.IsNullOrWhiteSpace(flags) ? null : flags;
        FlagCount = flagCount;

        if (flagCount > 0)
        {
            FlagsCleared = false;
        }

        Touch(now);
    }

    /// <summary>An engineer has read the flags and is content for this to proceed.</summary>
    public void ClearFlags(DateTimeOffset? now = null)
    {
        FlagsCleared = true;
        Touch(now);
    }

    /// <summary>Attaches or replaces the structured spec. Confirmation does not survive a replacement.</summary>
    public void RecordSpec(string? spec, DateTimeOffset? now = null)
    {
        Spec = string.IsNullOrWhiteSpace(spec) ? null : spec;
        ConfirmedContentHash = null;
        ConfirmedBy = null;
        ConfirmedAt = null;
        Touch(now);
    }

    /// <summary>Records that a human confirmed the acceptance criteria of a particular spec.</summary>
    public void RecordConfirmation(Guid confirmedBy, string contentHash, DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);

        ConfirmedBy = confirmedBy;
        ConfirmedContentHash = contentHash;
        ConfirmedAt = DomainTime.Resolve(now);
        Touch(now);
    }

    /// <summary>Attaches the request this conversation produced.</summary>
    public void BindRequest(Guid requestId, DateTimeOffset? now = null)
    {
        RequestId = requestId;
        Touch(now);
    }

    /// <summary>
    /// Moves the conversation up a mode, keeping every turn.
    /// </summary>
    /// <remarks>
    /// The rule is <see cref="ModePromotion"/>'s, not a second copy of it: the row and the aggregate
    /// must refuse <c>chat → build</c> identically, or resuming from Postgres would be a way around
    /// the aggregate.
    /// </remarks>
    /// <exception cref="ModePromotionException">The promotion is not permitted, or nothing is confirmed.</exception>
    public void PromoteTo(InteractionMode mode, DateTimeOffset? now = null)
    {
        if (!ModePromotion.IsPermitted(Mode, mode))
        {
            throw new ModePromotionException(Mode, mode, ModePromotion.Explain(Mode, mode));
        }

        if (mode == InteractionMode.Build && !IsConfirmed)
        {
            throw new ModePromotionException(
                Mode,
                mode,
                "Nothing gets built until someone has confirmed what it should do. Confirm the spec first.");
        }

        if (mode == InteractionMode.Build && RequiresEngineerReview)
        {
            throw new ModePromotionException(
                Mode,
                mode,
                "This request was flagged for an engineer to read before anything runs.");
        }

        var from = Mode;
        Mode = mode;
        AppendCharterTurn(ConversationTurnKind.ModePromoted, $"{from} -> {mode}", now);
    }

    private ConversationTurnRecord Append(ConversationTurnRecord turn)
    {
        _turns.Add(turn);
        Touch(turn.At);

        return turn;
    }

    private int NextSeq()
    {
        var highest = 0;

        foreach (var turn in _turns)
        {
            if (turn.Seq > highest)
            {
                highest = turn.Seq;
            }
        }

        return highest + 1;
    }

    private void Touch(DateTimeOffset? now) => UpdatedAt = DomainTime.Resolve(now);
}

/// <summary>
/// One persisted turn. The durable counterpart of <c>ConversationTurn</c>, and it keeps that type's
/// security property rather than becoming a way around it.
/// </summary>
/// <remarks>
/// <para>
/// A turn holds either untrusted requester text or model-authored text, never both, and the two are
/// reachable through different members. The stored characters live in a private property that only EF
/// touches, so there is no <c>Text</c> to read: <see cref="AuthoredText"/> throws on a requester turn,
/// and requester text comes back only as a <see cref="RequesterText"/>. Persistence is therefore a
/// one-way valve — a bare string goes in from the same place <c>Request.RawText</c> comes from, and a
/// typed, prompt-hostile value comes out.
/// </para>
/// <para>
/// That matters more here than on the aggregate. A row is the thing a resume path reads, and a resume
/// path that handed a prompt builder <c>turn.Body</c> would undo section 16 without touching any of
/// the code that was written to enforce it.
/// </para>
/// </remarks>
public sealed class ConversationTurnRecord
{
    private ConversationTurnRecord()
    {
    }

    private ConversationTurnRecord(
        Guid id,
        Guid conversationId,
        int seq,
        ConversationTurnKind kind,
        InteractionMode mode,
        string body,
        DateTimeOffset at)
    {
        Id = id;
        ConversationId = conversationId;
        Seq = seq;
        Kind = kind;
        Mode = mode;
        Body = body;
        At = at;
    }

    public Guid Id { get; private set; }

    public Guid ConversationId { get; private set; }

    /// <summary>Monotonic within a conversation, starting at 1. What restores the ordering on reload.</summary>
    public int Seq { get; private set; }

    public ConversationTurnKind Kind { get; private set; }

    /// <summary>The mode the conversation was in when the turn happened.</summary>
    public InteractionMode Mode { get; private set; }

    public DateTimeOffset At { get; private set; }

    /// <summary>
    /// The stored characters. Private on purpose — it is the column, not an accessor, and the two
    /// public readers below are the only ways to it.
    /// </summary>
    private string Body { get; set; } = string.Empty;

    /// <summary>Whether this turn carries untrusted text.</summary>
    public bool IsUntrusted => Kind == ConversationTurnKind.RequesterMessage;

    /// <summary>How many characters were stored. Safe to log for either kind of turn.</summary>
    public int Length => Body.Length;

    /// <summary>The model- or Charter-authored text.</summary>
    /// <exception cref="InvalidOperationException">This is a requester turn.</exception>
    public string AuthoredText => IsUntrusted
        ? throw new InvalidOperationException(
            "This turn holds untrusted requester text. Read it as RequesterText — it is not "
            + "model-authored and must never be handed onward as if it were (section 16).")
        : Body;

    /// <summary>The untrusted text, typed so it cannot be mistaken for something an agent may be told.</summary>
    /// <exception cref="InvalidOperationException">This is not a requester turn.</exception>
    public RequesterText RequesterText => IsUntrusted
        ? Refinement.RequesterText.From(Body)
        : throw new InvalidOperationException("This turn was not authored by a requester.");

    /// <summary>Records something a person typed.</summary>
    internal static ConversationTurnRecord ForRequester(
        Guid conversationId,
        int seq,
        string rawText,
        InteractionMode mode,
        DateTimeOffset? now)
    {
        ArgumentNullException.ThrowIfNull(rawText);

        return new ConversationTurnRecord(
            Guid.CreateVersion7(),
            conversationId,
            seq,
            ConversationTurnKind.RequesterMessage,
            mode,
            rawText,
            DomainTime.Resolve(now));
    }

    /// <summary>Records something Charter or the refiner produced.</summary>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is a requester turn.</exception>
    internal static ConversationTurnRecord ForCharter(
        Guid conversationId,
        int seq,
        ConversationTurnKind kind,
        string text,
        InteractionMode mode,
        DateTimeOffset? now)
    {
        if (kind == ConversationTurnKind.RequesterMessage)
        {
            throw new ArgumentException(
                "A requester turn must be recorded with AppendRequesterMessage so its text stays "
                + "typed as untrusted.",
                nameof(kind));
        }

        ArgumentNullException.ThrowIfNull(text);

        return new ConversationTurnRecord(
            Guid.CreateVersion7(),
            conversationId,
            seq,
            kind,
            mode,
            text,
            DomainTime.Resolve(now));
    }
}
