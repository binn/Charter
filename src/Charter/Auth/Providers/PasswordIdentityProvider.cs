using Charter.Data;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Charter.Auth.Providers;

/// <summary>
/// Email and password: the provider that is always available, whatever else is configured
/// (section 21).
/// </summary>
/// <remarks>
/// <para>
/// The verifier lives on the user's <see cref="Identity"/> row for
/// <see cref="IdentityProviderKind.Password"/>, alongside whatever OAuth identities they have also
/// linked. Deleting that row is how password sign-in is turned off for one person.
/// </para>
/// <para>
/// Three things this deliberately does that a naive version does not: it never says which half of
/// the pair was wrong, it spends the same PBKDF2 work when no user matched so latency is not an
/// enumeration oracle, and it re-hashes on sign-in when the stored parameters are behind the current
/// ones.
/// </para>
/// </remarks>
public sealed class PasswordIdentityProvider : IIdentityProvider
{
    /// <summary>What the seam and any URL calls this provider.</summary>
    public const string ProviderName = "password";

    private const string GenericFailure = "That email address and password do not match an account.";

    private readonly CharterDbContext database;
    private readonly ICharterPasswordHasher hasher;
    private readonly ISignInThrottle throttle;
    private readonly ILogger<PasswordIdentityProvider> logger;

    public PasswordIdentityProvider(
        CharterDbContext database,
        ICharterPasswordHasher hasher,
        ISignInThrottle throttle,
        ILogger<PasswordIdentityProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(throttle);
        ArgumentNullException.ThrowIfNull(logger);

        this.database = database;
        this.hasher = hasher;
        this.throttle = throttle;
        this.logger = logger;
    }

    /// <inheritdoc />
    public IdentityProviderKind Kind => IdentityProviderKind.Password;

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public IdentityProviderStyle Style => IdentityProviderStyle.Credential;

    /// <inheritdoc />
    public bool RequiresOrganizationMode => false;

    /// <inheritdoc />
    public async Task<IdentityAuthenticationResult> BeginAsync(
        IdentityAuthenticationAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        if (string.IsNullOrWhiteSpace(attempt.Email) || attempt.Password is null)
        {
            return new IdentityAuthenticationResult.Failed(
                IdentityFailureReason.MalformedAttempt,
                "Enter both an email address and a password.");
        }

        var email = attempt.Email.Trim().ToLowerInvariant();
        var throttleKey = attempt.ClientKey is { Length: > 0 } client ? $"{email}|{client}" : email;

        if (!throttle.TryBegin(throttleKey, out var retryAfter))
        {
            // Logged without the email, which is somebody's personal data and is not needed to see
            // that an instance is being guessed at.
            logger.LogWarning("A password sign-in was throttled after repeated failures");

            return new IdentityAuthenticationResult.Failed(
                IdentityFailureReason.Throttled,
                "Too many sign-in attempts. Try again shortly.",
                retryAfter);
        }

        var match = await (from user in database.Users
                           join identity in database.Identities on user.Id equals identity.UserId
                           where user.Email == email && identity.Provider == IdentityProviderKind.Password
                           select new { user, identity })
            .SingleOrDefaultAsync(cancellationToken);

        if (match?.identity.SecretHash is not { } storedHash)
        {
            // Equal work whether or not the account exists.
            hasher.SpendEquivalentWork();
            throttle.RecordFailure(throttleKey);

            return new IdentityAuthenticationResult.Failed(
                IdentityFailureReason.InvalidCredentials,
                GenericFailure);
        }

        var verification = hasher.Verify(storedHash, attempt.Password);

        if (verification == PasswordVerification.Failed)
        {
            throttle.RecordFailure(throttleKey);

            return new IdentityAuthenticationResult.Failed(
                IdentityFailureReason.InvalidCredentials,
                GenericFailure);
        }

        if (verification == PasswordVerification.SuccessRehashNeeded)
        {
            match.identity.SetSecretHash(hasher.Hash(attempt.Password));
        }

        match.identity.RecordUse();
        await database.SaveChangesAsync(cancellationToken);

        throttle.Reset(throttleKey);

        return new IdentityAuthenticationResult.Authenticated(new ExternalIdentity(
            IdentityProviderKind.Password,
            match.identity.ProviderUserId,
            match.user.Email,
            match.user.DisplayName));
    }

    /// <summary>
    /// Not a step this provider has. A credential sign-in finishes in <see cref="BeginAsync"/>.
    /// </summary>
    public Task<IdentityAuthenticationResult> CompleteAsync(
        IdentityAuthenticationAttempt attempt,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IdentityAuthenticationResult>(new IdentityAuthenticationResult.Failed(
            IdentityFailureReason.UnsupportedStep,
            "Password sign-in has no callback step."));

    /// <summary>
    /// Sets or replaces the password on an existing user, creating the password identity row if this
    /// user has only ever signed in through OAuth.
    /// </summary>
    /// <remarks>
    /// The plaintext reaches this method and nothing else: it is hashed here and the
    /// <see cref="Secret"/> goes out of scope. Nothing writes it anywhere, and no log statement in
    /// this class takes the attempt as an argument.
    /// </remarks>
    public async Task<bool> SetPasswordAsync(
        Guid userId,
        Configuration.Secret password,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (!CharterPasswordHasher.IsAcceptable(password))
        {
            return false;
        }

        var identity = await database.Identities.SingleOrDefaultAsync(
            row => row.UserId == userId && row.Provider == IdentityProviderKind.Password,
            cancellationToken);

        if (identity is null)
        {
            var exists = await database.Users.AnyAsync(row => row.Id == userId, cancellationToken);
            if (!exists)
            {
                return false;
            }

            database.Identities.Add(NewPasswordIdentity(userId, hasher.Hash(password)));
        }
        else
        {
            identity.SetSecretHash(hasher.Hash(password));
        }

        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// The password identity row for a user: subject is the user's own id, because the "provider"
    /// here is Charter itself and there is no external subject to record.
    /// </summary>
    public static Identity NewPasswordIdentity(Guid userId, string secretHash, DateTimeOffset? now = null)
        => Identity.Create(userId, IdentityProviderKind.Password, userId.ToString(), secretHash, now);
}
