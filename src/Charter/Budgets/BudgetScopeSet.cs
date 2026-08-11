using Charter.Domain;

namespace Charter.Budgets;

/// <summary>
/// Everything a piece of work belongs to, so section 34.3 can find every budget that governs it.
/// </summary>
/// <remarks>
/// <para>
/// Nesting is conjunctive: <strong>a session must have headroom in <em>every</em> applicable
/// budget</strong>, not the most specific one. A user with $200 remaining inside a team whose pool
/// is exhausted cannot spend. So this is a set of memberships rather than a single winner, and
/// nothing here resolves a precedence order for spending — only for
/// <see cref="Budget.ReservedAmount"/>, where a floor has to know what it is a floor beneath.
/// </para>
/// <para>
/// Teams, projects and tags are strings because section 34.2 spells <c>scope_id</c> as one column
/// across all seven scope types, and a role scope holds a role name rather than a row id.
/// </para>
/// </remarks>
public sealed record BudgetScopeSet
{
    /// <summary>The organisation. There is only ever one (section 7.2a).</summary>
    public required Guid OrgId { get; init; }

    /// <summary>Whose spend this is.</summary>
    public required Guid UserId { get; init; }

    /// <summary>The repository, where the work has one.</summary>
    public Guid? RepoId { get; init; }

    /// <summary>The roles the spender holds. A role-scoped budget matches any of them.</summary>
    public IReadOnlyList<MemberRole> Roles { get; init; } = [];

    /// <summary>Team identifiers the spender belongs to.</summary>
    public IReadOnlyList<string> Teams { get; init; } = [];

    /// <summary>Project identifiers the work belongs to.</summary>
    public IReadOnlyList<string> Projects { get; init; } = [];

    /// <summary>Free-form tags, for campaign and cost-centre budgets.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// Whether <paramref name="budget"/> governs this work at <paramref name="instant"/>.
    /// </summary>
    /// <remarks>
    /// Four independent conditions, all of which must hold: the organisation, the scope, the
    /// category (section 34.6), and the window for a campaign budget (section 34.7). A budget that
    /// fails any of them is not a budget with no headroom — it simply does not apply, and saying so
    /// separately is what keeps <em>"chat is uncapped here"</em> from reading as <em>"chat has
    /// spent its cap"</em>.
    /// </remarks>
    public bool IsGovernedBy(Budget budget, LedgerCategory category, DateTimeOffset instant)
    {
        ArgumentNullException.ThrowIfNull(budget);

        return budget.OrgId == OrgId
            && budget.Covers(category)
            && budget.IsActiveAt(instant)
            && MatchesScope(budget);
    }

    /// <summary>
    /// How narrow a scope type is, from the organisation outwards to one person.
    /// </summary>
    /// <remarks>
    /// Used only by <see cref="Budget.ReservedAmount"/>, which is a floor beneath the shared pool:
    /// spend inside a person's guaranteed reserve must not be charged to the budgets above it, and
    /// "above" needs an order. It is deliberately not used to pick a winner — section 34.3 rules
    /// most-specific-wins out in the first sentence.
    /// </remarks>
    public static int Specificity(BudgetScopeType scopeType) => scopeType switch
    {
        BudgetScopeType.Org => 0,
        BudgetScopeType.Team => 1,
        BudgetScopeType.Project => 2,
        BudgetScopeType.Repo => 3,
        BudgetScopeType.Role => 4,
        BudgetScopeType.Tag => 5,
        BudgetScopeType.User => 6,
        _ => 0,
    };

    private bool MatchesScope(Budget budget) => budget.ScopeType switch
    {
        // An org-scoped budget governs everything in the organisation; scope_id is redundant and is
        // honoured anyway when somebody set one, rather than silently ignored.
        BudgetScopeType.Org => budget.ScopeId is null || Equals(budget.ScopeId, OrgId),
        BudgetScopeType.User => Equals(budget.ScopeId, UserId),
        BudgetScopeType.Repo => RepoId is { } repoId && Equals(budget.ScopeId, repoId),
        BudgetScopeType.Team => Contains(Teams, budget.ScopeId),
        BudgetScopeType.Project => Contains(Projects, budget.ScopeId),
        BudgetScopeType.Tag => Contains(Tags, budget.ScopeId),
        BudgetScopeType.Role => budget.ScopeId is { } role
            && Roles.Any(held => string.Equals(held.ToString(), role, StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };

    private static bool Equals(string? scopeId, Guid id)
        => Guid.TryParse(scopeId, out var parsed) && parsed == id;

    private static bool Contains(IReadOnlyList<string> values, string? scopeId)
        => scopeId is { Length: > 0 } && values.Contains(scopeId, StringComparer.OrdinalIgnoreCase);
}
