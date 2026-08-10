using System.Globalization;
using System.Security.Claims;
using Charter.Auth.Authorization;
using Charter.Domain;

namespace Charter.Auth;

/// <summary>The claim types Charter's own cookie carries.</summary>
public static class CharterClaimTypes
{
    /// <summary>The <see cref="User"/> id. The subject of everything in the audit log.</summary>
    public const string UserId = "charter:user_id";

    /// <summary>The organisation this session is acting in.</summary>
    public const string OrgId = "charter:org_id";

    /// <summary>The <see cref="Member"/> id — the row that carries the roles.</summary>
    public const string MemberId = "charter:member_id";

    /// <summary>One claim per role held. Additive by design (section 7.1).</summary>
    public const string Role = "charter:role";

    /// <summary>Which of the section 21 providers this session signed in with.</summary>
    public const string Provider = "charter:provider";
}

/// <summary>Names the cookie scheme so the host and the endpoints agree on one spelling.</summary>
public static class CharterAuthenticationDefaults
{
    public const string Scheme = "charter";

    /// <summary>Prefixed <c>__Host-</c> where the deployment is https, which pins it to this origin.</summary>
    public const string SecureCookieName = "__Host-charter-session";

    /// <summary>The plain name, for an http deployment where the prefix would make the cookie invalid.</summary>
    public const string CookieName = "charter-session";
}

/// <summary>
/// Converts between a signed-in member and the cookie's claims.
/// </summary>
/// <remarks>
/// <para>
/// The roles ride in the cookie so the common case costs no query, but nothing security-relevant
/// trusts them on their own: <see cref="ICharterAuthorizationService"/> re-reads the member row for
/// every decision. A cookie that outlives a revoked role therefore grants nothing — which is the
/// reason to be relaxed about putting roles in it at all.
/// </para>
/// <para>
/// There is no "personal mode" claim, and no place to put one (section 7.2).
/// </para>
/// </remarks>
public static class CharterPrincipalFactory
{
    /// <summary>Builds the principal for a signed-in member.</summary>
    public static ClaimsPrincipal Create(MemberSnapshot member, string displayName, IdentityProviderKind provider)
    {
        ArgumentNullException.ThrowIfNull(member);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, member.UserId.ToString()),
            new(ClaimTypes.Name, displayName ?? string.Empty),
            new(CharterClaimTypes.UserId, member.UserId.ToString()),
            new(CharterClaimTypes.OrgId, member.OrgId.ToString()),
            new(CharterClaimTypes.MemberId, member.MemberId.ToString()),
            new(CharterClaimTypes.Provider, provider.ToString().ToLowerInvariant()),
        };

        claims.AddRange(member.Roles.Select(role =>
            new Claim(CharterClaimTypes.Role, role.ToString().ToLowerInvariant())));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, CharterAuthenticationDefaults.Scheme));
    }

    /// <summary>Reads the organisation and user out of a principal, or null when it is not ours.</summary>
    public static (Guid OrgId, Guid UserId)? Read(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var orgId = principal.FindFirstValue(CharterClaimTypes.OrgId);
        var userId = principal.FindFirstValue(CharterClaimTypes.UserId);

        return Guid.TryParse(orgId, CultureInfo.InvariantCulture, out var org)
               && Guid.TryParse(userId, CultureInfo.InvariantCulture, out var user)
            ? (org, user)
            : null;
    }
}
