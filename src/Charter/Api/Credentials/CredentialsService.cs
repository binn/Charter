using Charter.Api.Requests;
using Charter.Auth.Authorization;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Charter.Models;
using Charter.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ModelIdentifier = Charter.Models.ModelIdentifier;

namespace Charter.Api.Credentials;

/// <summary>
/// Settings → Credentials: list, create and revoke the model credentials of section 20b.2.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> The section 20b.3 chain reads <c>credential_grants</c>, and until
/// now no route in the application wrote a row to it. The only way to link a credential was to insert
/// encrypted ciphertext into Postgres by hand, which meant the resolution chain's first three tiers
/// were unreachable on every instance ever deployed. Half of "the default install cannot make a model
/// call" was the missing environment tier; this is the other half.
/// </para>
/// <para>
/// <strong>Authorisation, server-side.</strong> Section 7.4: engineers and administrators only, checked
/// here rather than in the endpoint lambda, and a credential belonging to another organisation is
/// answered as not found rather than as forbidden (section 7.3).
/// </para>
/// <para>
/// <strong>The secret goes one way.</strong> It arrives on the create body, is encrypted with
/// <c>CHARTER_CREDENTIAL_KEY</c> before it reaches the database, and no property on any response type
/// can carry it back out. Section 20b.2 is explicit that there is no reveal.
/// </para>
/// </remarks>
public sealed class CredentialsService
{
    /// <summary>The refusal an ordinary member reads.</summary>
    public const string EngineerOnly =
        "model credentials are managed by engineers and administrators";

    /// <summary>Section 20b.7's one-time caution, shown when a credential is opted into a pool.</summary>
    public const string SharedPoolWarning =
        "This credential will now pay for other people's requests. Using your own subscription for "
        + "your own work is ordinary use; letting other people's sessions run through it may not be, "
        + "depending on your provider's terms. Check them before you rely on this, and withdraw the "
        + "credential from the pool at any time.";

    private readonly CharterDbContext database;
    private readonly ICredentialProtector protector;
    private readonly ICredentialResolver resolver;
    private readonly InstanceModelCredentials instanceKeys;
    private readonly CharterConfig config;
    private readonly TimeProvider clock;
    private readonly ILogger<CredentialsService> logger;

    public CredentialsService(
        CharterDbContext database,
        ICredentialProtector protector,
        ICredentialResolver resolver,
        InstanceModelCredentials instanceKeys,
        CharterConfig config,
        TimeProvider clock,
        ILogger<CredentialsService> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(instanceKeys);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        this.database = database;
        this.protector = protector;
        this.resolver = resolver;
        this.instanceKeys = instanceKeys;
        this.config = config;
        this.clock = clock;
        this.logger = logger;
    }

    /// <summary>Whether a member may see or change this organisation's credentials (section 7.4).</summary>
    public static bool MayManage(MemberSnapshot member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return member.HasRole(MemberRole.Engineer) || member.HasRole(MemberRole.Admin);
    }

    /// <summary>
    /// Every credential this instance can authenticate with, stored and environment alike, plus
    /// whether the configured control-plane models can actually be served by one.
    /// </summary>
    public async Task<(CommandOutcome Outcome, CredentialListResponse? List)> ListAsync(
        MemberSnapshot member,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        if (!MayManage(member))
        {
            return (CommandOutcome.Forbidden(EngineerOnly), null);
        }

        var grants = await database.CredentialGrants
            .AsNoTracking()
            .Where(grant => grant.OrgId == member.OrgId)
            .OrderByDescending(grant => grant.CreatedAt)
            .ToListAsync(cancellationToken);

        var ownerIds = grants.Select(grant => grant.OwnerUserId).Distinct().ToList();

        var owners = await database.Users
            .AsNoTracking()
            .Where(user => ownerIds.Contains(user.Id))
            .Select(user => new { user.Id, user.DisplayName })
            .ToDictionaryAsync(user => user.Id, user => user.DisplayName, cancellationToken);

        var credentials = new List<CredentialResponse>(grants.Count + 2);

        foreach (var grant in grants)
        {
            credentials.Add(Project(grant, owners.GetValueOrDefault(grant.OwnerUserId)));
        }

        // The environment tier, shown alongside the rows. An operator who cannot see these cannot
        // tell a working instance from one that resolves nothing.
        foreach (var key in instanceKeys.Describe())
        {
            credentials.Add(Project(key));
        }

        return (
            CommandOutcome.Ok(),
            new CredentialListResponse
            {
                Credentials = credentials,
                SharedPoolAllowed = config.Models.AllowSharedPool,
                Models =
                [
                    await DescribeModelAsync("refine", Translate(config.Models.Refine), member, cancellationToken),
                    await DescribeModelAsync("teach", Translate(config.Models.Teach), member, cancellationToken),
                ],
            });
    }

    /// <summary>
    /// Links a credential, encrypted at rest, owned by the caller.
    /// </summary>
    /// <remarks>
    /// The owner is always the caller and never a body field. Section 20b.5's consent mechanics rest
    /// on a pooled credential having been offered by the person whose quota it spends, and an admin
    /// uploading somebody else's key on their behalf would make that consent a fiction.
    /// </remarks>
    public async Task<(CommandOutcome Outcome, CreateCredentialResponse? Created)> CreateAsync(
        MemberSnapshot member,
        CreateCredentialBody body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(body);

        if (!MayManage(member))
        {
            return (CommandOutcome.Forbidden(EngineerOnly), null);
        }

        if (body.Kind is not { } apiKind)
        {
            return (CommandOutcome.Invalid("choose which kind of credential this is"), null);
        }

        if (string.IsNullOrWhiteSpace(body.Secret))
        {
            return (CommandOutcome.Invalid("paste the key or token this credential uses"), null);
        }

        var kind = ToDomain(apiKind);
        var scope = body.Scope is ApiCredentialScope.SharedPool
            ? CredentialScope.SharedPool
            : CredentialScope.Personal;

        string? baseUrl = string.IsNullOrWhiteSpace(body.BaseUrl) ? null : body.BaseUrl.Trim();

        if (baseUrl is not null && !Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
        {
            return (CommandOutcome.Invalid("the base URL has to be an absolute http or https address"), null);
        }

        if (kind == CredentialKind.CustomOpenAiCompatible && baseUrl is null)
        {
            return (
                CommandOutcome.Invalid(
                    "a custom OpenAI-compatible credential needs a base URL — there is no public "
                    + "endpoint to fall back to"),
                null);
        }

        if (body.MaxSessionsPerDayFromOthers is < 0)
        {
            return (CommandOutcome.Invalid("a daily cap cannot be negative"), null);
        }

        var now = clock.GetUtcNow();

        var grant = CredentialGrant.Create(
            orgId: member.OrgId,
            ownerUserId: member.UserId,
            kind: kind,

            // Section 20b.2: ciphertext from here on. The plaintext exists only as the parameter
            // above, is never assigned to a field, and is never written to a log or a response.
            secretEncrypted: protector.Protect(body.Secret.Trim()),
            scope: scope,
            baseUrl: baseUrl,
            priority: body.Priority ?? 0,
            maxSessionsPerDayFromOthers: body.MaxSessionsPerDayFromOthers,
            now: now);

        database.CredentialGrants.Add(grant);

        database.AuditLogs.Add(AuditLog.Record(
            member.OrgId,
            CredentialAuditActions.Linked,
            targetType: "credential_grant",
            actorUserId: member.UserId,
            targetId: grant.Id.ToString(),
            now: now));

        await database.SaveChangesAsync(cancellationToken);

        // Kind and scope, never the value. This line is the record that a credential appeared, and
        // section 19 is unconditional about the other half of it.
        logger.LogInformation(
            "Credential grant {GrantId} linked for organisation {OrgId}: {Kind}, {Scope}.",
            grant.Id,
            member.OrgId,
            grant.Kind,
            grant.Scope);

        var ownerName = await database.Users
            .AsNoTracking()
            .Where(user => user.Id == member.UserId)
            .Select(user => user.DisplayName)
            .FirstOrDefaultAsync(cancellationToken);

        return (
            CommandOutcome.Ok(),
            new CreateCredentialResponse(
                Project(grant, ownerName),
                scope == CredentialScope.SharedPool ? SharedPoolWarning : null));
    }

    /// <summary>
    /// Revokes a credential. Immediate, and the chain stops offering it on the next resolution.
    /// </summary>
    public async Task<CommandOutcome> RevokeAsync(
        MemberSnapshot member,
        Guid credentialId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        if (!MayManage(member))
        {
            return CommandOutcome.Forbidden(EngineerOnly);
        }

        var grant = await database.CredentialGrants.FirstOrDefaultAsync(
            row => row.Id == credentialId && row.OrgId == member.OrgId,
            cancellationToken);

        // Section 7.3: another organisation's credential is not found, never forbidden.
        if (grant is null)
        {
            return CommandOutcome.NotFound();
        }

        if (grant.Status == CredentialStatus.Revoked)
        {
            return CommandOutcome.Ok();
        }

        var now = clock.GetUtcNow();

        grant.Revoke();

        database.AuditLogs.Add(AuditLog.Record(
            member.OrgId,
            CredentialAuditActions.Revoked,
            targetType: "credential_grant",
            actorUserId: member.UserId,
            targetId: grant.Id.ToString(),
            now: now));

        await database.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Credential grant {GrantId} revoked by {ActorUserId}.",
            grant.Id,
            member.UserId);

        return CommandOutcome.Ok();
    }

    /// <summary>
    /// Asks the real resolver whether the configured model can be served, and reports its own
    /// sentence when it cannot.
    /// </summary>
    /// <remarks>
    /// Deliberately the resolver rather than a rule of this screen's own. The point of the row is to
    /// answer "will a request work", and the only trustworthy answer is the one the refine job will
    /// get — including the remedy, which is the same sentence the job records when it fails.
    /// </remarks>
    private async Task<CredentialModelResponse> DescribeModelAsync(
        string purpose,
        ModelIdentifier model,
        MemberSnapshot member,
        CancellationToken cancellationToken)
    {
        var resolution = await resolver.ResolveAsync(
            new ModelCredentialQuery(model, member.UserId.ToString(), member.OrgId.ToString()),
            cancellationToken);

        return new CredentialModelResponse(
            purpose,
            model.Canonical,
            resolution.Resolved,
            resolution.Resolved ? null : resolution.Explanation);
    }

    private static ModelIdentifier Translate(Charter.Configuration.ModelIdentifier identifier)
        => ModelIdentifier.Parse(identifier.Qualified);

    private static CredentialResponse Project(CredentialGrant grant, string? ownerName) =>
        new()
        {
            Id = grant.Id.ToString(),
            Source = ApiCredentialSource.Grant,
            Kind = ToApi(grant.Kind),
            Scope = grant.Scope == CredentialScope.SharedPool
                ? ApiCredentialScope.SharedPool
                : ApiCredentialScope.Personal,
            Status = ToApi(grant.Status),
            OwnerName = ownerName,
            OwnerUserId = grant.OwnerUserId.ToString(),
            BaseUrl = grant.BaseUrl,
            Priority = grant.Priority,
            MaxSessionsPerDayFromOthers = grant.MaxSessionsPerDayFromOthers,
            OverflowEnabled = grant.OverflowEnabled,
            ExhaustedUntil = grant.ExhaustedUntil,
            InvalidReason = grant.InvalidReason,
            ExpiresAt = grant.ExpiresAt,
            CreatedAt = grant.CreatedAt,
            LastUsedAt = grant.LastUsedAt,
        };

    private static CredentialResponse Project(InstanceModelKeyStatus key) =>
        new()
        {
            Id = InstanceModelCredentials.IdPrefix + key.Variable,
            Source = ApiCredentialSource.Environment,
            Variable = key.Variable,
            Kind = ToApi(key.Kind),
            Scope = ApiCredentialScope.Personal,
            Status = ToApi(key.Status),

            // No owner, no priority, no created-at: an environment variable has none of them, and
            // inventing values would make the row look like something that can be revoked.
            ExhaustedUntil = key.ExhaustedUntil,
            InvalidReason = key.InvalidReason,
            LastUsedAt = key.LastUsedAt,
        };

    private static ApiCredentialKind ToApi(CredentialKind kind) => kind switch
    {
        CredentialKind.AnthropicOauth => ApiCredentialKind.AnthropicOauth,
        CredentialKind.AnthropicApiKey => ApiCredentialKind.AnthropicApiKey,
        CredentialKind.OpenAiOauth => ApiCredentialKind.OpenAiOauth,
        CredentialKind.OpenAiApiKey => ApiCredentialKind.OpenAiApiKey,
        CredentialKind.GoogleApiKey => ApiCredentialKind.GoogleApiKey,
        CredentialKind.XaiApiKey => ApiCredentialKind.XaiApiKey,
        CredentialKind.OpenRouterKey => ApiCredentialKind.OpenRouterKey,
        CredentialKind.CursorApiKey => ApiCredentialKind.CursorApiKey,
        CredentialKind.CustomOpenAiCompatible => ApiCredentialKind.CustomOpenAiCompatible,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown credential kind."),
    };

    private static ApiCredentialKind ToApi(ModelCredentialKind kind) => kind switch
    {
        ModelCredentialKind.AnthropicOAuth => ApiCredentialKind.AnthropicOauth,
        ModelCredentialKind.AnthropicApiKey => ApiCredentialKind.AnthropicApiKey,
        ModelCredentialKind.OpenAiOAuth => ApiCredentialKind.OpenAiOauth,
        ModelCredentialKind.OpenAiApiKey => ApiCredentialKind.OpenAiApiKey,
        ModelCredentialKind.GoogleApiKey => ApiCredentialKind.GoogleApiKey,
        ModelCredentialKind.XaiApiKey => ApiCredentialKind.XaiApiKey,
        ModelCredentialKind.OpenRouterKey => ApiCredentialKind.OpenRouterKey,
        ModelCredentialKind.CursorApiKey => ApiCredentialKind.CursorApiKey,
        ModelCredentialKind.CustomOpenAiCompatible => ApiCredentialKind.CustomOpenAiCompatible,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown credential kind."),
    };

    private static ApiCredentialStatus ToApi(ModelCredentialStatus status) => status switch
    {
        ModelCredentialStatus.Active => ApiCredentialStatus.Active,
        ModelCredentialStatus.Exhausted => ApiCredentialStatus.Exhausted,
        ModelCredentialStatus.Invalid => ApiCredentialStatus.Invalid,
        ModelCredentialStatus.Revoked => ApiCredentialStatus.Revoked,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown credential status."),
    };

    private static ApiCredentialStatus ToApi(CredentialStatus status) => status switch
    {
        CredentialStatus.Active => ApiCredentialStatus.Active,
        CredentialStatus.Exhausted => ApiCredentialStatus.Exhausted,
        CredentialStatus.Invalid => ApiCredentialStatus.Invalid,
        CredentialStatus.Revoked => ApiCredentialStatus.Revoked,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown credential status."),
    };

    private static CredentialKind ToDomain(ApiCredentialKind kind) => kind switch
    {
        ApiCredentialKind.AnthropicOauth => CredentialKind.AnthropicOauth,
        ApiCredentialKind.AnthropicApiKey => CredentialKind.AnthropicApiKey,
        ApiCredentialKind.OpenAiOauth => CredentialKind.OpenAiOauth,
        ApiCredentialKind.OpenAiApiKey => CredentialKind.OpenAiApiKey,
        ApiCredentialKind.GoogleApiKey => CredentialKind.GoogleApiKey,
        ApiCredentialKind.XaiApiKey => CredentialKind.XaiApiKey,
        ApiCredentialKind.OpenRouterKey => CredentialKind.OpenRouterKey,
        ApiCredentialKind.CursorApiKey => CredentialKind.CursorApiKey,
        ApiCredentialKind.CustomOpenAiCompatible => CredentialKind.CustomOpenAiCompatible,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown credential kind."),
    };
}

/// <summary>The audit verbs the credentials routes write (section 7.3, guardrail 5).</summary>
public static class CredentialAuditActions
{
    /// <summary>Section 20b.2: a credential was linked to this organisation.</summary>
    public const string Linked = "credential.linked";

    /// <summary>Section 20b.2: revocation, which is immediate.</summary>
    public const string Revoked = "credential.revoked";
}
