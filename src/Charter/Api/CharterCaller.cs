using System.Security.Claims;
using Charter.Auth;
using Charter.Auth.Authorization;

namespace Charter.Api;

/// <summary>
/// The acting member, re-read from the database on every call.
/// </summary>
/// <remarks>
/// The cookie carries roles so the common case is cheap, but nothing security-relevant trusts them:
/// this resolves the <c>members</c> row through
/// <see cref="ICharterAuthorizationService.ResolveMemberAsync"/>, so a cookie that outlives a revoked
/// role grants nothing. Both the HTTP endpoints and the SignalR hub go through here, which is what
/// makes "the hub authorises with the same service as REST" true rather than aspirational.
/// </remarks>
public static class CharterCaller
{
    /// <summary>
    /// The member behind a principal, or <c>null</c> when the principal is not ours or names a
    /// membership that no longer exists.
    /// </summary>
    public static async Task<MemberSnapshot?> ResolveAsync(
        ClaimsPrincipal? principal,
        ICharterAuthorizationService authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);

        if (CharterPrincipalFactory.Read(principal) is not { } identity)
        {
            return null;
        }

        return await authorization.ResolveMemberAsync(identity.OrgId, identity.UserId, cancellationToken);
    }
}
