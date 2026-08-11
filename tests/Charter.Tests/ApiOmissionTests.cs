using System.Text.Json;
using Charter.Api.Contracts;
using Charter.Api.Requests;

namespace Charter.Tests;

/// <summary>
/// Section 7.4, checked on the wire.
/// </summary>
/// <remarks>
/// <para>
/// <em>"Authorisation is not a rendering concern."</em> These tests read the raw response body — the
/// bytes <see cref="System.Text.Json.JsonSerializer"/> actually produced — and assert that the keys
/// a requester may not see <strong>are not in it</strong>. Deserialising into
/// <c>RequestDetailResponse</c> and asserting <c>Transcript is null</c> would pass just as happily
/// for a body containing <c>"transcript": null</c>, and that body would be a leak of the fact that a
/// transcript exists at all.
/// </para>
/// <para>
/// The visibility driving the projection comes from
/// <see cref="Charter.Auth.Authorization.SessionVisibilityPolicy"/> through
/// <see cref="RequestVisibility"/> — the same call the endpoint makes. There is no second rule being
/// tested here.
/// </para>
/// </remarks>
public class ApiOmissionTests
{
    /// <summary>Every key section 7.4 and section 27.7 withhold from a viewer without repo read.</summary>
    public static TheoryData<string> EngineerOnlyKeys =>
    [
        "transcript",
        "changes",
        "details",
        "changeRequestNumber",
        "changeRequestUrl",
        "commitSha",
        "branch",
        "runner",
        "durationMs",
        "costUsd",
        "eventSeq",
        "technicalApproach",
        "scope",
        "risks",

        // Section 14 and section 7.5, added with panes 2 and 3 and withheld on the same terms. The
        // recap names files, branches and deviations; the actions panel names the branch it would
        // stop writes to. Both are engineer-shaped all the way down, which is why their nested keys
        // are listed too — `ApiPayloads.Keys` walks the whole document, so a recap smuggled inside
        // some other property would still fail this.
        "recap",
        "sessionActions",
        "deviations",
        "couldNotVerify",
        "reviewOrder",
        "autoDispatched",
        "riskReasons",
        "canTakeOver",
        "handedOff",
    ];

    [Theory]
    [MemberData(nameof(EngineerOnlyKeys))]
    public async Task ARequestersBodyDoesNotCarryTheKeyAtAll(string key)
    {
        var scenario = ApiScenario.Build();
        var body = await scenario.RenderDetailAsync(scenario.Requester);

        Assert.DoesNotContain(key, ApiPayloads.Keys(body));
    }

    [Fact]
    public async Task ARequestersBodyDoesNotCarryTheCommitShaOrTheCostAsValuesEither()
    {
        var scenario = ApiScenario.Build();
        var body = await scenario.RenderDetailAsync(scenario.Requester);

        // Section 7.1: "a requester never sees a SHA". Checked against the text, not the shape, so a
        // SHA smuggled into a summary string would fail this too.
        Assert.DoesNotContain(ApiScenario.CommitSha, body, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiScenario.TechnicalApproach, body, StringComparison.Ordinal);
        Assert.DoesNotContain("1.42", body, StringComparison.Ordinal);

        // Nor the repository's own name (section 7.1: never `owner/repo`).
        Assert.DoesNotContain("northbeam/quote-tool", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEngineersBodyCarriesAllOfIt()
    {
        var scenario = ApiScenario.Build();
        var body = await scenario.RenderDetailAsync(scenario.Engineer);
        var keys = ApiPayloads.Keys(body);

        Assert.Contains("transcript", keys);
        Assert.Contains("changes", keys);
        Assert.Contains("details", keys);
        Assert.Contains("commitSha", keys);
        Assert.Contains("costUsd", keys);
        Assert.Contains("eventSeq", keys);
        Assert.Contains("recap", keys);
        Assert.Contains("sessionActions", keys);
        Assert.Contains(ApiScenario.CommitSha, body, StringComparison.Ordinal);
        Assert.Contains(ApiScenario.HeadBranch, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRecapAndTheActionPanelAreAbsentKeysRatherThanNullOnes()
    {
        // The rule this suite exists for, applied to the two properties added with panes 2 and 3.
        // A deserialiser cannot tell absent from null, so this reads the document.
        var scenario = ApiScenario.Build();

        using var requester = JsonDocument.Parse(await scenario.RenderDetailAsync(scenario.Requester));
        Assert.False(requester.RootElement.TryGetProperty("recap", out _));
        Assert.False(requester.RootElement.TryGetProperty("sessionActions", out _));

        using var engineer = JsonDocument.Parse(await scenario.RenderDetailAsync(scenario.Engineer));
        Assert.True(engineer.RootElement.TryGetProperty("recap", out var recap));
        Assert.True(engineer.RootElement.TryGetProperty("sessionActions", out var actions));

        // And they are usable, not merely present.
        Assert.NotEmpty(recap.GetProperty("summaryMd").GetString()!);
        Assert.Equal(ApiScenario.HeadBranch, actions.GetProperty("branch").GetString());
    }

    [Fact]
    public async Task TheRecapNeverReachesARequesterEvenAsText()
    {
        // Section 14's recap is prose, so the key check is not enough on its own: a summary leaking
        // into some requester-facing string would keep every key test green.
        var scenario = ApiScenario.Build();
        var body = await scenario.RenderDetailAsync(scenario.Requester);

        Assert.DoesNotContain("src/Auth/LoginController.cs", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Session recap", body, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiScenario.HeadBranch, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheEngineersTranscriptCarriesTheLinkageSectionTwelveNeeds()
    {
        // Omission must not be over-applied in the other direction either: an engineer's pane 2 is
        // useless without `kind`, the milestone run marking and a total to page against.
        var scenario = ApiScenario.Build();

        using var document = JsonDocument.Parse(await scenario.RenderDetailAsync(scenario.Engineer));
        var transcript = document.RootElement.GetProperty("transcript");

        Assert.Equal(scenario.Events.Count, transcript.GetProperty("totalCount").GetInt32());

        var write = transcript
            .GetProperty("events")
            .EnumerateArray()
            .Single(row => row.GetProperty("kind").GetString() == "file_write");

        Assert.Equal("src/Auth/LoginController.cs", write.GetProperty("path").GetString());
        Assert.NotNull(write.GetProperty("milestoneId").GetString());

        // The adapter's own event name travels verbatim beside the projection (section 12b).
        Assert.Equal("file_write", write.GetProperty("type").GetString());

        // `level` is absent on an ordinary row rather than stamped `info` on every one of them.
        Assert.False(write.TryGetProperty("level", out _));
    }

    [Fact]
    public async Task TheOmissionIsAbsenceRatherThanANullProperty()
    {
        var scenario = ApiScenario.Build();
        var body = await scenario.RenderDetailAsync(scenario.Requester);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        // `TryGetProperty` returning false is the assertion. A JSON null would return true.
        Assert.False(root.TryGetProperty("transcript", out _));
        Assert.False(root.TryGetProperty("changes", out _));

        var artifact = root.GetProperty("artifacts")[0];
        Assert.False(artifact.TryGetProperty("details", out _));

        // And the artifact is still fully usable — omission removed the engineer block, not the card.
        Assert.Equal("hosted_preview", artifact.GetProperty("kind").GetString());
        Assert.True(artifact.GetProperty("primary").GetBoolean());
        Assert.NotNull(artifact.GetProperty("payload").GetProperty("url").GetString());
    }

    [Fact]
    public async Task TheRequesterSpecIsTheRefinementRequesterViewAndCarriesNothingElse()
    {
        var scenario = ApiScenario.Build();
        var body = await scenario.RenderDetailAsync(scenario.Requester);

        using var document = JsonDocument.Parse(body);
        var spec = document.RootElement.GetProperty("spec");

        Assert.Equal("Remember the last selected vertical", spec.GetProperty("title").GetString());
        Assert.Equal(2, spec.GetProperty("acceptanceCriteria").GetArrayLength());

        // Section 10b: the engineer-facing fields are not members of the requester type at all.
        foreach (var forbidden in new[] { "technicalApproach", "scope", "risks" })
        {
            Assert.False(spec.TryGetProperty(forbidden, out _));
        }

        // Nothing is open on a confirmable spec, and the key is then absent rather than an empty
        // array — the client's test is presence, never length.
        Assert.False(spec.TryGetProperty("openQuestions", out _));
    }

    [Fact]
    public async Task ARequesterIsToldWhatIsStillOpenRatherThanJustThatSomethingIs()
    {
        // Section 10b lists open_questions on the structured spec and then says the requester view
        // renders "nothing else"; the two cannot both hold. They are resolved in favour of the
        // requester, because open questions are the one field on a spec addressed *to* them, they are
        // what blocks the confirm button, and withholding them leaves a person looking at a disabled
        // button with no way to find out why. Section 10b was amended to match.
        var scenario = ApiScenario.Build(openQuestions: ["Should archived quotes remember it too?"]);
        var body = await scenario.RenderDetailAsync(scenario.Requester);

        using var document = JsonDocument.Parse(body);
        var spec = document.RootElement.GetProperty("spec");

        var open = spec.GetProperty("openQuestions").EnumerateArray().Select(item => item.GetString()).ToList();
        Assert.Equal(["Should archived quotes remember it too?"], open);

        // And it is still not a hole in section 7.4 — nothing engineer-facing came with it.
        Assert.False(spec.TryGetProperty("technicalApproach", out _));
        Assert.False(spec.TryGetProperty("risks", out _));
        Assert.DoesNotContain(ApiScenario.TechnicalApproach, body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRequesterSpecTypeHasNoEngineerPropertyToLeakInTheFirstPlace()
    {
        // An optional property invites `spec.technicalApproach && …` on the client, which is the
        // CSS-hiding pattern section 7.4 forbids. The strongest guarantee is that there is nothing to
        // read: this fails at compile time for anyone who adds one, and here for anyone who does it
        // by reflection-friendly means.
        var properties = typeof(RequesterSpecResponse).GetProperties().Select(property => property.Name).ToList();

        Assert.DoesNotContain("TechnicalApproach", properties);
        Assert.DoesNotContain("Scope", properties);
        Assert.DoesNotContain("Risks", properties);
        Assert.DoesNotContain("BodyMd", properties);
    }

    [Fact]
    public async Task TheAcceptanceCriteriaAreTheSameTextInBothRenderings()
    {
        // Section 10b: they are the contract, shared verbatim. The requester view returns the engineer
        // view's own list instance, so this is really asserting that nothing re-wrote them on the way
        // out of the projection.
        var scenario = ApiScenario.Build();
        var document = Charter.Refinement.SpecDocumentMapper.ToDocument(scenario.Spec);

        var body = await scenario.RenderDetailAsync(scenario.Requester);
        using var parsed = JsonDocument.Parse(body);

        var wire = parsed.RootElement
            .GetProperty("spec")
            .GetProperty("acceptanceCriteria")
            .EnumerateArray()
            .Select(item => item.GetProperty("text").GetString()!)
            .ToList();

        Assert.Equal(document.ForEngineer().AcceptanceCriteria, wire);
        Assert.Equal(document.ForRequester().AcceptanceCriteria, wire);
    }

    [Fact]
    public async Task AMemberOfAnotherOrganisationIsProjectedNothingAtAll()
    {
        var scenario = ApiScenario.Build();
        var visibility = scenario.VisibilityFor(scenario.Outsider);

        Assert.True(visibility.IsEmpty);

        // The endpoint turns an empty visibility into a 404 before projecting, so what follows is the
        // belt to that braces: even projected, nothing engineer-shaped comes out.
        var body = await ApiPayloads.RenderAsync(
            RequestProjection.Detail(scenario.AggregateFor(scenario.Outsider), visibility, DateTimeOffset.UtcNow));

        var keys = ApiPayloads.Keys(body);
        Assert.DoesNotContain("transcript", keys);
        Assert.DoesNotContain("details", keys);
    }

    [Fact]
    public async Task TheCancelButtonIsOfferedOnlyWhileThereIsARunnerToKill()
    {
        // Section 11: "Must actually kill the runner and settle token cost." A preview waiting to be
        // tried has nothing left to stop, and offering the button there would be a lie.
        var scenario = ApiScenario.Build();
        var body = await scenario.RenderDetailAsync(scenario.Requester);

        using var settled = JsonDocument.Parse(body);
        Assert.False(settled.RootElement.GetProperty("cancellable").GetBoolean());
        Assert.False(settled.RootElement.GetProperty("thread").GetProperty("live").GetBoolean());

        var running = ApiScenario.Build();
        running.Session.TransitionTo(Charter.Domain.SessionStatus.Running);

        using var mid = JsonDocument.Parse(await running.RenderDetailAsync(running.Requester));
        Assert.True(mid.RootElement.GetProperty("cancellable").GetBoolean());
        Assert.True(mid.RootElement.GetProperty("thread").GetProperty("live").GetBoolean());
    }

    [Fact]
    public async Task TheSessionBodyARequesterGetsCarriesNoCredentialAndNoRepository()
    {
        // Sections 21 and 7.1, swept the same way as the request detail: a requester reaches
        // `/api/auth/session` on every page load, so it goes through the raw-body check too.
        var session = new SessionResponse
        {
            UserId = Guid.CreateVersion7().ToString(),
            DisplayName = "Ayesha Rahman",
            Email = "ayesha@example.com",
            OrganizationId = Guid.CreateVersion7().ToString(),
            Roles = [ApiRole.Requester],
            Provider = "password",
        };

        var body = await ApiPayloads.RenderAsync(session);
        var keys = ApiPayloads.Keys(body);

        foreach (var forbidden in new[] { "password", "passwordHash", "secret", "token", "repo", "repository" })
        {
            Assert.DoesNotContain(forbidden, keys, StringComparer.OrdinalIgnoreCase);
        }

        // What it does carry is what the shell needs and nothing beyond it.
        Assert.Equal(["userId", "displayName", "email", "organizationId", "roles", "provider"], [.. keys]);
    }

    [Fact]
    public async Task ARequesterStillGetsTheThingsSectionElevenPromisesThem()
    {
        var scenario = ApiScenario.Build();
        var body = await scenario.RenderDetailAsync(scenario.Requester);
        var keys = ApiPayloads.Keys(body);

        // Omission must not be over-applied: the status thread, the milestones, the spec and the
        // artifact card are the requester's whole product.
        Assert.Contains("thread", keys);
        Assert.Contains("milestones", keys);
        Assert.Contains("spec", keys);
        Assert.Contains("artifacts", keys);
        Assert.Contains("acceptanceCriteria", keys);

        using var document = JsonDocument.Parse(body);
        Assert.Equal("Quote tool", document.RootElement.GetProperty("projectName").GetString());
        Assert.NotEmpty(document.RootElement.GetProperty("thread").GetProperty("milestones").EnumerateArray());
    }
}
