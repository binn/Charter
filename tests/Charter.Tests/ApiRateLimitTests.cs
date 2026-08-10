using System.Security.Claims;
using System.Threading.RateLimiting;
using Charter.Api;
using Charter.Auth;
using Microsoft.AspNetCore.Http;

namespace Charter.Tests;

/// <summary>
/// Section 31: <em>"Rate limiting at intake — per user and per org. An enthusiastic requester with a
/// script should not be able to queue 400 sessions."</em>
/// </summary>
/// <remarks>
/// The limiter is driven directly rather than through a server, because what is being asserted is
/// the partitioning: that the two dimensions are enforced <em>together</em>. A per-user limit alone
/// lets ten accounts do what one was stopped from doing; a per-org limit alone lets one person
/// consume the whole team's allowance. Both failures are invisible to a test that only counts
/// rejections.
/// </remarks>
public class ApiRateLimitTests
{
    private static HttpContext Intake(Guid userId, Guid orgId)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(CharterClaimTypes.UserId, userId.ToString()),
                    new Claim(CharterClaimTypes.OrgId, orgId.ToString()),
                ],
                CharterAuthenticationDefaults.Scheme)),
        };

        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(IntakeRateLimitMetadata.Instance),
            "intake"));

        return context;
    }

    private static HttpContext Reading(Guid userId, Guid orgId)
    {
        var context = Intake(userId, orgId);
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "read"));
        return context;
    }

    private static int Acquire(PartitionedRateLimiter<HttpContext> limiter, HttpContext context, int attempts)
    {
        var granted = 0;

        for (var index = 0; index < attempts; index++)
        {
            using var lease = limiter.AttemptAcquire(context);
            if (lease.IsAcquired)
            {
                granted++;
            }
        }

        return granted;
    }

    [Fact]
    public void OnePersonRunningAScriptIsStoppedAtTheirOwnCeiling()
    {
        using var limiter = CharterRateLimiting.CreateIntakeLimiter(new CharterRateLimits
        {
            PerUserPerMinute = 3,
            PerUserPerHour = 100,
            PerOrgPerMinute = 100,
            PerOrgPerHour = 100,
        });

        var context = Intake(Guid.CreateVersion7(), Guid.CreateVersion7());

        Assert.Equal(3, Acquire(limiter, context, attempts: 40));
    }

    [Fact]
    public void TenAccountsCannotDoWhatOneWasStoppedFromDoing()
    {
        // The per-organisation dimension is the one that makes the per-user limit mean anything.
        using var limiter = CharterRateLimiting.CreateIntakeLimiter(new CharterRateLimits
        {
            PerUserPerMinute = 100,
            PerUserPerHour = 100,
            PerOrgPerMinute = 4,
            PerOrgPerHour = 100,
        });

        var orgId = Guid.CreateVersion7();
        var granted = 0;

        for (var person = 0; person < 10; person++)
        {
            granted += Acquire(limiter, Intake(Guid.CreateVersion7(), orgId), attempts: 5);
        }

        Assert.Equal(4, granted);
    }

    [Fact]
    public void OnePersonCannotEatTheWholeOrganisationsHourlyAllowance()
    {
        using var limiter = CharterRateLimiting.CreateIntakeLimiter(new CharterRateLimits
        {
            PerUserPerMinute = 100,
            PerUserPerHour = 5,
            PerOrgPerMinute = 100,
            PerOrgPerHour = 50,
        });

        var orgId = Guid.CreateVersion7();

        Assert.Equal(5, Acquire(limiter, Intake(Guid.CreateVersion7(), orgId), attempts: 40));

        // Their colleague is unaffected: the per-user window is theirs, not the organisation's.
        Assert.Equal(5, Acquire(limiter, Intake(Guid.CreateVersion7(), orgId), attempts: 40));
    }

    [Fact]
    public void OneOrganisationsFloodDoesNotThrottleAnother()
    {
        using var limiter = CharterRateLimiting.CreateIntakeLimiter(new CharterRateLimits
        {
            PerUserPerMinute = 2,
            PerUserPerHour = 2,
            PerOrgPerMinute = 2,
            PerOrgPerHour = 2,
        });

        Assert.Equal(2, Acquire(limiter, Intake(Guid.CreateVersion7(), Guid.CreateVersion7()), attempts: 10));
        Assert.Equal(2, Acquire(limiter, Intake(Guid.CreateVersion7(), Guid.CreateVersion7()), attempts: 10));
    }

    [Fact]
    public void ReadingIsNotIntakeAndIsNotLimited()
    {
        using var limiter = CharterRateLimiting.CreateIntakeLimiter(new CharterRateLimits
        {
            PerUserPerMinute = 1,
            PerUserPerHour = 1,
            PerOrgPerMinute = 1,
            PerOrgPerHour = 1,
        });

        // Polling a status thread while a session runs is the normal, expected behaviour of this app.
        // Throttling it would break section 11 in the name of section 31.
        Assert.Equal(50, Acquire(limiter, Reading(Guid.CreateVersion7(), Guid.CreateVersion7()), attempts: 50));
    }

    [Fact]
    public void AnAnonymousCallerHasNoPartitionToChargeAndIsLetThroughToTheAuthCheck()
    {
        using var limiter = CharterRateLimiting.CreateIntakeLimiter(new CharterRateLimits
        {
            PerUserPerMinute = 1,
            PerUserPerHour = 1,
            PerOrgPerMinute = 1,
            PerOrgPerHour = 1,
        });

        var anonymous = new DefaultHttpContext();
        anonymous.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(IntakeRateLimitMetadata.Instance),
            "intake"));

        // Intake requires authentication, so an unauthenticated caller is refused by the endpoint
        // rather than counted against somebody else's window. Partitioning by IP instead would
        // throttle a whole office behind one NAT.
        Assert.Null(CharterRateLimiting.UserKey(anonymous));
        Assert.Equal(10, Acquire(limiter, anonymous, attempts: 10));
    }

    [Fact]
    public void TheDefaultCeilingsAreWellUnderFourHundredSessions()
    {
        var limits = new CharterRateLimits();

        Assert.True(limits.PerUserPerHour < 400);
        Assert.True(limits.PerOrgPerHour < 400);
        Assert.True(limits.PerUserPerMinute <= limits.PerUserPerHour);
        Assert.True(limits.PerOrgPerMinute <= limits.PerOrgPerHour);
    }

    [Fact]
    public void ThePartitionKeysNameTheMemberAndTheOrganisation()
    {
        var userId = Guid.CreateVersion7();
        var orgId = Guid.CreateVersion7();
        var context = Intake(userId, orgId);

        Assert.Equal($"user:{userId:N}", CharterRateLimiting.UserKey(context));
        Assert.Equal($"org:{orgId:N}", CharterRateLimiting.OrgKey(context));
        Assert.True(CharterRateLimiting.IsIntake(context));
        Assert.False(CharterRateLimiting.IsIntake(Reading(userId, orgId)));
    }

    [Fact]
    public async Task TheRefusalIsPlainLanguageAndNotAStackTrace()
    {
        var body = await ApiPayloads.RenderAsync(ApiProblems.TooManyRequests());

        Assert.Contains("Give it a minute", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
    }
}
