using System.Reflection;
using Charter.Api.Contracts;

namespace Charter.Tests;

/// <summary>
/// Section 6: <em>"Never show an ETA. Elapsed time only."</em>
/// </summary>
/// <remarks>
/// <para>
/// The rule is repeated in section 27.7 for the <c>pending</c> artifact card, which is exactly where
/// somebody would be most tempted to add one. It is enforced twice here: once over the whole contract
/// namespace by reflection, so a type nobody serialised in a test is still covered, and once over the
/// bytes of a real response.
/// </para>
/// <para>
/// A spend ceiling is not an ETA. <c>estimatedCostUsd</c> on the approval queue is money the session
/// may not exceed (section 7.5), not a prediction about when anything finishes, so the forbidden
/// patterns below are time-shaped rather than merely "estimated".
/// </para>
/// </remarks>
public class ApiNoEtaTests
{
    /// <summary>
    /// Fragments that only ever appear in the name of a predicted finish time.
    /// </summary>
    /// <remarks>
    /// <c>eta</c> itself is handled separately by <see cref="LooksLikeEta"/>, because it is a
    /// substring of <c>detail</c> and <c>details</c> and a naive contains-check would condemn two
    /// perfectly legitimate members of the section 27.7 card.
    /// </remarks>
    private static readonly string[] ForbiddenFragments =
    [
        "estimatedcompletion",
        "estimatedfinish",
        "estimatedduration",
        "estimatedtime",
        "expectedcompletion",
        "expectedfinish",
        "finishesat",
        "completesat",
        "willfinish",
        "timeremaining",
        "remainingtime",
        "remainingms",
        "timeleft",
        "predictedcompletion",
        "forecast",
    ];

    /// <summary>Whether a member or key name carries <c>eta</c> as a word rather than inside one.</summary>
    private static bool LooksLikeEta(string name)
        => System.Text.RegularExpressions.Regex.IsMatch(name, "(^|_|[a-z0-9])[Ee][Tt][Aa]([A-Z_]|$)");

    /// <summary>Every reason a name would fail this rule, or empty.</summary>
    private static string? Offence(string name)
    {
        if (LooksLikeEta(name))
        {
            return "it names an ETA";
        }

        var lowered = name.ToLowerInvariant();

        foreach (var fragment in ForbiddenFragments)
        {
            if (lowered.Contains(fragment, StringComparison.Ordinal))
            {
                return $"it contains `{fragment}`";
            }
        }

        return null;
    }

    public static TheoryData<Type> ContractTypes
    {
        get
        {
            var data = new TheoryData<Type>();

            foreach (var type in typeof(RequestDetailResponse).Assembly
                         .GetTypes()
                         .Where(type => type.Namespace == "Charter.Api.Contracts" && !type.IsEnum)
                         .OrderBy(type => type.FullName, StringComparer.Ordinal))
            {
                data.Add(type);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ContractTypes))]
    public void NoContractTypeCarriesAPredictedCompletionTime(Type type)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.True(
                Offence(property.Name) is null,
                $"{type.Name}.{property.Name}: {Offence(property.Name)}. Section 6 allows elapsed time only.");
        }
    }

    [Fact]
    public async Task ARealResponseBodyCarriesNoSuchKey()
    {
        var scenario = ApiScenario.Build();

        foreach (var member in new[] { scenario.Requester, scenario.Engineer })
        {
            var body = await scenario.RenderDetailAsync(member);

            foreach (var key in ApiPayloads.Keys(body))
            {
                Assert.True(
                    Offence(key) is null,
                    $"The response carries `{key}` — {Offence(key)} — which reads as a predicted completion time.");
            }
        }
    }

    [Fact]
    public async Task TheStatusThreadCarriesStartsAndEndsAndNothingForward()
    {
        var scenario = ApiScenario.Build();
        var body = await scenario.RenderDetailAsync(scenario.Requester);

        using var document = System.Text.Json.JsonDocument.Parse(body);
        var thread = document.RootElement.GetProperty("thread");

        // Elapsed time is computed by the client from `startedAt`. That is the only time basis the
        // contract offers, and it can only ever count upward.
        Assert.True(thread.TryGetProperty("startedAt", out _));

        // Every remaining timestamp on the thread names something that already happened. A session
        // still running has no `endedAt` at all rather than a guess at one.
        var timestamps = thread
            .EnumerateObject()
            .Select(property => property.Name)
            .Where(name => name.EndsWith("At", StringComparison.Ordinal))
            .ToList();

        Assert.All(timestamps, name => Assert.Contains(name, new[] { "startedAt", "endedAt" }));
    }

    [Fact]
    public void ThePendingArtifactCardHasNoTimeFieldBeyondItsExpiry()
    {
        // Section 27.7: `pending` renders a skeleton, an elapsed timer and the current milestone —
        // "Never an ETA". `expiresAt` counts down to a deletion Charter itself scheduled (section
        // 27.5); it is not a prediction about the agent.
        var names = typeof(VerificationArtifactResponse)
            .GetProperties()
            .Select(property => property.Name)
            .Where(name => name.EndsWith("At", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(["ExpiresAt"], names);
    }

    [Fact]
    public void TheApprovalQueuesOnlyEstimateIsMoney()
    {
        var estimated = typeof(PendingApprovalResponse)
            .GetProperties()
            .Where(property => property.Name.StartsWith("Estimated", StringComparison.Ordinal))
            .Select(property => property.Name)
            .ToList();

        Assert.Equal(["EstimatedCostUsd"], estimated);
        Assert.Equal(typeof(decimal), typeof(PendingApprovalResponse).GetProperty("EstimatedCostUsd")!.PropertyType);
    }
}
