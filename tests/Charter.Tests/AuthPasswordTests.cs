using Charter.Auth.Providers;
using Charter.Configuration;
using Charter.Domain;

namespace Charter.Tests;

/// <summary>
/// Password hashing and verification (section 21).
/// </summary>
/// <remarks>
/// Most of these run against a hasher with a deliberately low iteration count, because what is being
/// tested is the wrapper's behaviour rather than PBKDF2. One test uses the shipping parameters, so a
/// change to the defaults is still caught.
/// </remarks>
public class AuthPasswordTests
{
    private const int FastIterations = 1_000;

    private static readonly Secret Correct = new("correct horse battery staple");
    private static readonly Secret Wrong = new("correct horse battery stapler");

    [Fact]
    public void AHashVerifiesAgainstItsOwnPassword()
    {
        var hasher = new CharterPasswordHasher(FastIterations);
        var hash = hasher.Hash(Correct);

        Assert.Equal(PasswordVerification.Success, hasher.Verify(hash, Correct));
    }

    [Fact]
    public void AWrongPasswordDoesNotVerify()
    {
        var hasher = new CharterPasswordHasher(FastIterations);

        Assert.Equal(PasswordVerification.Failed, hasher.Verify(hasher.Hash(Correct), Wrong));
    }

    [Fact]
    public void TheStoredValueIsAHashAndNotThePassword()
    {
        var hasher = new CharterPasswordHasher(FastIterations);
        var hash = hasher.Hash(Correct);

        Assert.DoesNotContain("correct horse", hash, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("battery", hash, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TwoHashesOfTheSamePasswordDiffer()
    {
        // Salted. Without this, equal hashes would tell an attacker with a database dump which
        // accounts share a password.
        var hasher = new CharterPasswordHasher(FastIterations);

        Assert.NotEqual(hasher.Hash(Correct), hasher.Hash(Correct));
    }

    [Fact]
    public void AHashFromWeakerParametersVerifiesAndAsksToBeRewritten()
    {
        var old = new CharterPasswordHasher(FastIterations);
        var current = new CharterPasswordHasher(FastIterations * 10);

        Assert.Equal(PasswordVerification.SuccessRehashNeeded, current.Verify(old.Hash(Correct), Correct));
    }

    [Fact]
    public void AnUnreadableStoredHashFailsRatherThanThrows()
    {
        var hasher = new CharterPasswordHasher(FastIterations);

        Assert.Equal(PasswordVerification.Failed, hasher.Verify("not-a-hash", Correct));
        Assert.Equal(PasswordVerification.Failed, hasher.Verify("AQAAAA==", Correct));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("elevenchar")]
    public void APasswordShorterThanTheMinimumIsRefused(string candidate)
    {
        var hasher = new CharterPasswordHasher(FastIterations);
        var password = new Secret(candidate);

        Assert.False(CharterPasswordHasher.IsAcceptable(password));
        Assert.Throws<ArgumentException>(() => hasher.Hash(password));
    }

    [Fact]
    public void AnAbsurdlyLongPasswordIsRefusedRatherThanHashed()
    {
        // PBKDF2 over a megabyte of submitted text is a denial of service with extra steps.
        Assert.False(CharterPasswordHasher.IsAcceptable(new Secret(new string('a', 1024))));
    }

    [Fact]
    public void TheShippingParametersStillRoundTrip()
    {
        var hasher = new CharterPasswordHasher();

        Assert.Equal(600_000, CharterPasswordHasher.DefaultIterationCount);
        Assert.Equal(12, CharterPasswordHasher.MinimumPasswordLength);
        Assert.Equal(PasswordVerification.Success, hasher.Verify(hasher.Hash(Correct), Correct));
    }

    [Fact]
    public void ASignInAttemptNeverRendersThePassword()
    {
        // Records print their properties. A password typed into a log line is the failure this
        // guards against, and Secret is what makes the careless thing safe.
        var attempt = new IdentityAuthenticationAttempt
        {
            Email = "someone@example.com",
            Password = Correct,
        };

        var rendered = attempt.ToString();

        Assert.DoesNotContain("correct horse", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Secret.Placeholder, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePasswordIdentityRowNamesTheUserAndCarriesTheHash()
    {
        var userId = Guid.CreateVersion7();
        var identity = PasswordIdentityProvider.NewPasswordIdentity(userId, "stored-hash");

        Assert.Equal(IdentityProviderKind.Password, identity.Provider);
        Assert.Equal(userId.ToString(), identity.ProviderUserId);
        Assert.Equal("stored-hash", identity.SecretHash);
    }

    [Fact]
    public void AFederatedIdentityCarriesNoSecret()
    {
        var identity = Identity.Create(Guid.CreateVersion7(), IdentityProviderKind.GitHub, "12345");

        Assert.Null(identity.SecretHash);
    }
}

/// <summary>Section 31: the sign-in throttle.</summary>
public class AuthSignInThrottleTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AFreshKeyIsAllowedThrough()
    {
        var throttle = new SignInThrottle(new ModelFakeTimeProvider(Now));

        Assert.True(throttle.TryBegin("someone@example.com", out var retryAfter));
        Assert.Equal(TimeSpan.Zero, retryAfter);
    }

    [Fact]
    public void EnoughFailuresCloseTheWindowAndSayForHowLong()
    {
        var clock = new ModelFakeTimeProvider(Now);
        var throttle = new SignInThrottle(clock, maxFailures: 3, window: TimeSpan.FromMinutes(10));

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.True(throttle.TryBegin("key", out _));
            throttle.RecordFailure("key");
        }

        Assert.False(throttle.TryBegin("key", out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void TheWindowExpires()
    {
        var clock = new ModelFakeTimeProvider(Now);
        var throttle = new SignInThrottle(clock, maxFailures: 1, window: TimeSpan.FromMinutes(10));

        throttle.RecordFailure("key");
        Assert.False(throttle.TryBegin("key", out _));

        clock.Now = Now.AddMinutes(11);
        Assert.True(throttle.TryBegin("key", out _));
    }

    [Fact]
    public void ASuccessfulSignInClearsTheCount()
    {
        var throttle = new SignInThrottle(new ModelFakeTimeProvider(Now), maxFailures: 1);

        throttle.RecordFailure("key");
        Assert.False(throttle.TryBegin("key", out _));

        throttle.Reset("key");
        Assert.True(throttle.TryBegin("key", out _));
    }

    [Fact]
    public void OnePersonsFailuresDoNotLockOutAnother()
    {
        var throttle = new SignInThrottle(new ModelFakeTimeProvider(Now), maxFailures: 1);

        throttle.RecordFailure("first@example.com");

        Assert.False(throttle.TryBegin("first@example.com", out _));
        Assert.True(throttle.TryBegin("second@example.com", out _));
    }
}
