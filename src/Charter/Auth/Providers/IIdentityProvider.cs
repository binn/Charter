using Charter.Configuration;
using Charter.Domain;

namespace Charter.Auth.Providers;

/// <summary>How a provider gets from "someone wants to sign in" to a verified subject.</summary>
public enum IdentityProviderStyle
{
    /// <summary>The person hands Charter a credential and Charter verifies it. Email and password.</summary>
    Credential,

    /// <summary>
    /// The person is sent to an authority and comes back with something to validate. Every OAuth
    /// provider and SAML.
    /// </summary>
    Redirect,
}

/// <summary>Why a sign-in did not produce a subject.</summary>
public enum IdentityFailureReason
{
    /// <summary>Wrong email, wrong password, unknown subject. Never distinguished to the caller.</summary>
    InvalidCredentials,

    /// <summary>The attempt did not carry what this provider needs.</summary>
    MalformedAttempt,

    /// <summary>Section 31: too many attempts. The caller should surface a retry-after.</summary>
    Throttled,

    /// <summary>The provider is not configured on this instance.</summary>
    NotConfigured,

    /// <summary>The step does not apply to this provider — completing a credential sign-in, say.</summary>
    UnsupportedStep,

    /// <summary>The authority answered, but not with something usable.</summary>
    ProviderRejected,
}

/// <summary>A subject as some provider describes it, before Charter has decided who that is.</summary>
/// <param name="Provider">Which of the section 21 providers vouched for this.</param>
/// <param name="ProviderUserId">The provider's own stable subject id. Never the email address.</param>
/// <param name="Email">Used only to match an existing Charter user, never as the identity itself.</param>
/// <param name="DisplayName">Optional; providers vary in whether they supply one.</param>
public sealed record ExternalIdentity(
    IdentityProviderKind Provider,
    string ProviderUserId,
    string? Email,
    string? DisplayName);

/// <summary>
/// One sign-in attempt, whichever provider it is for.
/// </summary>
/// <remarks>
/// The password is a <see cref="Secret"/>, so the compiler-generated <c>ToString()</c> of this record
/// prints a placeholder. Section 21: a password is never stored and never logged, and the way to make
/// that true is to make the careless thing safe rather than to rely on nobody interpolating a record.
/// </remarks>
public sealed record IdentityAuthenticationAttempt
{
    /// <summary>For credential providers.</summary>
    public string? Email { get; init; }

    /// <summary>For credential providers. Redacted in every string representation.</summary>
    public Secret? Password { get; init; }

    /// <summary>
    /// The query or form values a redirect provider came back with — <c>code</c> and <c>state</c> for
    /// OAuth, <c>SAMLResponse</c> and <c>RelayState</c> for SAML. A dictionary rather than named
    /// properties precisely so SAML slots in without reshaping this record.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Parameters { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>Where to send the person once they are signed in.</summary>
    public Uri? ReturnUrl { get; init; }

    /// <summary>
    /// Something stable about the caller — usually the remote address — used only as a throttling
    /// key (section 31). Never persisted.
    /// </summary>
    public string? ClientKey { get; init; }

    /// <summary>The absolute callback URL this instance published to the provider.</summary>
    public Uri? RedirectUri { get; init; }
}

/// <summary>The outcome of one step of a sign-in.</summary>
public abstract record IdentityAuthenticationResult
{
    private IdentityAuthenticationResult()
    {
    }

    /// <summary>A verified subject. Charter still has to decide which user, if any, that is.</summary>
    public sealed record Authenticated(ExternalIdentity Identity) : IdentityAuthenticationResult;

    /// <summary>Send the person to <paramref name="Location"/>; the answer comes back to the callback.</summary>
    public sealed record RedirectRequired(Uri Location, string State) : IdentityAuthenticationResult;

    /// <summary>No subject. <paramref name="Message"/> is safe to show; it never names which half was wrong.</summary>
    public sealed record Failed(IdentityFailureReason Reason, string Message, TimeSpan? RetryAfter = null)
        : IdentityAuthenticationResult;
}

/// <summary>
/// The single seam every sign-in method sits behind (section 21).
/// </summary>
/// <remarks>
/// <para>
/// Two steps rather than one, because a redirect provider genuinely has two: <see cref="BeginAsync"/>
/// either verifies a credential outright or asks for a redirect, and <see cref="CompleteAsync"/>
/// consumes whatever came back. A credential provider answers <see cref="BeginAsync"/> and refuses
/// <see cref="CompleteAsync"/>; a redirect provider does the reverse. Nothing above this interface
/// needs to know which kind it is holding beyond <see cref="Style"/>.
/// </para>
/// <para>
/// This shape is what SAML needs: an <c>AuthnRequest</c> redirect out, a <c>SAMLResponse</c> form
/// post back in, arriving through <see cref="IdentityAuthenticationAttempt.Parameters"/>. Retrofitting
/// that into an interface shaped only for OAuth's <c>code</c> parameter is the miserable week
/// section 21 is warning about, so the shape is here from the start even though the implementation
/// is not.
/// </para>
/// </remarks>
public interface IIdentityProvider
{
    /// <summary>Which section 21 provider this is.</summary>
    IdentityProviderKind Kind { get; }

    /// <summary>The stable key used in URLs and configuration: <c>password</c>, <c>github</c>, …</summary>
    string Name { get; }

    /// <summary>What the sign-in page should render for this provider.</summary>
    IdentityProviderStyle Style { get; }

    /// <summary>Section 21: SAML is organisation mode only.</summary>
    bool RequiresOrganizationMode { get; }

    /// <summary>Verifies a credential, or asks for the redirect that will produce one.</summary>
    Task<IdentityAuthenticationResult> BeginAsync(
        IdentityAuthenticationAttempt attempt,
        CancellationToken cancellationToken = default);

    /// <summary>Consumes a callback from an authority.</summary>
    Task<IdentityAuthenticationResult> CompleteAsync(
        IdentityAuthenticationAttempt attempt,
        CancellationToken cancellationToken = default);
}
