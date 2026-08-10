using Charter.Api.Contracts;
using Charter.Domain;
using Charter.Refinement;

namespace Charter.Api.Requests;

/// <summary>
/// Rebuilds the refinement conversation from Postgres.
/// </summary>
/// <remarks>
/// <para>
/// Section 2.3: the container can restart mid-session and there is no in-memory orchestration state,
/// so the thread has to be derivable from rows. The rows are <c>conversation_turns</c> — the turns of
/// the <see cref="ConversationRecord"/> bound to this request — plus two the conversation does not
/// necessarily hold: the request's own <see cref="Request.RawText"/>, which is the opening turn in
/// the requester's own words, and the <see cref="Spec"/>, whose existence is Charter having proposed
/// something.
/// </para>
/// <para>
/// Every message id is deterministic — a turn's own id, or a value derived from the request or spec
/// id — so a client that refetches and then replays the stream dedupes rather than duplicating
/// (section 2.3).
/// </para>
/// <para>
/// <strong>Section 16.</strong> A requester turn's characters come back through
/// <see cref="ConversationTurnRecord.RequesterText"/> and are echoed to the person who typed them.
/// They are not handed onward as model-authored text anywhere on this path: the projection reads
/// <see cref="ConversationTurnRecord.AuthoredText"/> only for turns the record itself says are not
/// requester turns, and that property throws for the rest.
/// </para>
/// </remarks>
public static class RefinementThread
{
    /// <summary>The turns that can be rebuilt from stored rows, oldest first.</summary>
    /// <param name="request">The request the thread belongs to.</param>
    /// <param name="spec">The current spec, if refinement has produced one.</param>
    /// <param name="conversation">
    /// The persisted conversation, when one is bound to this request. A request filed before its
    /// refine job has run has none yet, and the thread is then just the opening turn.
    /// </param>
    public static IReadOnlyList<RefinementMessageResponse> Derive(
        Request request,
        Spec? spec,
        ConversationRecord? conversation = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var messages = new List<RefinementMessageResponse>();

        if (conversation is not null)
        {
            foreach (var turn in conversation.Turns)
            {
                if (Project(turn) is { } message)
                {
                    messages.Add(message);
                }
            }
        }

        // The opening turn is the request itself. It is prepended only when the conversation does not
        // already carry it, because a refine job that seeded the conversation from the request would
        // otherwise show the same sentence twice.
        if (!messages.Exists(message =>
                message.Author == ApiRefinementAuthor.Requester
                && string.Equals(message.Body, request.RawText, StringComparison.Ordinal)))
        {
            messages.Insert(0, new RefinementMessageResponse
            {
                Id = $"{request.Id:N}:opening",
                Author = ApiRefinementAuthor.Requester,

                // Section 16: this is untrusted text. It is echoed back to the person who typed it and
                // is never handed to an agent — the agent sees the model-authored spec.
                Kind = ApiRefinementMessageKind.Message,
                Body = request.RawText,
                CreatedAt = request.CreatedAt,
            });
        }

        // A spec row with no proposal turn behind it is a spec written by a path that did not record
        // one. Announcing it is more honest than a confirmation card that appears out of nowhere.
        if (spec is not null
            && !messages.Exists(message => message.Kind == ApiRefinementMessageKind.SpecProposed))
        {
            messages.Add(new RefinementMessageResponse
            {
                Id = $"{spec.Id:N}:proposed",
                Author = ApiRefinementAuthor.Charter,
                Kind = ApiRefinementMessageKind.SpecProposed,
                Body = "That is enough to build from. Read the checks at the bottom — those are what "
                       + "you will be asked to confirm at the end.",
                CreatedAt = spec.CreatedAt,
                SpecVersion = spec.Version,
            });
        }

        return messages;
    }

    /// <summary>
    /// The turn a requester just submitted, ready to broadcast.
    /// </summary>
    /// <remarks>
    /// The id is derived from the request and the submission time rather than allocated at random, so
    /// a retried publish after a reconnect carries the id the client already has.
    /// </remarks>
    public static RefinementMessageResponse RequesterTurn(Guid requestId, string body, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(body);

        return new RefinementMessageResponse
        {
            Id = $"{requestId:N}:turn:{at.ToUnixTimeMilliseconds()}",
            Author = ApiRefinementAuthor.Requester,
            Kind = ApiRefinementMessageKind.Message,
            Body = body,
            CreatedAt = at,
        };
    }

    /// <summary>
    /// One stored turn as the client's <c>RefinementMessage</c>, or <c>null</c> when it is not a turn
    /// the requester is meant to read.
    /// </summary>
    /// <remarks>
    /// Two kinds are deliberately dropped. A mode promotion reads <c>Plan -&gt; Build</c>, which is
    /// Charter's vocabulary and not a sentence anybody typed; an engineer note names the reviewing
    /// engineer by id, and section 7.1 does not put ids in front of requesters. Both stay in the row,
    /// which is where an audit reads them.
    /// </remarks>
    private static RefinementMessageResponse? Project(ConversationTurnRecord turn)
    {
        var kind = turn.Kind switch
        {
            ConversationTurnKind.RequesterMessage => ApiRefinementMessageKind.Message,
            ConversationTurnKind.ClarifyingQuestion => ApiRefinementMessageKind.Question,
            ConversationTurnKind.Answer => ApiRefinementMessageKind.Message,
            ConversationTurnKind.Refusal => ApiRefinementMessageKind.Refusal,
            ConversationTurnKind.SpecProposed => ApiRefinementMessageKind.SpecProposed,
            ConversationTurnKind.SpecConfirmed => ApiRefinementMessageKind.Message,
            _ => (ApiRefinementMessageKind?)null,
        };

        if (kind is not { } messageKind)
        {
            return null;
        }

        return new RefinementMessageResponse
        {
            Id = turn.Id.ToString("N"),
            Author = turn.IsUntrusted ? ApiRefinementAuthor.Requester : ApiRefinementAuthor.Charter,
            Kind = messageKind,

            // Section 16: the requester's own words come back typed as untrusted and are revealed
            // here only to be shown to the person who wrote them. Everything else is model-authored,
            // and AuthoredText refuses to serve a requester turn.
            Body = turn.IsUntrusted ? turn.RequesterText.RevealForRequesterEcho() : turn.AuthoredText,
            CreatedAt = turn.At,

            // Section 10: a refusal tells the requester a human is now involved rather than leaving
            // them at a dead end.
            RoutedToEngineer = messageKind == ApiRefinementMessageKind.Refusal ? true : null,
        };
    }
}
