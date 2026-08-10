using Charter.Api.Contracts;
using Charter.Domain;

namespace Charter.Api.Requests;

/// <summary>
/// Rebuilds the refinement conversation from Postgres.
/// </summary>
/// <remarks>
/// <para>
/// Section 2.3: the container can restart mid-session and there is no in-memory orchestration state,
/// so the thread has to be derivable from rows. Two rows carry conversation today — the request's own
/// <see cref="Request.RawText"/>, which is the opening turn in the requester's own words, and the
/// <see cref="Spec"/>, whose existence is Charter having proposed something. Both produce
/// deterministic message ids, so a client that refetches and then replays the stream dedupes rather
/// than duplicating.
/// </para>
/// <para>
/// <strong>Known gap.</strong> There is no <c>refinement_messages</c> table yet, so the clarifying
/// questions and answers between those two turns are not persisted and do not appear here. A turn
/// submitted through <c>POST /api/requests/{id}/refinement</c> is durably recorded as the payload of
/// the queued <c>Refine</c> job and broadcast live, but it is not replayed on a later fetch. That is
/// a schema gap, not a projection one: adding the table is what closes it.
/// </para>
/// </remarks>
public static class RefinementThread
{
    /// <summary>The turns that can be rebuilt from stored rows, oldest first.</summary>
    public static IReadOnlyList<RefinementMessageResponse> Derive(Request request, Spec? spec)
    {
        ArgumentNullException.ThrowIfNull(request);

        var messages = new List<RefinementMessageResponse>(2)
        {
            new()
            {
                Id = $"{request.Id:N}:opening",
                Author = ApiRefinementAuthor.Requester,

                // Section 16: this is untrusted text. It is echoed back to the person who typed it and
                // is never handed to an agent — the agent sees the model-authored spec.
                Kind = ApiRefinementMessageKind.Message,
                Body = request.RawText,
                CreatedAt = request.CreatedAt,
            },
        };

        if (spec is not null)
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
}
