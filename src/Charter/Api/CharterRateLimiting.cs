using System.Globalization;
using System.Threading.RateLimiting;
using Charter.Auth;
using Microsoft.AspNetCore.RateLimiting;

namespace Charter.Api;

/// <summary>
/// Marks an endpoint as intake, which is what the section 31 limiter partitions on.
/// </summary>
/// <remarks>
/// Endpoint metadata rather than a path list: a route that gets renamed keeps its limit, and a new
/// intake route that forgets the marker is a visible omission at the call site instead of a silent
/// hole in a regex somewhere else.
/// </remarks>
public sealed class IntakeRateLimitMetadata
{
    /// <summary>The single instance every intake endpoint carries.</summary>
    public static IntakeRateLimitMetadata Instance { get; } = new();
}

/// <summary>
/// How much intake one person, and one organisation, may do.
/// </summary>
/// <remarks>
/// Section 31: <em>"Rate limiting at intake — per user and per org. An enthusiastic requester with a
/// script should not be able to queue 400 sessions."</em> Both dimensions are needed: a per-user
/// limit alone lets ten accounts do the damage one was stopped from doing, and a per-org limit alone
/// lets one person consume the whole organisation's allowance.
/// </remarks>
public sealed record CharterRateLimits
{
    /// <summary>A burst allowance. Filing several requests in a sitting is normal.</summary>
    public int PerUserPerMinute { get; init; } = 6;

    /// <summary>The sustained ceiling for one person.</summary>
    public int PerUserPerHour { get; init; } = 40;

    /// <summary>A burst allowance for a busy team.</summary>
    public int PerOrgPerMinute { get; init; } = 30;

    /// <summary>The sustained ceiling for the whole organisation. Well under 400 sessions.</summary>
    public int PerOrgPerHour { get; init; } = 200;

    /// <summary>How many callers to track before the limiter starts evicting idle partitions.</summary>
    public int MaxTrackedPartitions { get; init; } = 4_096;
}

/// <summary>
/// Builds the section 31 intake limiter out of ASP.NET Core's own primitives.
/// </summary>
/// <remarks>
/// <para>
/// The limiter is installed as the global one, and every partition returns
/// <see cref="RateLimitPartition.GetNoLimiter{TKey}"/> for any endpoint that is not carrying
/// <see cref="IntakeRateLimitMetadata"/>. That is deliberate: an endpoint policy can only apply one
/// limiter per endpoint, and section 31 asks for two dimensions at once. Chaining four partitioned
/// limiters — user-minute, user-hour, org-minute, org-hour — is the only way to enforce "per user
/// <em>and</em> per org" rather than whichever one was attached last.
/// </para>
/// <para>
/// Partitioning is by the authenticated member, never by IP: a shared office NAT would otherwise
/// throttle a whole company because one person was busy, and an unauthenticated caller cannot reach
/// intake at all.
/// </para>
/// </remarks>
public static class CharterRateLimiting
{
    /// <summary>The four chained partitions, exposed so a test can drive them without a server.</summary>
    public static PartitionedRateLimiter<HttpContext> CreateIntakeLimiter(CharterRateLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        return PartitionedRateLimiter.CreateChained(
            Fixed("u1", UserKey, limits.PerUserPerMinute, TimeSpan.FromMinutes(1)),
            Fixed("u60", UserKey, limits.PerUserPerHour, TimeSpan.FromHours(1)),
            Fixed("o1", OrgKey, limits.PerOrgPerMinute, TimeSpan.FromMinutes(1)),
            Fixed("o60", OrgKey, limits.PerOrgPerHour, TimeSpan.FromHours(1)));
    }

    /// <summary>Installs the limiter and the plain-language refusal (section 11).</summary>
    public static void Configure(RateLimiterOptions options, CharterRateLimits limits)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(limits);

        options.GlobalLimiter = CreateIntakeLimiter(limits);
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        options.OnRejected = async (context, cancellationToken) =>
        {
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            {
                context.HttpContext.Response.Headers.RetryAfter =
                    ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            }

            await ApiProblems.TooManyRequests().ExecuteAsync(context.HttpContext);
            _ = cancellationToken;
        };
    }

    /// <summary>Whether this request is intake at all. Everything else is unlimited here.</summary>
    public static bool IsIntake(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.GetEndpoint()?.Metadata.GetMetadata<IntakeRateLimitMetadata>() is not null;
    }

    /// <summary>The per-user partition key, or <c>null</c> when there is no member to charge.</summary>
    public static string? UserKey(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return CharterPrincipalFactory.Read(context.User) is { } identity
            ? string.Create(CultureInfo.InvariantCulture, $"user:{identity.UserId:N}")
            : null;
    }

    /// <summary>The per-organisation partition key.</summary>
    public static string? OrgKey(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return CharterPrincipalFactory.Read(context.User) is { } identity
            ? string.Create(CultureInfo.InvariantCulture, $"org:{identity.OrgId:N}")
            : null;
    }

    private static PartitionedRateLimiter<HttpContext> Fixed(
        string dimension,
        Func<HttpContext, string?> key,
        int permitLimit,
        TimeSpan window)
        => PartitionedRateLimiter.Create<HttpContext, string>(
            context =>
            {
                if (!IsIntake(context) || key(context) is not { } partition)
                {
                    return RateLimitPartition.GetNoLimiter(string.Empty);
                }

                return RateLimitPartition.GetFixedWindowLimiter(
                    string.Concat(dimension, ":", partition),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permitLimit,
                        Window = window,

                        // Nothing waits in line. Section 31 is about refusing a flood, and a queue that
                        // holds a request open for a minute reads as the app hanging.
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true,
                    });
            },
            StringComparer.Ordinal);
}
