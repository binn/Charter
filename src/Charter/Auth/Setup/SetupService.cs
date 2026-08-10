using Charter.Auth.Audit;
using Charter.Auth.Providers;
using Charter.Configuration;
using Charter.Data;
using Microsoft.EntityFrameworkCore;

namespace Charter.Auth.Setup;

/// <summary>What the setup route was asked to do (section 30.1).</summary>
public sealed record SetupRequest
{
    /// <summary>The value the operator read out of the container log.</summary>
    public required string Token { get; init; }

    public required string Email { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Never stored, never logged; hashed and discarded inside <see cref="SetupService"/>.</summary>
    public required Secret Password { get; init; }

    /// <summary>Optional. Section 30.2 asks for it again on the dashboard checklist.</summary>
    public string? OrganizationName { get; init; }
}

/// <summary>Why setup was refused.</summary>
public enum SetupRejection
{
    /// <summary>A user already exists. Setup mode cannot be re-entered (section 30.1).</summary>
    AlreadyCompleted,

    /// <summary>The token was wrong, expired, or already redeemed.</summary>
    Token,

    /// <summary>The email address is not usable.</summary>
    Email,

    /// <summary>The display name is missing.</summary>
    DisplayName,

    /// <summary>The password is shorter than <see cref="CharterPasswordHasher.MinimumPasswordLength"/>.</summary>
    Password,
}

/// <summary>The outcome of redeeming a setup token.</summary>
public abstract record SetupResult
{
    private SetupResult()
    {
    }

    public sealed record Completed(Guid OrganizationId, Guid UserId, Guid MemberId) : SetupResult;

    public sealed record Rejected(SetupRejection Reason, string Message) : SetupResult;
}

/// <summary>
/// Redeems the one-time setup token and seeds the instance (section 30.1).
/// </summary>
/// <remarks>
/// The token creates exactly one admin account and then expires. Three separate things enforce that:
/// the token store is single-use, the "are there any users" check runs again inside the transaction,
/// and the unique index on <c>users.email</c> is the last word if two requests race.
/// </remarks>
public sealed class SetupService
{
    private readonly CharterDbContext database;
    private readonly SetupTokenStore tokens;
    private readonly SetupState state;
    private readonly SetupModeService mode;
    private readonly ICharterPasswordHasher hasher;
    private readonly CharterConfig config;
    private readonly TimeProvider clock;

    public SetupService(
        CharterDbContext database,
        SetupTokenStore tokens,
        SetupState state,
        SetupModeService mode,
        ICharterPasswordHasher hasher,
        CharterConfig config,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(clock);

        this.database = database;
        this.tokens = tokens;
        this.state = state;
        this.mode = mode;
        this.hasher = hasher;
        this.config = config;
        this.clock = clock;
    }

    /// <summary>Validates the request and, if it holds, seeds the instance.</summary>
    public async Task<SetupResult> CompleteAsync(
        SetupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await mode.IsSetupRequiredAsync(cancellationToken))
        {
            return new SetupResult.Rejected(
                SetupRejection.AlreadyCompleted,
                "This instance has already been set up. Sign in instead.");
        }

        if (!IsPlausibleEmail(request.Email))
        {
            return new SetupResult.Rejected(SetupRejection.Email, "Enter a valid email address.");
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return new SetupResult.Rejected(SetupRejection.DisplayName, "Enter the name you want to be known by.");
        }

        if (!CharterPasswordHasher.IsAcceptable(request.Password))
        {
            return new SetupResult.Rejected(
                SetupRejection.Password,
                $"Choose a password of at least {CharterPasswordHasher.MinimumPasswordLength} characters.");
        }

        // Consumed before anything is written: a redemption that then fails must not leave the token
        // usable, or a race becomes a second chance at claiming the instance.
        var rejection = tokens.TryConsume(request.Token);
        if (rejection != SetupTokenRejection.None)
        {
            return new SetupResult.Rejected(SetupRejection.Token, Describe(rejection));
        }

        var seeded = PersonalOrganizationSeeder.Seed(
            request.OrganizationName ?? PersonalOrganizationSeeder.DefaultOrganizationName,
            request.Email,
            request.DisplayName,
            hasher.Hash(request.Password),
            config.Mode,
            clock.GetUtcNow());

        database.Organizations.Add(seeded.Organization);
        database.Users.Add(seeded.User);
        database.Members.Add(seeded.Member);
        database.Identities.Add(seeded.Identity);
        database.AutoDispatchPolicies.Add(seeded.AutoDispatch);

        database.AuditLogs.Add(AuditWriter.ToRow(
            new AuditEntry
            {
                OrgId = seeded.Organization.Id,
                ActorUserId = seeded.User.Id,
                Action = AuditActions.SetupCompleted,
                TargetType = "organization",
                TargetId = seeded.Organization.Id.ToString(),
                Metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["mode"] = seeded.Organization.Mode.ToString().ToLowerInvariant(),
                    ["roles"] = string.Join(',', seeded.Member.Roles),
                },
            },
            clock.GetUtcNow()));

        await database.SaveChangesAsync(cancellationToken);

        state.MarkCompleted();

        return new SetupResult.Completed(seeded.Organization.Id, seeded.User.Id, seeded.Member.Id);
    }

    private static string Describe(SetupTokenRejection rejection) => rejection switch
    {
        SetupTokenRejection.Expired =>
            "That setup token has expired. Restart Charter and use the new token from the logs.",
        SetupTokenRejection.AlreadyUsed =>
            "That setup token has already been used.",
        SetupTokenRejection.NotIssued =>
            "No setup token is outstanding for this instance.",
        _ => "That setup token is not correct. Check the container logs.",
    };

    /// <summary>
    /// A deliberately loose check. Address validation beyond "one @ with something either side" is a
    /// well-known way to reject real addresses, and the account is created by the operator anyway.
    /// </summary>
    private static bool IsPlausibleEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        var trimmed = email.Trim();
        var at = trimmed.IndexOf('@', StringComparison.Ordinal);

        return at > 0
               && at == trimmed.LastIndexOf('@')
               && at < trimmed.Length - 1
               && !trimmed.Any(char.IsWhiteSpace)
               && trimmed.Length <= 320;
    }
}
