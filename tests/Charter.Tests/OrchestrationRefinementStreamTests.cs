using System.Text.Json;
using Charter.Api.Contracts;
using Charter.Api.Requests;
using Charter.Domain;

namespace Charter.Tests;

/// <summary>
/// Section 11's streaming rule, applied to the first thing a requester ever experiences.
/// </summary>
/// <remarks>
/// <para>
/// <em>"But <strong>do</strong> stream something — a 5–20 minute silent gap reads as broken."</em>
/// Refinement is entirely model-paced and it is the front of the loop, so it is the gap that matters
/// most. <c>RefineJobHandler</c> wrote correct rows and published nothing, which meant the thread only
/// moved when somebody reloaded — and a requester who has just typed the first thing they ever asked
/// Charter for is exactly the person who will not reload, they will close the tab.
/// </para>
/// <para>
/// What is asserted here is not that <em>some</em> frames went out. It is that the frames and a
/// reload <strong>agree</strong>: every published message and the proposed spec are compared, as
/// serialised bytes, against what <c>GET /api/requests/{id}</c> returns for the same request. That is
/// the property the layering decision exists to buy — the frames come out of
/// <see cref="RefinementThread"/> and <see cref="RequestProjection"/>, the same functions the read
/// path uses, rather than out of a second rendering kept in step by convention.
/// </para>
/// </remarks>
public class OrchestrationRefinementStreamTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryRefinementTurnStreamsAndWhatItStreamsIsWhatAReloadReturns()
    {
        await using var world = await PhaseOneWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var (filed, requestId) = await world.Commands().CreateAsync(
            world.Requester,
            new CreateRequestBody
            {
                ProjectId = world.RepoId.ToString(),
                RawText = "every time i start a new quote it makes me pick solar again",
            },
            Token);

        Assert.True(filed.Succeeded);

        // ── turn one: the refiner refuses to guess and asks ──────────────────────────────────────
        world.Stream.Clear();
        Assert.Equal(1, await world.RunQueueAsync(Token));

        var opening = world.Stream.RequesterFramesOf<RefinementMessageStreamEvent>().ToList();

        // The requester's own words and Charter's question, both, on the turn that produced them.
        Assert.Equal(2, opening.Count);
        Assert.Contains(opening, frame => frame.Message.Author == ApiRefinementAuthor.Requester);
        Assert.Contains(opening, frame => frame.Message.Kind == ApiRefinementMessageKind.Question);

        // Section 6: no ETA, ever. The typing indicator is the only progress signal, and the turn
        // being over is what turns it off — nothing else ever did, so a thread whose refine job failed
        // used to sit with it running.
        var thinking = Assert.Single(world.Stream.RequesterFramesOf<CharterThinkingStreamEvent>());
        Assert.False(thinking.Thinking);

        Assert.Equal(
            ApiRequestStatus.Refining,
            Assert.Single(world.Stream.RequesterFramesOf<StatusStreamEvent>()).Status);

        await AssertAgreesWithReloadAsync(world, requestId);

        // ── turn two: the answer unblocks the spec ───────────────────────────────────────────────
        world.Stream.Clear();

        var replied = await world.Commands().SendRefinementMessageAsync(
            world.Requester,
            requestId,
            new SendRefinementMessageBody { Body = "no, only new ones" },
            Token);

        Assert.True(replied.Succeeded);

        // The API's own half of the same rule: the reply echoes immediately and the indicator goes on,
        // so the box does not look like it swallowed what was typed.
        Assert.True(Assert.Single(world.Stream.RequesterFramesOf<CharterThinkingStreamEvent>()).Thinking);

        world.Stream.Clear();
        Assert.Equal(1, await world.RunQueueAsync(Token));

        // Section 10b: the requester's rendering of the spec, streamed the moment it exists rather
        // than on the next reload.
        Assert.Single(world.Stream.RequesterFramesOf<SpecProposedStreamEvent>());

        var status = Assert.Single(world.Stream.RequesterFramesOf<StatusStreamEvent>());
        Assert.Equal(ApiRequestStatus.SpecReady, status.Status);

        // Section 6: "Waiting on {approver} to approve". A frame without the name would render
        // "Waiting for approval" live and something else after a reload.
        Assert.Equal("Tomas Beck", status.AwaitingApprovalFrom);

        await AssertAgreesWithReloadAsync(world, requestId);
    }

    [Fact]
    public async Task NoRequesterFrameCarriesAShaARepositoryOrAnEngineerOnlyKey()
    {
        await using var world = await PhaseOneWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var (_, requestId) = await world.Commands().CreateAsync(
            world.Requester,
            new CreateRequestBody
            {
                ProjectId = world.RepoId.ToString(),
                RawText = "every time i start a new quote it makes me pick solar again",
            },
            Token);

        await world.RefineAsync(requestId, Token);
        await world.Commands().ApproveSpecAsync(world.Approver, requestId, version: 1, Token);
        await world.RunQueueAsync(Token);

        var sessionId = await world.SessionIdAsync(requestId, Token);
        await world.RunnerReportsAsync(sessionId, Token);

        // Section 7.4 on the wire. Every frame a viewer without repository read received, rendered
        // exactly as SignalR would write it, checked for the fields section 7.1 says they never see.
        foreach (var frame in world.Stream.RequesterFrames)
        {
            var json = await ApiPayloads.RenderAsync(frame);
            var keys = ApiPayloads.Keys(json);

            foreach (var forbidden in new[]
            {
                "technicalApproach", "headSha", "eventSeq", "costUsd", "transcript", "changes",
                "details", "recap", "branch", "runner",
            })
            {
                Assert.DoesNotContain(forbidden, keys);
            }

            Assert.DoesNotContain(PhaseOneWorld.HeadSha, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(PhaseOneWorld.BaseSha, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(PhaseOneWorld.RepoFullName, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(sessionId.ToString("D"), json, StringComparison.OrdinalIgnoreCase);
        }

        // And the control: the frames were not empty, so the loop above is not vacuously green.
        Assert.NotEmpty(world.Stream.RequesterFrames);
    }

    [Fact]
    public async Task RefinementStillCompletesWhenTheHubIsUnreachable()
    {
        await using var world = await PhaseOneWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        // Section 2.3: the rows are the record and the stream is a courtesy on top. A hub that throws
        // must not fail the turn — the requester would get a retried model call instead of a thread
        // that is slightly late.
        world.Stream.FailEverything = true;

        var (_, requestId) = await world.Commands().CreateAsync(
            world.Requester,
            new CreateRequestBody
            {
                ProjectId = world.RepoId.ToString(),
                RawText = "every time i start a new quote it makes me pick solar again",
            },
            Token);

        Assert.Equal(1, await world.RunQueueAsync(Token));
        Assert.Equal(RequestStatus.Refining, await world.RequestStatusAsync(requestId));

        var conversation = await world.ClarifyingQuestionsAsync(requestId, Token);
        Assert.NotEmpty(conversation);
    }

    /// <summary>
    /// Every streamed message and spec, compared as bytes against what a refetch would return.
    /// </summary>
    private static async Task AssertAgreesWithReloadAsync(PhaseOneWorld world, Guid requestId)
    {
        var view = await world.Queries().LoadAsync(world.Requester, requestId, Token);
        Assert.NotNull(view);

        var detail = RequestProjection.Detail(view.Aggregate, view.Visibility, DateTimeOffset.UtcNow);

        var reloaded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in detail.Refinement.Messages)
        {
            reloaded[message.Id] = await ApiPayloads.RenderAsync(message);
        }

        var streamed = world.Stream.RequesterFramesOf<RefinementMessageStreamEvent>().ToList();
        Assert.NotEmpty(streamed);

        foreach (var frame in streamed)
        {
            // The id has to be one a reload also produces, or the client's dedupe-by-id would show the
            // same sentence twice: once live and once on the next load.
            Assert.True(
                reloaded.ContainsKey(frame.Message.Id),
                $"The live thread carried message '{frame.Message.Id}', which a reload does not.");

            Assert.Equal(reloaded[frame.Message.Id], await ApiPayloads.RenderAsync(frame.Message));
        }

        foreach (var frame in world.Stream.RequesterFramesOf<SpecProposedStreamEvent>())
        {
            Assert.NotNull(detail.Spec);
            Assert.Equal(
                await ApiPayloads.RenderAsync(detail.Spec),
                await ApiPayloads.RenderAsync(frame.Spec));
        }

        foreach (var frame in world.Stream.RequesterFramesOf<StatusStreamEvent>())
        {
            Assert.Equal(detail.Status, frame.Status);
        }

        // A frame is safe to apply twice (section 2.3), and the id is what makes that checkable.
        var ids = world.Stream.RequesterFrames.Select(frame => frame.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheSeamOrchestrationDependsOnIsDeclaredInRowsRatherThanWireShapes()
    {
        // The layering decision, pinned. `Charter.Orchestration` must not learn `Charter.Api.Contracts`
        // to satisfy section 11 — the seam carries domain entities and the API renders them, so the
        // dependency is inverted rather than moved.
        foreach (var property in typeof(Charter.Orchestration.RefinementTurn).GetProperties())
        {
            Assert.DoesNotContain(
                "Charter.Api",
                property.PropertyType.FullName ?? string.Empty,
                StringComparison.Ordinal);
        }

        var publish = typeof(Charter.Orchestration.IRefinementStream)
            .GetMethod(nameof(Charter.Orchestration.IRefinementStream.PublishAsync));

        Assert.NotNull(publish);
        Assert.Equal(typeof(Charter.Orchestration.RefinementTurn), publish.GetParameters()[0].ParameterType);
    }
}
