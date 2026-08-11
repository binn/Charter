using System.Security.Claims;
using Charter.Auth.Audit;
using Charter.Auth.Authorization;
using Charter.Auth.Providers;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Charter.Auth;

/// <summary>Why a sign-in did not end in a session.</summary>
public enum SignInFailure
{
    /// <summary>
    /// Wrong address, wrong password, unknown subject. <b>One value on purpose</b> (section 21): the
    /// two cases are never told apart, because the difference is an account-enumeration oracle.
    /// </summary>
    InvalidCredentials,

    /// <summary>Section 31: too many attempts from this caller.</summary>
    Throttled,

    /// <summary>The attempt did not carry what the provider needs.</summary>
    Malformed,

    /// <summary>This instance does not offer that provider.</summary>
    UnknownProvider,

    /// <summary>
    /// The subject was verified, but no Charter account belongs to it. Never creates one (section 21).
    /// </summary>
    NoAccount,

    /// <summary>The person authenticated but is a member of no organisation.</summary>
    NoMembership,
}

/// <summary>The outcome of one sign-in attempt.</summary>
public abstract record SignInResult
{
    private SignInResult()
    {
    }

    /// <summary>A session may be issued for <paramref name="Principal"/>.</summary>
    public sealed record Succeeded(
        ClaimsPrincipal Principal,
        MemberSnapshot Member,
        string DisplayName,
        string Email,
        IdentityProviderKind Provider,
        Uri? ReturnUrl = null) : SignInResult;

    /// <summary>Send the person to <paramref name="Location"/>; the answer comes back to the callback.</summary>
    public sealed record RedirectRequired(Uri Location) : SignInResult;

    /// <summary>No session. <paramref name="Message"/> is safe to show and never names which half was wrong.</summary>
    public sealed record Failed(SignInFailure Reason, string Message, TimeSpan? RetryAfter = null) : SignInResult;
}

/// <summary>
/// Everything between "somebody presented a credential" and "there is a principal to put in a
/// cookie" (section 21).
/// </summary>
/// <remarks>
/// <para>
/// The endpoints hold none of this. A sign-in is: ask the <see cref="IIdentityProvider"/> for a
/// verified subject, ask <see cref="IIdentityLinker"/> which Charter user that is — never creating
/// one — resolve the membership that carries the roles, and build the principal. Four steps, one
/// place, so email/password, four OAuth providers and a future SAML all reach the same cookie
/// through the same audit entry.
/// </para>
/// <para>
/// <b>Failure is deliberately uninformative.</b> Every refusal that could distinguish "no such user"
/// from "wrong password" collapses to <see cref="SignInFailure.InvalidCredentials"/> with one
/// sentence, and <see cref="PasswordIdentityProvider"/> already spends the same hashing work either
/// way so the latency does not leak it back.
/// </para>
/// </remarks>
public sealed class SignInService
{
    /// <summary>
    /// The one sentence a refused credential ever produces (section 21). It names neither half.
    /// </summary>
    public const string GenericFailure = "That email address and password do not match an account.";

    private readonly IdentityProviderRegistry providers;
    private readonly IIdentityLinker linker;
    private readonly ICharterAuthorizationService authorization;
    private readonly CharterDbContext database;
    private readonly IAuditWriter audit;
    private readonly CharterConfig config;
    private readonly ILogger<SignInService> logger;

    public SignInService(
        IdentityProviderRegistry providers,
        IIdentityLinker linker,
        ICharterAuthorizationService authorization,
        CharterDbContext database,
        IAuditWriter audit,
        CharterConfig config,
        ILogger<SignInService> logger)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(linker);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        this.providers = providers;
        this.linker = linker;
        this.authorization = authorization;
        this.database = database;
        this.audit = audit;
        this.config = config;
        this.logger = logger;
    }

    /// <summary>Every sign-in method this instance offers, password first (section 21).</summary>
    public IReadOnlyList<IIdentityProvider> Available => providers.All;

    /// <summary>Email and password. Always available (section 21).</summary>
    public Task<SignInResult> WithPasswordAsync(
        string? email,
        Secret? password,
        string? clientKey,
        CancellationToken cancellationToken = default)
        => StepAsync(
            providers.Password,
            attempt => providers.Password.BeginAsync(attempt, cancellationToken),
            new IdentityAuthenticationAttempt
            {
                Email = email,
                Password = password,
                ClientKey = clientKey,
            },
            returnUrl: null,
            cancellationToken);

    /// <summary>
    /// Starts a redirect provider: returns where to send the person, with a single-use <c>state</c>.
    /// </summary>
    public async Task<SignInResult> BeginAsync(
        string providerName,
        Uri? returnUrl,
        CancellationToken cancellationToken = default)
    {
        if (providers.Find(providerName) is not { } provider)
        {
            return UnknownProvider();
        }

        if (provider.Style != IdentityProviderStyle.Redirect)
        {
            return new SignInResult.Failed(
                SignInFailure.Malformed,
                "That sign-in method is a form, not a redirect.");
        }

        var result = await provider.BeginAsync(
            new IdentityAuthenticationAttempt { ReturnUrl = Safe(returnUrl) },
            cancellationToken);

        return result switch
        {
            IdentityAuthenticationResult.RedirectRequired redirect =>
                new SignInResult.RedirectRequired(redirect.Location),
            IdentityAuthenticationResult.Failed failed => Translate(failed),
            _ => new SignInResult.Failed(SignInFailure.Malformed, "That sign-in could not be started."),
        };
    }

    /// <summary>Consumes a callback from an authority and resolves it to a Charter account.</summary>
    public async Task<SignInResult> CompleteAsync(
        string providerName,
        IReadOnlyDictionary<string, string?> parameters,
        string? clientKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        if (providers.Find(providerName) is not { } provider)
        {
            return UnknownProvider();
        }

        var attempt = new IdentityAuthenticationAttempt
        {
            Parameters = parameters,
            ClientKey = clientKey,
        };

        return await StepAsync(
            provider,
            candidate => provider.CompleteAsync(candidate, cancellationToken),
            attempt,
            returnUrl: null,
            cancellationToken);
    }

    /// <summary>
    /// Issues a session for a user Charter has just created or just verified another way — the
    /// setup token (section 30.1) and an accepted invitation (section 30.2).
    /// </summary>
    /// <remarks>
    /// Deliberately not a back door around the providers: it is only ever reached from a code path
    /// that has already established the account exists <em>and</em> that the caller controls it, and
    /// it writes the same audit entry an ordinary sign-in does.
    /// </remarks>
    public async Task<SignInResult> ForUserAsync(
        Guid userId,
        IdentityProviderKind provider,
        CancellationToken cancellationToken = default)
    {
        var user = await database.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == userId, cancellationToken);

        return user is null
            ? new SignInResult.Failed(SignInFailure.NoAccount, GenericFailure)
            : await IssueAsync(user, provider, returnUrl: null, cancellationToken);
    }

    /// <summary>Records that a session ended. The other half of the audit story.</summary>
    public Task RecordSignOutAsync(MemberSnapshot member, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        return audit.RecordAsync(
            new AuditEntry
            {
                OrgId = member.OrgId,
                ActorUserId = member.UserId,
                Action = AuditActions.SignedOut,
                TargetType = "user",
                TargetId = member.UserId.ToString(),
            },
            cancellationToken);
    }

    private async Task<SignInResult> StepAsync(
        IIdentityProvider provider,
        Func<IdentityAuthenticationAttempt, Task<IdentityAuthenticationResult>> step,
        IdentityAuthenticationAttempt attempt,
        Uri? returnUrl,
        CancellationToken cancellationToken)
    {
        var result = await step(attempt);

        if (result is IdentityAuthenticationResult.RedirectRequired redirect)
        {
            return new SignInResult.RedirectRequired(redirect.Location);
        }

        if (result is IdentityAuthenticationResult.Failed failed)
        {
            return Translate(failed);
        }

        if (result is not IdentityAuthenticationResult.Authenticated authenticated)
        {
            return new SignInResult.Failed(SignInFailure.InvalidCredentials, GenericFailure);
        }

        var resolution = await linker.ResolveAsync(authenticated.Identity, cancellationToken);

        if (resolution is IdentityResolution.NoAccount refusal)
        {
            // Section 21: a successful federated sign-in never creates a user. The person is told
            // plainly to ask for an invitation rather than left staring at a generic failure.
            logger.LogInformation(
                "A verified {Provider} subject signed in with no Charter account to match",
                provider.Kind);

            return new SignInResult.Failed(SignInFailure.NoAccount, refusal.Message);
        }

        var user = resolution switch
        {
            IdentityResolution.Linked linked => linked.User,
            IdentityResolution.NewlyLinked newly => newly.User,
            _ => null,
        };

        return user is null
            ? new SignInResult.Failed(SignInFailure.InvalidCredentials, GenericFailure)
            : await IssueAsync(user, provider.Kind, returnUrl, cancellationToken);
    }

    private async Task<SignInResult> IssueAsync(
        User user,
        IdentityProviderKind provider,
        Uri? returnUrl,
        CancellationToken cancellationToken)
    {
        var memberships = await authorization.ResolveMembershipsAsync(user.Id, cancellationToken);

        // Section 7.2a: one instance, one organisation. There is nothing to choose between, and a
        // user with no membership is an account that was never finished.
        if (memberships.Count == 0)
        {
            logger.LogWarning("A user signed in with no membership; they cannot be shown anything");

            return new SignInResult.Failed(
                SignInFailure.NoMembership,
                "Your account is not a member of this organisation yet. Ask an administrator.");
        }

        var member = memberships[0];

        await audit.RecordAsync(
            new AuditEntry
            {
                OrgId = member.OrgId,
                ActorUserId = user.Id,
                Action = AuditActions.SignedIn,
                TargetType = "user",
                TargetId = user.Id.ToString(),
                Metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["provider"] = provider.ToString().ToLowerInvariant(),
                },
            },
            cancellationToken);

        return new SignInResult.Succeeded(
            CharterPrincipalFactory.Create(member, user.DisplayName, provider),
            member,
            user.DisplayName,
            user.Email,
            provider,
            returnUrl);
    }

    private static SignInResult Translate(IdentityAuthenticationResult.Failed failed) => failed.Reason switch
    {
        IdentityFailureReason.Throttled =>
            new SignInResult.Failed(SignInFailure.Throttled, failed.Message, failed.RetryAfter),

        IdentityFailureReason.MalformedAttempt or IdentityFailureReason.UnsupportedStep =>
            new SignInResult.Failed(SignInFailure.Malformed, failed.Message),

        IdentityFailureReason.NotConfigured =>
            new SignInResult.Failed(SignInFailure.UnknownProvider, failed.Message),

        IdentityFailureReason.ProviderRejected =>
            new SignInResult.Failed(SignInFailure.InvalidCredentials, failed.Message),

        // Section 21, the rule this whole type exists to keep: one answer for both halves.
        _ => new SignInResult.Failed(SignInFailure.InvalidCredentials, GenericFailure),
    };

    private static SignInResult UnknownProvider() => new SignInResult.Failed(
        SignInFailure.UnknownProvider,
        "That sign-in method is not available on this instance.");

    /// <summary>
    /// Keeps an open redirect out of the sign-in flow.
    /// </summary>
    /// <remarks>
    /// A <c>returnUrl</c> that leaves this origin is the classic phishing hop off a login page, so
    /// anything absolute and off-origin is dropped rather than corrected.
    /// </remarks>
    private Uri? Safe(Uri? returnUrl)
    {
        if (returnUrl is null)
        {
            return null;
        }

        if (!returnUrl.IsAbsoluteUri)
        {
            return returnUrl;
        }

        return Uri.Compare(
                   returnUrl,
                   config.BaseUrl,
                   UriComponents.SchemeAndServer,
                   UriFormat.Unescaped,
                   StringComparison.OrdinalIgnoreCase) == 0
            ? returnUrl
            : null;
    }
}
