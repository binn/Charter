namespace Charter.Refinement;

/// <summary>
/// The three modes of section 10b. They are modes of <em>one</em> conversation surface, not separate
/// apps.
/// </summary>
public enum InteractionMode
{
    /// <summary>Read-only Q&amp;A over the project. Produces nothing and dispatches nothing.</summary>
    Chat,

    /// <summary>Exploring a change before committing to it. Produces a spec; dispatches nothing.</summary>
    Plan,

    /// <summary>Implementing an approved spec. The only mode that dispatches an agent.</summary>
    Build,
}

/// <summary>Thrown when a conversation is asked to move to a mode it may not move to.</summary>
public sealed class ModePromotionException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public ModePromotionException(InteractionMode from, InteractionMode to, string message)
        : base(message)
    {
        From = from;
        To = to;
    }

    /// <summary>Creates the exception.</summary>
    public ModePromotionException()
        : base("That promotion is not permitted.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public ModePromotionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public ModePromotionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The mode the conversation was in.</summary>
    public InteractionMode From { get; }

    /// <summary>The mode it was asked to move to.</summary>
    public InteractionMode To { get; }
}

/// <summary>
/// The promotion rule of section 10b, as a rule rather than a paragraph of documentation.
/// </summary>
/// <remarks>
/// <para>
/// A conversation is promoted <em>upward</em> — <c>chat → plan → build</c> — one step at a time, and
/// history is carried across. Two consequences are load-bearing and enforced here rather than
/// described:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <strong>Chat cannot promote straight to Build.</strong> Chat is where a meaningful share of
/// requests should die, because the answer is often <em>it already does that</em>. Skipping Plan
/// skips the step where that happens, and Plan is also where the spec — the thing that keeps raw
/// requester text away from the agent (section 16) — is authored.
/// </description></item>
/// <item><description>
/// <strong>Chat has no repo write access.</strong> <see cref="AllowsRepoWrite"/> is false for
/// anything but Build, so read-only is a property of the mode rather than of the caller remembering.
/// </description></item>
/// </list>
/// </remarks>
public static class ModePromotion
{
    /// <summary>Whether a conversation in <paramref name="from"/> may move to <paramref name="to"/>.</summary>
    public static bool IsPermitted(InteractionMode from, InteractionMode to) => (from, to) switch
    {
        (InteractionMode.Chat, InteractionMode.Plan) => true,
        (InteractionMode.Plan, InteractionMode.Build) => true,
        _ => false,
    };

    /// <summary>Why a promotion was refused, in language that can be shown to a requester.</summary>
    public static string Explain(InteractionMode from, InteractionMode to) => (from, to) switch
    {
        (InteractionMode.Chat, InteractionMode.Build) =>
            "A question has to become a plan before anything gets built. Let's work out what you "
            + "need first — plenty of questions turn out not to need a change at all.",
        _ when from == to =>
            $"This conversation is already in {Describe(from)}.",
        (InteractionMode.Plan, InteractionMode.Chat) or (InteractionMode.Build, _) =>
            $"A conversation moves forward from {Describe(from)}, not back to {Describe(to)}.",
        _ =>
            $"A conversation cannot move from {Describe(from)} to {Describe(to)}.",
    };

    /// <summary>Whether a mode may write to the repository. Only Build may.</summary>
    public static bool AllowsRepoWrite(InteractionMode mode) => mode == InteractionMode.Build;

    /// <summary>Whether a mode dispatches an agent. Only Build does (section 10b).</summary>
    public static bool DispatchesAgent(InteractionMode mode) => mode == InteractionMode.Build;

    /// <summary>A requester-facing name for a mode.</summary>
    public static string Describe(InteractionMode mode) => mode switch
    {
        InteractionMode.Chat => "a question",
        InteractionMode.Plan => "a plan",
        InteractionMode.Build => "a build",
        _ => mode.ToString(),
    };
}
