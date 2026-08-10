using Charter.Api.Contracts;
using Charter.Auth.Authorization;
using Charter.Domain;

namespace Charter.Api.Requests;

/// <summary>
/// Section 7.5's four post-hoc actions, as affordances.
/// </summary>
/// <remarks>
/// <para>
/// The booleans decide whether a control is <em>offered</em>. They are not the authorisation check —
/// <see cref="RequestCommandService"/> refuses each POST on its own terms, and deliberately refuses
/// exactly what this declines to offer. Two spellings of "may I steer this" is how a button that is
/// not shown turns out to still work.
/// </para>
/// <para>
/// The whole object is omitted for a viewer who may perform none of them (section 7.4), so the panel
/// is absent rather than present-and-empty. It is also omitted when no branch is known, because the
/// take-over confirmation names the branch it stops writes to and <em>"stops writes to that
/// branch"</em> is not a sentence worth showing without one.
/// </para>
/// </remarks>
public static class SessionActionsProjection
{
    /// <summary>The panel, or <c>null</c> when it must not be sent.</summary>
    /// <param name="session">The latest session, if any.</param>
    /// <param name="changeRequest">The change request, which is where the branch name comes from.</param>
    /// <param name="visibility">Section 7.4's answer for this viewer.</param>
    /// <param name="handedOffByName">Who took it over, by display name. Never a user id (section 7.1).</param>
    public static SessionActionsResponse? Panel(
        Session? session,
        ChangeRequest? changeRequest,
        SessionVisibility visibility,
        string? handedOffByName)
    {
        ArgumentNullException.ThrowIfNull(visibility);

        // Section 7.5's actions are the engineer's, and section 7.4 gives repository read as the one
        // gate that names an engineer. An approver holds the spend gate, which is a different gate.
        if (!visibility.Transcript || session is null)
        {
            return null;
        }

        var branch = changeRequest?.HeadBranch;
        if (string.IsNullOrWhiteSpace(branch))
        {
            return null;
        }

        var handedOff = session.Status == SessionStatus.HandedOff;

        return new SessionActionsResponse
        {
            // Approving is a review verdict on work that exists, so it is offered once there is a
            // change request to review and not while the agent is still writing to it.
            CanApprove = !handedOff && session.Status is SessionStatus.PrOpen
                or SessionStatus.PreviewReady
                or SessionStatus.InReview,

            // Steer continues the session; revise forks the spec onto the same branch. Both are
            // agent writes, and a taken-over branch takes no more of those — ever.
            CanSteer = !handedOff && !session.IsTerminal,
            CanRevise = !handedOff && session.Status != SessionStatus.Merged,

            // Taking over is the escape hatch, so it stays available right up until it is used —
            // including on a failed session, which is the case it exists for.
            CanTakeOver = !handedOff,
            Branch = branch,
            HandedOff = handedOff
                ? new HandedOffResponse
                {
                    At = session.EndedAt ?? session.CreatedAt,
                    ByName = string.IsNullOrWhiteSpace(handedOffByName)
                        ? "An engineer"
                        : handedOffByName,
                }
                : null,
        };
    }
}
