using Charter.Data;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;

namespace Charter.Budgets;

/// <summary>
/// Who to ask, named, for a message about a limit.
/// </summary>
/// <param name="Sentence">
/// A complete sentence, ready to append to a limit message. Never empty.
/// </param>
/// <param name="Named">Whether it names actual people rather than falling back to a role.</param>
public readonly record struct BudgetAuthorityDescription(string Sentence, bool Named);

/// <summary>
/// Resolves who can raise a budget.
/// </summary>
/// <remarks>
/// Section 34.5: <strong>every limit message names who can raise it.</strong> A dead end that does
/// not say who to ask is the fastest way to make people stop using the tool — the requester has no
/// way to tell "wait five minutes" from "this will never work" and stops filing requests. Budgets
/// are an admin's to edit (section 7.1), so this is the admin list.
/// </remarks>
public interface IBudgetAuthority
{
    /// <summary>Names the people who can raise budgets in this organisation.</summary>
    Task<BudgetAuthorityDescription> DescribeAsync(Guid orgId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class BudgetAuthority : IBudgetAuthority
{
    /// <summary>
    /// How many names a message carries before it stops listing them.
    /// </summary>
    /// <remarks>
    /// Three. A message that lists nineteen admins names nobody in particular, and the reader has
    /// to pick one anyway.
    /// </remarks>
    public const int MaxNames = 3;

    /// <summary>The fallback when no admin can be named. Still says who, just not which.</summary>
    public const string RoleFallback = "Ask an administrator of this Charter instance to raise it.";

    private readonly CharterDbContext _db;

    /// <summary>Creates the resolver.</summary>
    public BudgetAuthority(CharterDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc />
    public async Task<BudgetAuthorityDescription> DescribeAsync(
        Guid orgId,
        CancellationToken cancellationToken = default)
    {
        // Loaded and filtered in memory: an instance serves exactly one organisation (section 7.2a),
        // so this is the whole member list and it is small.
        var admins = await (
            from member in _db.Members.AsNoTracking()
            where member.OrgId == orgId
            join user in _db.Users.AsNoTracking() on member.UserId equals user.Id
            select new { member.Roles, user.DisplayName, user.Email })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var names = admins
            .Where(candidate => candidate.Roles.Contains(MemberRole.Admin))
            .Select(candidate => Describe(candidate.DisplayName, candidate.Email))
            .Where(static name => name.Length > 0)
            .Order(StringComparer.Ordinal)
            .Take(MaxNames)
            .ToList();

        return Compose(names);
    }

    /// <summary>Builds the sentence from an already-resolved set of names.</summary>
    public static BudgetAuthorityDescription Compose(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        if (names.Count == 0)
        {
            return new BudgetAuthorityDescription(RoleFallback, Named: false);
        }

        var joined = names.Count == 1
            ? names[0]
            : string.Join(", ", names.Take(names.Count - 1)) + " or " + names[^1];

        return new BudgetAuthorityDescription($"Ask {joined} to raise it.", Named: true);
    }

    private static string Describe(string? displayName, string? email)
    {
        var name = displayName?.Trim();
        var address = email?.Trim();

        return (name, address) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{name} ({address})",
            ({ Length: > 0 }, _) => name!,
            (_, { Length: > 0 }) => address!,
            _ => string.Empty,
        };
    }
}
