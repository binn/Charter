using System.Text.Json;
using Charter.Api.Contracts;
using Charter.Api.Requests;
using Charter.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Charter.Tests;

/// <summary>
/// The realtime layer: idempotent frames (section 2.3) that respect section 7.4.
/// </summary>
public class HubStreamTests
{
    private static MilestoneResponse Milestone(string label, long? eventSeq = null) => new()
    {
        Id = "ms-1",
        Kind = ApiMilestoneKind.Changing,
        Label = label,
        OccurredAt = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero),
        State = ApiMilestoneState.Done,
        EventSeq = eventSeq,
    };

    [Fact]
    public void RepublishingTheSameContentProducesTheSameId()
    {
        // Section 2.3: the container can restart mid-session, the client resubscribes and replays.
        // The client dedupes by id, so republishing identical content must collide.
        var first = RequestStreamEvents.Milestone(Milestone("Making the changes"));
        var second = RequestStreamEvents.Milestone(Milestone("Making the changes"));

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public void AnIdIsNeverReusedForDifferentContent()
    {
        var before = RequestStreamEvents.Milestone(Milestone("Making the changes"));
        var after = RequestStreamEvents.Milestone(Milestone("Checking it works"));

        Assert.NotEqual(before.Id, after.Id);

        // The same milestone republished with a pane-2 link is different content, and gets a different
        // id, which is what stops a requester's cached frame masking an engineer's richer one.
        var linked = RequestStreamEvents.Milestone(Milestone("Making the changes", eventSeq: 48));
        Assert.NotEqual(before.Id, linked.Id);
    }

    [Fact]
    public void TheIdNamesItsFrameTypeAndItsSubject()
    {
        var frame = RequestStreamEvents.Milestone(Milestone("Making the changes"));

        Assert.StartsWith("milestone:ms-1:", frame.Id, StringComparison.Ordinal);

        // An update to a milestone is a different frame type over the same subject, so the two cannot
        // collide even when the milestone body is byte-identical.
        var updated = RequestStreamEvents.MilestoneUpdated(Milestone("Making the changes"));
        Assert.NotEqual(frame.Id, updated.Id);
    }

    [Fact]
    public void EveryFrameKindProducesAStableIdAndItsUnionTag()
    {
        var frames = new (RequestStreamEvent Frame, string Tag)[]
        {
            (RequestStreamEvents.Milestone(Milestone("a")), "milestone"),
            (RequestStreamEvents.MilestoneUpdated(Milestone("a")), "milestone_updated"),
            (RequestStreamEvents.Status(ApiRequestStatus.Queued), "status"),
            (RequestStreamEvents.RefinementMessage(new RefinementMessageResponse
            {
                Id = "m1",
                Author = ApiRefinementAuthor.Charter,
                Kind = ApiRefinementMessageKind.Message,
                Body = "hello",
                CreatedAt = DateTimeOffset.UnixEpoch,
            }), "refinement_message"),
            (RequestStreamEvents.CharterThinking(thinking: true), "charter_thinking"),
            (RequestStreamEvents.ArtifactState("art-1", ApiArtifactState.Pending), "artifact_state"),
            (RequestStreamEvents.Failed("This turned out to be bigger than expected."), "failed"),
            (RequestStreamEvents.Ended(DateTimeOffset.UnixEpoch), "ended"),
        };

        foreach (var (frame, tag) in frames)
        {
            Assert.Equal(tag, frame.Type);
            Assert.NotEmpty(frame.Id);
            Assert.StartsWith(tag + ":", frame.Id, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AFrameSerialisesAsTheUnionApplyStreamEventSwitchesOn()
    {
        var frame = RequestStreamEvents.Status(ApiRequestStatus.SpecReady, "Tomas Beck");
        var json = await ApiPayloads.RenderAsync(frame);

        using var document = JsonDocument.Parse(json);
        Assert.Equal("status", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("spec_ready", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("Tomas Beck", document.RootElement.GetProperty("awaitingApprovalFrom").GetString());
    }

    [Fact]
    public async Task AnAbsentOptionalIsAbsentOnTheStreamToo()
    {
        var json = await ApiPayloads.RenderAsync(RequestStreamEvents.Status(ApiRequestStatus.Queued));

        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("awaitingApprovalFrom", out _));
    }

    [Fact]
    public async Task ARequesterFrameCarriesNoPaneTwoLink()
    {
        // Section 7.4 on the stream: the requester group is sent a milestone with no eventSeq at all,
        // rather than one the client is trusted to ignore.
        var requesterFrame = RequestStreamEvents.Milestone(Milestone("Making the changes"));
        var engineerFrame = RequestStreamEvents.Milestone(Milestone("Making the changes", eventSeq: 48));

        Assert.DoesNotContain("eventSeq", ApiPayloads.Keys(await ApiPayloads.RenderAsync(requesterFrame)));
        Assert.Contains("eventSeq", ApiPayloads.Keys(await ApiPayloads.RenderAsync(engineerFrame)));
    }

    [Fact]
    public void TheTwoAudiencesAreDifferentGroups()
    {
        var requestId = Guid.CreateVersion7();

        Assert.NotEqual(RequestStreamGroups.Requester(requestId), RequestStreamGroups.Engineer(requestId));
        Assert.EndsWith(":requester", RequestStreamGroups.Requester(requestId), StringComparison.Ordinal);
        Assert.EndsWith(":engineer", RequestStreamGroups.Engineer(requestId), StringComparison.Ordinal);

        // Two requests never share a group.
        Assert.NotEqual(
            RequestStreamGroups.Requester(requestId),
            RequestStreamGroups.Requester(Guid.CreateVersion7()));
    }

    [Fact]
    public async Task ThePublisherSendsTheRequesterShapeToOneGroupAndTheEngineerShapeToTheOther()
    {
        var hub = new RecordingHubContext();
        var publisher = new RequestStreamPublisher(hub);
        var requestId = Guid.CreateVersion7();

        await publisher.PublishAsync(
            requestId,
            RequestStreamEvents.Milestone(Milestone("Making the changes")),
            RequestStreamEvents.Milestone(Milestone("Making the changes", eventSeq: 48)),
            TestContext.Current.CancellationToken);

        var requester = hub.Sends.Single(send => send.Group == RequestStreamGroups.Requester(requestId));
        var engineer = hub.Sends.Single(send => send.Group == RequestStreamGroups.Engineer(requestId));

        Assert.Equal(RequestStreamGroups.EventMethod, requester.Method);

        var requesterFrame = Assert.IsType<MilestoneStreamEvent>(requester.Argument);
        var engineerFrame = Assert.IsType<MilestoneStreamEvent>(engineer.Argument);

        Assert.Null(requesterFrame.Milestone.EventSeq);
        Assert.Equal(48, engineerFrame.Milestone.EventSeq);
    }

    [Fact]
    public async Task AnUndifferentiatedFrameGoesToBothGroupsUnchanged()
    {
        var hub = new RecordingHubContext();
        var publisher = new RequestStreamPublisher(hub);
        var requestId = Guid.CreateVersion7();

        await publisher.PublishAsync(requestId, RequestStreamEvents.Ended(DateTimeOffset.UnixEpoch), TestContext.Current.CancellationToken);

        Assert.Equal(2, hub.Sends.Count);
        Assert.Single(hub.Sends.Select(send => send.Argument).Distinct());
    }

    [Fact]
    public void TheHubRefusesWithOneSentenceForGoneAndForNotYours()
    {
        // The message must not distinguish the two cases, for the same reason the 404 does not
        // (section 7.3).
        Assert.Contains("may belong to someone else", RequestHub.SubscriptionRefused, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", RequestHub.SubscriptionRefused, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHubAuthorisesThroughTheSameServicesTheRestLayerUses()
    {
        // Not a style point: a hub with its own lookup is a second authorisation path, and the two
        // drift the first time one of them is edited.
        var parameters = typeof(RequestHub)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToList();

        Assert.Contains(typeof(Charter.Auth.Authorization.ICharterAuthorizationService), parameters);
        Assert.Contains(typeof(RequestQueryService), parameters);
    }

    [Fact]
    public void TheHubRequiresAnAuthenticatedConnection()
    {
        Assert.NotEmpty(typeof(RequestHub)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true));
    }

    private sealed record Send(string Group, string Method, object? Argument);

    private sealed class RecordingHubContext : IHubContext<RequestHub>
    {
        private readonly RecordingClients clients = new();

        public List<Send> Sends => clients.Sends;

        public IHubClients Clients => clients;

        public IGroupManager Groups { get; } = new NoGroups();

        private sealed class RecordingClients : IHubClients
        {
            public List<Send> Sends { get; } = [];

            public IClientProxy All => new RecordingProxy(Sends, "all");

            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => All;

            public IClientProxy Client(string connectionId) => new RecordingProxy(Sends, connectionId);

            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => All;

            public IClientProxy Group(string groupName) => new RecordingProxy(Sends, groupName);

            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds)
                => Group(groupName);

            public IClientProxy Groups(IReadOnlyList<string> groupNames) => All;

            public IClientProxy User(string userId) => new RecordingProxy(Sends, userId);

            public IClientProxy Users(IReadOnlyList<string> userIds) => All;
        }

        private sealed class RecordingProxy : IClientProxy
        {
            private readonly List<Send> sends;
            private readonly string target;

            public RecordingProxy(List<Send> sends, string target)
            {
                this.sends = sends;
                this.target = target;
            }

            public Task SendCoreAsync(
                string method,
                object?[] args,
                CancellationToken cancellationToken = default)
            {
                sends.Add(new Send(target, method, args.Length == 0 ? null : args[0]));
                return Task.CompletedTask;
            }
        }

        private sealed class NoGroups : IGroupManager
        {
            public Task AddToGroupAsync(
                string connectionId,
                string groupName,
                CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task RemoveFromGroupAsync(
                string connectionId,
                string groupName,
                CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }
}
