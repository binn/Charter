using System.Security.Cryptography;
using System.Text;
using Charter.Configuration;
using Microsoft.AspNetCore.Http;

namespace Charter.Deployments;

/// <summary>What the deployment webhook made of a caller.</summary>
public enum DeploymentWebhookAdmission
{
    /// <summary>The caller presented this instance's deployment secret.</summary>
    Allowed,

    /// <summary>No secret is configured, so the endpoint has nothing to admit anybody on.</summary>
    NotConfigured,

    /// <summary>A secret is configured and the caller did not present it, or presented the wrong one.</summary>
    Refused,
}

/// <summary>
/// The admission rule for section 18's generic deployment webhook.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A head commit SHA is not a credential.</strong> It was the endpoint's only admission rule,
/// and the reasoning behind that — "an unguessable 40-character key that already exists" — does not
/// survive contact with where the value comes from. The execution plane authors that SHA, so it knows
/// it before Charter does (section 16.3); it appears in every fork, every public pull request, every CI
/// log, and every notification email. A value that is legitimately known to strangers cannot decide
/// which strangers may write.
/// </para>
/// <para>
/// What that endpoint accepts is a URL Charter fetches on a loop from inside the control plane's own
/// network, and shows a non-engineer as a button under the sentence "Nothing you do here touches the
/// real one". Both of those need to be reached by the operator's hosting platform and by nobody else,
/// so the endpoint takes a per-instance shared secret: <c>CHARTER_DEPLOYMENT_WEBHOOK_SECRET</c>.
/// </para>
/// <para>
/// <strong>Three ways to present it, because platforms differ in what they will send.</strong> An
/// <c>Authorization: Bearer</c> header and an <c>X-Charter-Deployment-Secret</c> header are the ones to
/// use. The <c>?token=</c> query parameter exists for platforms whose post-deploy hook is a URL field
/// and nothing else — it is genuinely weaker, because a URL turns up in proxy logs and browser history
/// in a way a header does not, and the documentation says so rather than pretending the three are
/// equivalent.
/// </para>
/// <para>
/// <strong>Fail closed.</strong> An instance with no secret configured refuses every deployment report
/// rather than falling back to the SHA. A default that is safe only until somebody notices is not a
/// default.
/// </para>
/// </remarks>
public static class DeploymentWebhookAuthentication
{
    /// <summary>The dedicated header, for a platform that can send one.</summary>
    public const string HeaderName = "X-Charter-Deployment-Secret";

    /// <summary>The query parameter, for a platform that can only be given a URL.</summary>
    public const string QueryName = "token";

    /// <summary>The shortest secret this endpoint will run with.</summary>
    /// <remarks>
    /// Enforced at startup rather than here, so an operator learns their secret is too short when they
    /// set it and not when a preview quietly stops binding (section 4.1).
    /// </remarks>
    public const int MinimumSecretLength = 24;

    /// <summary>The one sentence a refused caller is given, whatever was wrong.</summary>
    /// <remarks>
    /// Missing and wrong produce the same answer, exactly as the GitHub webhook's do, so the response
    /// cannot be used to tell them apart. The log distinguishes them.
    /// </remarks>
    public const string RefusedMessage =
        "this endpoint requires the instance's deployment webhook secret, presented as an " +
        "Authorization: Bearer header, an " + HeaderName + " header, or a ?" + QueryName + "= parameter";

    /// <summary>What an operator who has not configured the endpoint at all is told.</summary>
    public const string NotConfiguredMessage =
        "this instance has no CHARTER_DEPLOYMENT_WEBHOOK_SECRET set, so the deployment webhook accepts " +
        "nothing. Set it and point your platform's post-deploy hook at this endpoint with it.";

    /// <summary>Reads whichever of the three carriers the caller used.</summary>
    /// <remarks>
    /// In precedence order, and the first one present wins outright — a caller that sends a header and
    /// a query parameter is not given two attempts at the secret.
    /// </remarks>
    public static string? Presented(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Presented(
            request.Headers.Authorization.ToString(),
            request.Headers[HeaderName].ToString(),
            request.Query[QueryName].ToString());
    }

    /// <inheritdoc cref="Presented(HttpRequest)" />
    public static string? Presented(string? authorization, string? header, string? query)
    {
        const string bearer = "Bearer ";

        if (!string.IsNullOrWhiteSpace(authorization)
            && authorization.StartsWith(bearer, StringComparison.OrdinalIgnoreCase)
            && authorization[bearer.Length..].Trim() is { Length: > 0 } token)
        {
            return token;
        }

        if (!string.IsNullOrWhiteSpace(header))
        {
            return header.Trim();
        }

        return string.IsNullOrWhiteSpace(query) ? null : query.Trim();
    }

    /// <summary>Decides whether a caller may write a deployment report.</summary>
    public static DeploymentWebhookAdmission Check(Secret? configured, string? presented)
    {
        if (configured is null)
        {
            return DeploymentWebhookAdmission.NotConfigured;
        }

        if (string.IsNullOrEmpty(presented))
        {
            return DeploymentWebhookAdmission.Refused;
        }

        return Matches(configured.Reveal(), presented)
            ? DeploymentWebhookAdmission.Allowed
            : DeploymentWebhookAdmission.Refused;
    }

    /// <summary>Compares in constant time, so a caller cannot walk the secret one character at a time.</summary>
    private static bool Matches(string expected, string presented)
    {
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(presented);

        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
