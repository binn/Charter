namespace Charter.Api.Contracts;

/// <summary>How a sign-in method is presented (section 21).</summary>
public enum ApiAuthProviderStyle
{
    /// <summary>A form on the page: email and password.</summary>
    Credential,

    /// <summary>A button that leaves for an authority and comes back.</summary>
    Redirect,
}

/// <summary>One sign-in method this instance offers.</summary>
/// <remarks>
/// Nothing here is a secret: a client id is public by construction and is not carried anyway. The
/// page needs a name to label the button and a URL to send the browser to, and no more.
/// </remarks>
public sealed record AuthProviderResponse
{
    /// <summary>The stable key: <c>password</c>, <c>github</c>, <c>google</c>, …</summary>
    public required string Name { get; init; }

    public required ApiAuthProviderStyle Style { get; init; }

    /// <summary>Where the browser goes to start a redirect sign-in. Absent for the password form.</summary>
    public string? StartUrl { get; init; }
}

/// <summary><c>GET /api/auth/providers</c>. What the sign-in page should render.</summary>
public sealed record AuthProvidersResponse
{
    /// <summary>Password first, then whichever federated providers are configured.</summary>
    public required IReadOnlyList<AuthProviderResponse> Providers { get; init; }

    /// <summary>
    /// True when this instance can email a reset link, so the page knows whether to offer
    /// <em>forgot password</em> or to say who to ask instead (change spec 001 part C.1).
    /// </summary>
    public required bool SelfServicePasswordReset { get; init; }
}

/// <summary><c>POST /api/auth/sign-in</c>.</summary>
public sealed record SignInBody
{
    public string? Email { get; init; }

    /// <summary>Never logged, never stored, never echoed back.</summary>
    public string? Password { get; init; }
}

/// <summary><c>GET /api/auth/session</c>, and the body of a successful sign-in.</summary>
/// <remarks>
/// Deliberately thin. The full viewer — capabilities, preferences, organisation name — is
/// <c>GET /api/me</c>, and duplicating it here would be a second thing to keep in step with section
/// 7.4.
/// </remarks>
public sealed record SessionResponse
{
    public required string UserId { get; init; }

    public required string DisplayName { get; init; }

    public required string Email { get; init; }

    public required string OrganizationId { get; init; }

    /// <summary>Additive (section 7.1).</summary>
    public required IReadOnlyList<ApiRole> Roles { get; init; }

    /// <summary>Which of the section 21 providers this session signed in with.</summary>
    public required string Provider { get; init; }
}

/// <summary><c>GET /api/setup/status</c>. The one thing an unclaimed instance will tell anybody.</summary>
/// <remarks>
/// Not a disclosure: the setup gate already answers every other API call with a 503 that says the
/// instance has no users (section 30.1). This exists so the SPA can render the setup page instead of
/// a sign-in form it would immediately fail against.
/// </remarks>
public sealed record SetupStatusResponse
{
    public required bool SetupRequired { get; init; }
}

/// <summary><c>POST /api/setup/complete</c>: redeems the one-time token (section 30.1).</summary>
public sealed record CompleteSetupBody
{
    /// <summary>The value the operator read out of the container log.</summary>
    public string? Token { get; init; }

    public string? Email { get; init; }

    public string? DisplayName { get; init; }

    /// <summary>Hashed and discarded. Never stored, never logged.</summary>
    public string? Password { get; init; }

    /// <summary>Optional; section 30.2 asks for it again on the dashboard checklist.</summary>
    public string? OrganizationName { get; init; }
}

/// <summary><c>POST /api/auth/forgot-password</c>.</summary>
public sealed record ForgotPasswordBody
{
    public string? Email { get; init; }
}

/// <summary>
/// What the forgot-password form is told, whoever typed into it.
/// </summary>
/// <remarks>
/// <strong>There is no link on this type, and there must never be one.</strong> Anybody can type
/// anybody's address into that form, so surfacing the one-time link there would turn "email is not
/// configured" into "account takeover by anyone who knows an address". The same sentence comes back
/// for an address with an account and for one without, so the form is not an enumeration oracle
/// either.
/// </remarks>
public sealed record ForgotPasswordResponse
{
    public required string Message { get; init; }
}

/// <summary><c>POST /api/auth/reset-password</c>.</summary>
public sealed record ResetPasswordBody
{
    /// <summary>The one-time value out of the link.</summary>
    public string? Token { get; init; }

    /// <summary>The new password. Hashed and discarded.</summary>
    public string? Password { get; init; }
}

/// <summary><c>POST /api/auth/invitations/accept</c> (section 30.2).</summary>
public sealed record AcceptInvitationBody
{
    public string? Token { get; init; }

    /// <summary>What they want to be called. Ignored when the account already exists.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The password they are choosing. Hashed and discarded.</summary>
    public string? Password { get; init; }
}
