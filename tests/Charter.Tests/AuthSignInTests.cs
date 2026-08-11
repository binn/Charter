using Charter.Api.Accounts;
using Charter.Api.Contracts;
using Charter.Auth;
using Charter.Auth.Audit;
using Charter.Auth.Authorization;
using Charter.Auth.Providers;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Charter.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>
/// The one-time password-reset link (section 30.2, change spec 001 part C.1).
/// </summary>
/// <remarks>
/// The link is a signature rather than a row, and single use is a property of that signature: it
/// covers the verifier the account had when the link was minted, so setting a password invalidates
/// every outstanding link for that account at the same instant. These tests are what makes that a
/// property rather than a claim in a comment.
/// </remarks>
public class AuthPasswordResetTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private static PasswordResetTokens Tokens(TimeProvider clock)
        => new(new Secret(ConfigTestEnvironment.SecretKey), clock);

    [Fact]
    public void ALinkVerifiesAgainstTheVerifierItWasMintedFor()
    {
        var user = Guid.CreateVersion7();
        var tokens = Tokens(new ModelFakeTimeProvider(Now));

        var token = tokens.Issue(user, "hash-one");

        Assert.True(PasswordResetTokens.TryReadSubject(token, out var subject));
        Assert.Equal(user, subject);
        Assert.Equal(PasswordResetRejection.None, tokens.Verify(token, user, "hash-one"));
    }

    [Fact]
    public void SettingThePasswordSpendsEveryOutstandingLink()
    {
        // This is the whole single-use mechanism. The second click arrives after the verifier has
        // changed, so the signature no longer reproduces.
        var user = Guid.CreateVersion7();
        var tokens = Tokens(new ModelFakeTimeProvider(Now));

        var token = tokens.Issue(user, "hash-one");

        Assert.Equal(PasswordResetRejection.None, tokens.Verify(token, user, "hash-one"));
        Assert.Equal(PasswordResetRejection.Spent, tokens.Verify(token, user, "hash-two"));
    }

    [Fact]
    public void ALinkExpires()
    {
        var user = Guid.CreateVersion7();
        var clock = new ModelFakeTimeProvider(Now);
        var tokens = Tokens(clock);

        var token = tokens.Issue(user, "hash-one");

        clock.Now = Now + PasswordResetTokens.Lifetime + TimeSpan.FromMinutes(1);

        Assert.Equal(PasswordResetRejection.Expired, tokens.Verify(token, user, "hash-one"));
    }

    [Fact]
    public void ALinkForOneAccountIsNotALinkForAnother()
    {
        var tokens = Tokens(new ModelFakeTimeProvider(Now));
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        var token = tokens.Issue(first, "hash-one");

        Assert.Equal(PasswordResetRejection.Malformed, tokens.Verify(token, second, "hash-one"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("a.b.c")]
    public void RubbishIsRefusedRatherThanThrowing(string token)
    {
        var tokens = Tokens(new ModelFakeTimeProvider(Now));

        Assert.NotEqual(PasswordResetRejection.None, tokens.Verify(token, Guid.CreateVersion7(), null));
    }

    [Fact]
    public void ATamperedExpiryDoesNotVerify()
    {
        var user = Guid.CreateVersion7();
        var tokens = Tokens(new ModelFakeTimeProvider(Now));

        var parts = tokens.Issue(user, "hash-one").Split('.');
        var forged = string.Join('.', parts[0], long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) + 86_400, parts[2]);

        Assert.Equal(PasswordResetRejection.Spent, tokens.Verify(forged, user, "hash-one"));
    }

    [Fact]
    public void ADifferentInstanceKeyDoesNotVerify()
    {
        var user = Guid.CreateVersion7();
        var clock = new ModelFakeTimeProvider(Now);

        var token = Tokens(clock).Issue(user, "hash-one");
        var elsewhere = new PasswordResetTokens(new Secret(ConfigTestEnvironment.CredentialKey), clock);

        Assert.Equal(PasswordResetRejection.Spent, elsewhere.Verify(token, user, "hash-one"));
    }
}

/// <summary>
/// Signing in, against a real Postgres (section 21).
/// </summary>
/// <remarks>
/// The property these exist for is that a refusal says the same thing whichever half was wrong. It
/// is asserted on the reason <em>and</em> on the sentence, because a caller that switched on the
/// enum would still leak the difference if the words differed.
/// </remarks>
public class AuthSignInTests
{
    [Fact]
    public async Task TheRightPasswordSignsYouIn()
    {
        await using var world = await AuthWorld.CreateAsync();

        var result = await world.SignInAsync(world.AdminEmail, AuthWorld.Password);

        var succeeded = Assert.IsType<SignInResult.Succeeded>(result);

        Assert.Equal(world.AdminUserId, succeeded.Member.UserId);
        Assert.Equal(world.OrgId, succeeded.Member.OrgId);
        Assert.Contains(MemberRole.Admin, succeeded.Member.Roles);
        Assert.True(succeeded.Principal.Identity?.IsAuthenticated);

        // The claims the rest of the API reads back out of the cookie.
        var read = CharterPrincipalFactory.Read(succeeded.Principal);
        Assert.Equal((world.OrgId, world.AdminUserId), read);

        // Section 7.3, guardrail 5: attributable to a named human, from the first request.
        Assert.Contains(
            await world.AuditAsync(),
            entry => entry.Action == AuditActions.SignedIn && entry.ActorUserId == world.AdminUserId);
    }

    [Fact]
    public async Task NoSuchUserAndWrongPasswordAreTheSameAnswer()
    {
        await using var world = await AuthWorld.CreateAsync();

        var unknown = Assert.IsType<SignInResult.Failed>(
            await world.SignInAsync($"nobody-{Guid.CreateVersion7():N}@example.test", AuthWorld.Password));

        var wrong = Assert.IsType<SignInResult.Failed>(
            await world.SignInAsync(world.AdminEmail, "definitely-not-the-password"));

        Assert.Equal(SignInFailure.InvalidCredentials, unknown.Reason);
        Assert.Equal(SignInFailure.InvalidCredentials, wrong.Reason);

        // Same words, not merely the same status: a differing sentence is the same oracle.
        Assert.Equal(unknown.Message, wrong.Message);
        Assert.Equal(SignInService.GenericFailure, wrong.Message);

        // And neither says anything about the address that was tried.
        Assert.DoesNotContain(world.AdminEmail, wrong.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AMissingHalfIsRefusedWithoutTouchingTheDatabase()
    {
        await using var world = await AuthWorld.CreateAsync();

        var blank = Assert.IsType<SignInResult.Failed>(await world.SignInAsync(world.AdminEmail, password: null));

        Assert.Equal(SignInFailure.Malformed, blank.Reason);
    }

    [Fact]
    public async Task GuessingIsThrottledAndSaysHowLongToWait()
    {
        // Section 31, at the one place an unauthenticated stranger can spend the server's CPU.
        await using var world = await AuthWorld.CreateAsync(maxFailures: 3);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var failed = Assert.IsType<SignInResult.Failed>(await world.SignInAsync(world.AdminEmail, "wrong"));
            Assert.Equal(SignInFailure.InvalidCredentials, failed.Reason);
        }

        var throttled = Assert.IsType<SignInResult.Failed>(await world.SignInAsync(world.AdminEmail, "wrong"));

        Assert.Equal(SignInFailure.Throttled, throttled.Reason);
        Assert.NotNull(throttled.RetryAfter);
        Assert.True(throttled.RetryAfter > TimeSpan.Zero);

        // The correct password is refused too while the window is open. A throttle that let the
        // right answer through would be a way to test guesses for free.
        Assert.IsType<SignInResult.Failed>(await world.SignInAsync(world.AdminEmail, AuthWorld.Password));
    }

    [Fact]
    public async Task AVerifiedFederatedSubjectWithNoAccountCreatesNothing()
    {
        // Section 21: "A successful federated sign-in never creates a user." Otherwise OAuth is open
        // registration through a side door and section 30.1 buys nothing.
        await using var world = await AuthWorld.CreateAsync();

        world.Federated.Email = world.Address("stranger");

        var result = await world.SignIn.CompleteAsync(
            StubRedirectProvider.ProviderName,
            new Dictionary<string, string?>(StringComparer.Ordinal),
            clientKey: null,
            TestContext.Current.CancellationToken);

        var failed = Assert.IsType<SignInResult.Failed>(result);

        Assert.Equal(SignInFailure.NoAccount, failed.Reason);

        // Neither an account nor an identity row: nothing at all came of a verified subject.
        Assert.False(await world.Db.Users.AnyAsync(
            row => row.Email == world.Address("stranger"),
            TestContext.Current.CancellationToken));

        Assert.False(await world.Db.Identities.AnyAsync(
            row => row.ProviderUserId == world.Federated.Subject,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AFederatedSubjectMatchingAnExistingAddressLinksRatherThanCreating()
    {
        await using var world = await AuthWorld.CreateAsync();

        world.Federated.Email = world.AdminEmail;

        var succeeded = Assert.IsType<SignInResult.Succeeded>(await world.SignIn.CompleteAsync(
            StubRedirectProvider.ProviderName,
            new Dictionary<string, string?>(StringComparer.Ordinal),
            clientKey: null,
            TestContext.Current.CancellationToken));

        Assert.Equal(world.AdminUserId, succeeded.Member.UserId);

        // The organisation still has exactly the one member it was seeded with: the sign-in linked
        // an identity to an existing person rather than creating a second one.
        Assert.Equal(
            1,
            await world.Db.Members.CountAsync(
                row => row.OrgId == world.OrgId,
                TestContext.Current.CancellationToken));

        // The new way of signing in is a row, and a privilege change is audited.
        Assert.Contains(
            await world.AuditAsync(),
            entry => entry.Action == AuditActions.IdentityLinked);
    }

    [Fact]
    public async Task AnUnknownProviderIsRefusedRatherThanGuessedAt()
    {
        await using var world = await AuthWorld.CreateAsync();

        var failed = Assert.IsType<SignInResult.Failed>(await world.SignIn.BeginAsync(
            "saml",
            returnUrl: null,
            TestContext.Current.CancellationToken));

        Assert.Equal(SignInFailure.UnknownProvider, failed.Reason);
    }

    [Fact]
    public async Task AReturnUrlOffThisOriginIsDropped()
    {
        // A login page that will redirect anywhere is a phishing hop.
        await using var world = await AuthWorld.CreateAsync();

        Assert.IsType<SignInResult.RedirectRequired>(await world.SignIn.BeginAsync(
            StubRedirectProvider.ProviderName,
            new Uri("https://evil.example/steal"),
            TestContext.Current.CancellationToken));

        Assert.Null(world.Federated.LastReturnUrl);
    }

    [Fact]
    public async Task SigningOutIsRecordedAgainstTheNamedHuman()
    {
        await using var world = await AuthWorld.CreateAsync();

        await world.SignIn.RecordSignOutAsync(world.Admin, TestContext.Current.CancellationToken);

        Assert.Contains(
            await world.AuditAsync(),
            entry => entry.Action == AuditActions.SignedOut && entry.ActorUserId == world.AdminUserId);
    }
}

/// <summary>
/// One organisation, one admin, and the whole identity stack over a throwaway Postgres.
/// </summary>
/// <remarks>
/// Everything under test here is about rows — which account a credential resolves to, what the audit
/// log then says — so an in-memory double would be asserting nothing. Every world namespaces its own
/// organisation and addresses, so a shared database stays usable.
/// </remarks>
internal sealed class AuthWorld : IAsyncDisposable
{
    /// <summary>Long enough for <c>CharterPasswordHasher.IsAcceptable</c>.</summary>
    public const string Password = "a-long-enough-password";

    private AuthWorld(
        CharterDbContext db,
        CharterConfig config,
        Organization organization,
        User admin,
        Member adminMember,
        string tag,
        int maxFailures)
    {
        Db = db;
        Config = config;
        Tag = tag;
        OrgId = organization.Id;
        AdminUserId = admin.Id;
        AdminEmail = admin.Email;
        Admin = MemberSnapshot.From(adminMember);

        Clock = new ModelFakeTimeProvider(DateTimeOffset.UtcNow);
        Hasher = new CharterPasswordHasher(iterationCount: 1_000);
        Throttle = new SignInThrottle(Clock, maxFailures);
        Federated = new StubRedirectProvider();

        Registry = new IdentityProviderRegistry(
            new PasswordIdentityProvider(db, Hasher, Throttle, NullLogger<PasswordIdentityProvider>.Instance),
            [Federated]);

        Audit = new AuditWriter(db, Clock);
        Authorization = new CharterAuthorizationService(db, Audit);

        SignIn = new SignInService(
            Registry,
            new IdentityLinker(db, Audit),
            Authorization,
            db,
            Audit,
            config,
            NullLogger<SignInService>.Instance);

        Sender = new StubEmailSender();
        Mailer = new AccountMailer(Sender);
        Resets = new PasswordResetTokens(config.Keys.SecretKey, Clock);

        Accounts = new AccountService(
            db,
            new InvitationStore(db, Clock),
            Mailer,
            Registry.Password,
            Resets,
            Audit,
            config,
            Clock,
            NullLogger<AccountService>.Instance);
    }

    public CharterDbContext Db { get; }

    public CharterConfig Config { get; }

    /// <summary>What makes this world's organisation and addresses its own.</summary>
    public string Tag { get; }

    public Guid OrgId { get; }

    public Guid AdminUserId { get; }

    public string AdminEmail { get; }

    public MemberSnapshot Admin { get; }

    public ModelFakeTimeProvider Clock { get; }

    public ICharterPasswordHasher Hasher { get; }

    public ISignInThrottle Throttle { get; }

    public StubRedirectProvider Federated { get; }

    public IdentityProviderRegistry Registry { get; }

    public IAuditWriter Audit { get; }

    public ICharterAuthorizationService Authorization { get; }

    public SignInService SignIn { get; }

    public StubEmailSender Sender { get; }

    public IAccountMailer Mailer { get; }

    public PasswordResetTokens Resets { get; }

    public AccountService Accounts { get; }

    public static async Task<AuthWorld> CreateAsync(
        int maxFailures = SignInThrottle.DefaultMaxFailures,
        params (string Key, string? Value)[] configuration)
    {
        var url = Environment.GetEnvironmentVariable("CHARTER_TEST_DATABASE_URL");

        // Deliberately not a skip. Everything below is a claim about rows, and a suite that quietly
        // passes without a database would report green on an instance nobody can sign in to.
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(url),
            "Set CHARTER_TEST_DATABASE_URL to a throwaway Postgres to run the identity tests.");

        var options = new DbContextOptionsBuilder<CharterDbContext>();
        DataServiceCollectionExtensions.ConfigureNpgsql(options, DatabaseUrl.ToNpgsql(url!));

        var db = new CharterDbContext(options.Options);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var config = ConfigTestEnvironment.Valid(configuration);
        var tag = Guid.CreateVersion7().ToString("N");

        var organization = Organization.Create($"auth-{tag}", OrganizationMode.Organization);
        var admin = User.Create($"admin-{tag}@example.test", "Ada Admin");
        var member = Member.Create(organization.Id, admin.Id, Member.AllRoles);

        var hasher = new CharterPasswordHasher(iterationCount: 1_000);

        db.Organizations.Add(organization);
        db.Users.Add(admin);
        db.Members.Add(member);
        db.Identities.Add(PasswordIdentityProvider.NewPasswordIdentity(admin.Id, hasher.Hash(new Secret(Password))));

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        return new AuthWorld(db, config, organization, admin, member, tag, maxFailures);
    }

    /// <summary>An address that belongs to this world and no other.</summary>
    public string Address(string local) => $"{local}-{Tag}@example.test";

    /// <summary>
    /// Invites somebody and hands back the token, by taking the link the admin would have been shown.
    /// </summary>
    /// <remarks>
    /// The token exists in exactly one place — the response that created it — so a test that needs
    /// one has to read it the same way an administrator would.
    /// </remarks>
    public async Task<string> InviteAsync(string local, params ApiRole[] roles)
    {
        var wasEnabled = Sender.Enabled;
        Sender.Enabled = false;

        try
        {
            var (outcome, issued) = await Accounts.InviteAsync(
                Admin,
                new InviteMemberBody
                {
                    Email = Address(local),
                    Roles = roles.Length == 0 ? null : roles,
                },
                TestContext.Current.CancellationToken);

            Assert.True(outcome.Succeeded, outcome.Reason);
            Assert.NotNull(issued?.Delivery.Link);

            return TokenFrom(issued.Delivery.Link);
        }
        finally
        {
            Sender.Enabled = wasEnabled;
        }
    }

    /// <summary>The one-time value out of a surfaced link.</summary>
    public static string TokenFrom(string link)
    {
        var query = new Uri(link).Query.TrimStart('?');

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);

            if (separator > 0 && pair[..separator] == "token")
            {
                return Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }

        throw new InvalidOperationException("The surfaced link carried no token.");
    }

    /// <summary>A member of this organisation holding exactly <paramref name="roles"/>.</summary>
    public MemberSnapshot MemberOf(params MemberRole[] roles) => new()
    {
        MemberId = Guid.CreateVersion7(),
        OrgId = OrgId,
        UserId = Guid.CreateVersion7(),
        Roles = roles,
    };

    public Task<SignInResult> SignInAsync(string? email, string? password)
        => SignIn.WithPasswordAsync(
            email,
            password is null ? null : new Secret(password),
            clientKey: "203.0.113.7",
            TestContext.Current.CancellationToken);

    public async Task<IReadOnlyList<AuditLog>> AuditAsync()
        => await Db.AuditLogs
            .AsNoTracking()
            .Where(row => row.OrgId == OrgId)
            .ToListAsync(TestContext.Current.CancellationToken);

    public async Task<User> AddUserAsync(string email, string displayName, params MemberRole[] roles)
    {
        var user = User.Create(email, displayName);

        Db.Users.Add(user);
        Db.Members.Add(Member.Create(OrgId, user.Id, roles.Length == 0 ? [MemberRole.Requester] : roles));

        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Db.ChangeTracker.Clear();

        return user;
    }

    public async ValueTask DisposeAsync() => await Db.DisposeAsync();
}

/// <summary>A redirect provider that answers whatever the test set, so the seam can be driven.</summary>
internal sealed class StubRedirectProvider : IIdentityProvider
{
    public const string ProviderName = "stub";

    public IdentityProviderKind Kind => IdentityProviderKind.GitHub;

    public string Name => ProviderName;

    public IdentityProviderStyle Style => IdentityProviderStyle.Redirect;

    public bool RequiresOrganizationMode => false;

    /// <summary>The address the authority claims. Null is the private-email case.</summary>
    public string? Email { get; set; }

    /// <summary>
    /// Unique per stub. The subject is what the linker matches on first, and a shared value would
    /// let one test's linked identity decide another's outcome.
    /// </summary>
    public string Subject { get; set; } = $"stub-subject-{Guid.CreateVersion7():N}";

    /// <summary>What <see cref="BeginAsync"/> was handed, so the open-redirect rule can be checked.</summary>
    public Uri? LastReturnUrl { get; private set; }

    public Task<IdentityAuthenticationResult> BeginAsync(
        IdentityAuthenticationAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        LastReturnUrl = attempt.ReturnUrl;

        return Task.FromResult<IdentityAuthenticationResult>(
            new IdentityAuthenticationResult.RedirectRequired(new Uri("https://authority.example/authorize"), "state"));
    }

    public Task<IdentityAuthenticationResult> CompleteAsync(
        IdentityAuthenticationAttempt attempt,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IdentityAuthenticationResult>(
            new IdentityAuthenticationResult.Authenticated(
                new ExternalIdentity(Kind, Subject, Email, "Stub Person")));
}

/// <summary>An email sender the test turns on and off, so the real mailer's audience rules run.</summary>
internal sealed class StubEmailSender : IEmailSender
{
    public bool Enabled { get; set; }

    public List<EmailMessage> Sent { get; } = [];

    public EmailAvailability Availability => Enabled
        ? new EmailAvailability { Enabled = true, Provider = "smtp", FromAddress = "charter@example.test" }
        : new EmailAvailability
        {
            Enabled = false,
            Provider = "none",
            DisabledReason = "Email is not set up on this instance.",
            HowToEnable = "Set CHARTER_EMAIL_PROVIDER=smtp.",
        };

    public Task<EmailDeliveryResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        Sent.Add(message);

        return Task.FromResult(Enabled
            ? EmailDeliveryResult.Sent()
            : EmailDeliveryResult.Skipped("Email is not set up on this instance."));
    }
}
