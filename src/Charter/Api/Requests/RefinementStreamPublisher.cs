using Charter.Api.Contracts;
using Charter.Api.Projects;
using Charter.Domain;
using Charter.Hubs;
using Charter.Orchestration;
using Microsoft.Extensions.Logging;

namespace Charter.Api.Requests;

/// <summary>
/// <see cref="IRefinementStream"/>, rendered by the same code a reload runs.
/// </summary>
/// <remarks>
/// <para>
/// This is the second half of the layering decision <see cref="IRefinementStream"/> describes.
/// <c>Charter.Orchestration</c> declares what it needs in rows; this — which already sees
/// <see cref="RefinementThread"/>, <see cref="RequestProjection"/> and the hub — renders it. The point
/// is not tidiness. Every frame below comes out of the <em>same functions</em> that build
/// <c>GET /api/requests/{id}</c>, so a live view and a reload cannot disagree about a message id, a
/// body or an acceptance criterion: they are the same objects from the same projection, not two
/// renderings kept in step by convention.
/// </para>
/// <para>
/// Nothing here throws. Section 2.3 makes the rows the record and the stream a courtesy on top, so a
/// hub that is unreachable must not fail the refinement turn that has already been committed — the
/// requester would then get a retried model call instead of a slightly late thread.
/// </para>
/// </remarks>
public sealed class RefinementStreamPublisher : IRefinementStream
{
    private readonly IRequestStreamPublisher stream;
    private readonly RequestQueryService queries;
    private readonly ILogger<RefinementStreamPublisher> logger;

    public RefinementStreamPublisher(
        IRequestStreamPublisher stream,
        RequestQueryService queries,
        ILogger<RefinementStreamPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(logger);

        this.stream = stream;
        this.queries = queries;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task PublishAsync(RefinementTurn turn, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);

        try
        {
            await SendAsync(turn, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "Could not stream the refinement turn on request {RequestId}; the rows are written and a "
                + "reload will show it",
                turn.Request.Id);
        }
    }

    private async Task SendAsync(RefinementTurn turn, CancellationToken cancellationToken)
    {
        var request = turn.Request;

        // The whole thread, exactly as the detail endpoint derives it, so the frames below carry the
        // ids and bodies a refetch would carry rather than a second rendering of the same rows.
        var thread = RefinementThread.Derive(request, turn.Spec, turn.Conversation);
        var appended = turn.AppendedTurnIds.Select(id => id.ToString("N")).ToHashSet(StringComparer.Ordinal);

        foreach (var message in thread)
        {
            if (appended.Contains(message.Id))
            {
                await stream.PublishAsync(
                    request.Id,
                    RequestStreamEvents.RefinementMessage(message),
                    cancellationToken);
            }
        }

        // Section 10b: the requester's rendering of the spec, which is title, outcome, acceptance
        // criteria and open questions and nothing else. Both audiences get it, because a requester who
        // may not see the technical approach in a GET may not see it in a frame either — and the
        // projection is what withholds it, here as there.
        if (turn.Spec is not null && Spec(turn) is { } spec)
        {
            await stream.PublishAsync(request.Id, RequestStreamEvents.SpecProposed(spec), cancellationToken);
        }

        await stream.PublishAsync(
            request.Id,
            RequestStreamEvents.Status(request.Status.ToApi(), await AwaitingApprovalAsync(turn, cancellationToken)),
            cancellationToken);

        // The turn is over, whatever it produced. `SendRefinementMessageAsync` turned this on when the
        // reply was accepted and nothing else ever turned it off, so a thread whose refine job failed
        // used to sit with the indicator running forever.
        await stream.PublishAsync(
            request.Id,
            RequestStreamEvents.CharterThinking(thinking: false),
            cancellationToken);
    }

    /// <summary>
    /// Section 6: <em>Waiting on {approver} to approve</em>, and only there.
    /// </summary>
    /// <remarks>
    /// Resolved through the same query service the detail endpoint uses. A frame that left the name
    /// out would render "Waiting for approval" live and "Waiting on Tomas Beck" after a reload, which
    /// is precisely the disagreement this class exists to prevent.
    /// </remarks>
    private async Task<string?> AwaitingApprovalAsync(RefinementTurn turn, CancellationToken cancellationToken)
        => turn.Request.Status == RequestStatus.SpecReady
            ? await queries.ResolveApproverNameAsync(turn.Request.OrgId, cancellationToken)
            : null;

    private static RequesterSpecResponse? Spec(RefinementTurn turn)
        => RequestProjection.Spec(new RequestAggregate
        {
            Request = turn.Request,
            Repo = turn.Repo,
            Profile = RepoProjectProfile.For(turn.Repo),
            Spec = turn.Spec,
        });
}
