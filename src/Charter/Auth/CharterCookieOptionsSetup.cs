using Charter.Configuration;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Charter.Auth;

/// <summary>
/// Configures the session cookie from <see cref="CharterConfig"/>.
/// </summary>
/// <remarks>
/// A configuration class rather than a lambda so the settings come from the container the host built
/// rather than from a closure over the service collection, and so the one decision that varies —
/// whether this deployment is https — is read from the same validated <c>CHARTER_BASE_URL</c> that
/// preflight checked.
/// </remarks>
public sealed class CharterCookieOptionsSetup : IConfigureNamedOptions<CookieAuthenticationOptions>
{
    private readonly CharterConfig config;

    public CharterCookieOptionsSetup(CharterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        this.config = config;
    }

    /// <inheritdoc />
    public void Configure(string? name, CookieAuthenticationOptions options)
    {
        if (!string.Equals(name, CharterAuthenticationDefaults.Scheme, StringComparison.Ordinal))
        {
            return;
        }

        Configure(options);
    }

    /// <inheritdoc />
    public void Configure(CookieAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var https = config.BaseUrl.Scheme == Uri.UriSchemeHttps;

        // __Host- pins the cookie to this exact origin with no Domain attribute and requires Secure,
        // so it is only valid — and only usable — on an https deployment.
        options.Cookie.Name = https
            ? CharterAuthenticationDefaults.SecureCookieName
            : CharterAuthenticationDefaults.CookieName;

        // The frontend never reads this. The spec forbids browser storage APIs and cookies written
        // from JS, so HttpOnly costs nothing and takes session theft off the XSS impact list.
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = https ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        options.Cookie.Path = "/";

        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;

        // The SPA wants a status code, not a redirect to a sign-in page it would have to parse.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };

        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    }
}
