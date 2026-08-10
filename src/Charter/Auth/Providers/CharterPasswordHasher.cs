using Charter.Configuration;
using Charter.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Charter.Auth.Providers;

/// <summary>What verifying a stored hash against a supplied password concluded.</summary>
public enum PasswordVerification
{
    /// <summary>Wrong password, or a stored hash in a format this build cannot read.</summary>
    Failed,

    Success,

    /// <summary>Correct, but hashed under weaker parameters than the current ones.</summary>
    SuccessRehashNeeded,
}

/// <summary>Hashing and verification, as a seam so nothing else ever touches the algorithm.</summary>
public interface ICharterPasswordHasher
{
    /// <summary>Returns the string to store. The password itself is never returned or retained.</summary>
    string Hash(Secret password);

    /// <summary>Verifies <paramref name="password"/> against a stored hash in constant time.</summary>
    PasswordVerification Verify(string storedHash, Secret password);

    /// <summary>
    /// Performs the same work as a real verification against a throwaway hash.
    /// </summary>
    /// <remarks>
    /// Called when no user matched, so that "this email does not exist" and "this password is wrong"
    /// take the same time. Without it, sign-in latency is a user-enumeration oracle.
    /// </remarks>
    void SpendEquivalentWork();
}

/// <summary>
/// ASP.NET Core's <see cref="PasswordHasher{TUser}"/>, configured and wrapped.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is hand-rolled: the format, the salt, the constant-time comparison and the
/// rehash-needed signal are all the framework's. What this type adds is the parameters, the
/// <see cref="Secret"/>-typed surface, and the equal-work path for a missing user.
/// </para>
/// <para>
/// Version 3 of the format is PBKDF2 with HMAC-SHA256, a 128-bit salt and a 256-bit subkey.
/// <see cref="DefaultIterationCount"/> follows OWASP's current guidance for that construction. The
/// count is a constructor parameter so a test can drop it; nothing in production should.
/// </para>
/// </remarks>
public sealed class CharterPasswordHasher : ICharterPasswordHasher
{
    /// <summary>OWASP's PBKDF2-HMAC-SHA256 recommendation.</summary>
    public const int DefaultIterationCount = 600_000;

    /// <summary>
    /// Section 21 stores no password, so the only length rule is the one that matters: long enough.
    /// NIST 800-63B asks for a minimum length and no composition rules, because complexity rules
    /// produce <c>Password1!</c> and nothing else.
    /// </summary>
    public const int MinimumPasswordLength = 12;

    /// <summary>Bounded so a submitted megabyte cannot turn PBKDF2 into a denial of service.</summary>
    public const int MaximumPasswordLength = 256;

    private static readonly Secret WorkEqualisationPassword = new("charter-timing-equalisation");

    private readonly PasswordHasher<User> hasher;
    private readonly string throwawayHash;

    public CharterPasswordHasher(int iterationCount = DefaultIterationCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iterationCount, 1);

        hasher = new PasswordHasher<User>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = iterationCount,
        }));

        throwawayHash = hasher.HashPassword(Placeholder, WorkEqualisationPassword.Reveal());
    }

    /// <summary>True when the value clears the length rules and can be hashed.</summary>
    public static bool IsAcceptable(Secret? password)
        => password is not null
           && password.Length >= MinimumPasswordLength
           && password.Length <= MaximumPasswordLength;

    /// <inheritdoc />
    public string Hash(Secret password)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (!IsAcceptable(password))
        {
            throw new ArgumentException(
                $"A password must be between {MinimumPasswordLength} and {MaximumPasswordLength} characters.",
                nameof(password));
        }

        return hasher.HashPassword(Placeholder, password.Reveal());
    }

    /// <inheritdoc />
    public PasswordVerification Verify(string storedHash, Secret password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storedHash);
        ArgumentNullException.ThrowIfNull(password);

        // FormatException is what the framework throws for a hash string it cannot parse - a row
        // written by a future format, or a corrupted one. Treated as a failed verification rather
        // than an exception, because the caller's answer is the same either way.
        try
        {
            return hasher.VerifyHashedPassword(Placeholder, storedHash, password.Reveal()) switch
            {
                PasswordVerificationResult.Success => PasswordVerification.Success,
                PasswordVerificationResult.SuccessRehashNeeded => PasswordVerification.SuccessRehashNeeded,
                _ => PasswordVerification.Failed,
            };
        }
        catch (FormatException)
        {
            return PasswordVerification.Failed;
        }
    }

    /// <inheritdoc />
    public void SpendEquivalentWork()
        => _ = hasher.VerifyHashedPassword(Placeholder, throwawayHash, WorkEqualisationPassword.Reveal());

    /// <summary>
    /// <see cref="PasswordHasher{TUser}"/> is generic over a user type it never reads, so one shared
    /// instance stands in for every caller rather than the caller's real <see cref="User"/> row.
    /// Passing the real row would imply the hash is bound to it, and it is not.
    /// </summary>
    private static User Placeholder { get; } = User.Create("hasher@charter.invalid", "hasher");
}
