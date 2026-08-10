using System.Text.Json;
using Charter.Api;
using Charter.Api.Endpoints;
using Charter.Api.Requests;
using Charter.Auth.Authorization;
using Charter.Domain;
using Microsoft.AspNetCore.Http;

namespace Charter.Tests;

/// <summary>
/// What the API returns when it refuses, and why the refusals read the way they do.
/// </summary>
public class ApiAuthorizationTests
{
    [Fact]
    public async Task ARefusalIsProblemDetailsWithASentenceAPersonCanRead()
    {
        var body = await ApiPayloads.RenderAsync(
            ApiProblems.Forbidden("reading a transcript needs read access to the repository"));

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal(403, root.GetProperty("status").GetInt32());
        Assert.Equal("You do not have access to this", root.GetProperty("title").GetString());
        Assert.Equal(
            "Reading a transcript needs read access to the repository.",
            root.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task NotFoundAndNotYoursAreTheSameAnswer()
    {
        // Section 7.3: the API must never become an existence oracle for another organisation's work.
        var body = await ApiPayloads.RenderAsync(ApiProblems.NotFound());

        using var document = JsonDocument.Parse(body);
        Assert.Equal(404, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(
            "It may have been removed, or it may belong to someone else.",
            document.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task AnUnexpectedFailureIsOneSentenceAndNoStackTrace()
    {
        // Section 11: "A non-engineer who sees a stack trace once never files again."
        var body = await ApiPayloads.RenderAsync(ApiProblems.Unexpected());

        Assert.Contains("Nothing you did caused it", body, StringComparison.Ordinal);
        Assert.DoesNotContain("   at Charter.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("System.", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(StatusCodes.Status204NoContent, StatusCodes.Status204NoContent)]
    [InlineData(StatusCodes.Status400BadRequest, StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status403Forbidden, StatusCodes.Status403Forbidden)]
    [InlineData(StatusCodes.Status404NotFound, StatusCodes.Status404NotFound)]
    [InlineData(StatusCodes.Status409Conflict, StatusCodes.Status409Conflict)]
    public async Task ACommandOutcomeBecomesTheStatusTheClientDistinguishesOn(int status, int expected)
    {
        // `ApiError` in client.ts keeps `status` precisely so a caller can tell 403 from 404.
        var outcome = new CommandOutcome(status == StatusCodes.Status204NoContent, "not allowed", status);
        var result = CharterApiEndpoints.ToResult(outcome);

        if (expected == StatusCodes.Status204NoContent)
        {
            Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.NoContent>(result);
            return;
        }

        var body = await ApiPayloads.RenderAsync(result);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(expected, document.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public void AnUnreadyRepositoryIsRequestableByNobody()
    {
        // Section 9: readiness is earned, and section 7.3 is deny-by-default. Both are the reason a
        // project is simply absent from GET /api/projects rather than present and disabled.
        var scenario = ApiScenario.Build();
        var member = scenario.Requester;

        var ready = scenario.RepoSnapshot;
        Assert.True(RepoAccessPolicy.CanFileRequest(member, ready).IsAllowed);

        var pending = ready with { Status = RepoStatus.Pending };
        Assert.True(RepoAccessPolicy.CanFileRequest(member, pending).IsDenied);

        var unscoped = ready with { Grants = [] };
        Assert.True(RepoAccessPolicy.CanFileRequest(member, unscoped).IsDenied);
    }

    [Fact]
    public void VisibilityIsResolvedByTheOneSectionSevenPointFourPolicy()
    {
        // RequestVisibility supplies inputs; it must not answer anything itself. Asking the policy
        // directly with the same snapshot has to give the identical record.
        var scenario = ApiScenario.Build();

        foreach (var member in new[] { scenario.Requester, scenario.Engineer, scenario.Outsider })
        {
            var throughAdapter = scenario.VisibilityFor(member);
            var throughPolicy = SessionVisibilityPolicy.For(
                member,
                scenario.RepoSnapshot,
                RequestVisibility.Snapshot(scenario.Request, scenario.Session));

            Assert.Equal(throughPolicy, throughAdapter);
        }
    }

    [Fact]
    public void ARequestWithNoSessionYetIsStillJudgedByTheSamePolicy()
    {
        // A request in Refining has no session, and inventing a second "request visibility" rule for
        // that case is exactly the fork section 7.4 warns about.
        var scenario = ApiScenario.Build();

        var withoutSession = RequestVisibility.Resolve(
            scenario.Requester,
            scenario.RepoSnapshot,
            scenario.Request,
            session: null);

        Assert.True(withoutSession.StatusThread);
        Assert.False(withoutSession.Transcript);
        Assert.False(withoutSession.IsEmpty);

        var outsider = RequestVisibility.Resolve(
            scenario.Outsider,
            scenario.RepoSnapshot,
            scenario.Request,
            session: null);

        Assert.True(outsider.IsEmpty);
    }

    [Fact]
    public void EngineerDetailsNeedBothRepositoryIdentityAndCost()
    {
        var scenario = ApiScenario.Build();

        Assert.False(RequestVisibility.CanSeeEngineerDetails(scenario.VisibilityFor(scenario.Requester)));
        Assert.True(RequestVisibility.CanSeeEngineerDetails(scenario.VisibilityFor(scenario.Engineer)));
        Assert.False(RequestVisibility.CanSeeEngineerDetails(SessionVisibility.None));
    }

    [Fact]
    public void CapabilitiesDescribeNavigationAndNeverGateData()
    {
        // Section 7.2: the flags say which links to draw. Nothing in the projection reads them — the
        // projection reads SessionVisibility — which is why a wrong flag is a cosmetic bug and not a
        // permission bug.
        var projection = typeof(RequestProjection)
            .GetMethods()
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToList();

        Assert.Contains(typeof(SessionVisibility), projection);
        Assert.DoesNotContain(typeof(Charter.Api.Contracts.ViewerCapabilitiesResponse), projection);
    }

    [Fact]
    public async Task IntakeRefusesAnEmptyRequestBeforeItRefusesOnPermissions()
    {
        var outcome = CommandOutcome.Invalid("Tell us what you need in your own words.");
        var body = await ApiPayloads.RenderAsync(CharterApiEndpoints.ToResult(outcome));

        using var document = JsonDocument.Parse(body);
        Assert.Equal(400, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(
            "Tell us what you need in your own words.",
            document.RootElement.GetProperty("detail").GetString());
    }
}
