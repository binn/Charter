using Charter.Api.Contracts;
using Charter.Data;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;

namespace Charter.Api.Viewer;

/// <summary>The resolved preference set for one user (section 12, section 13).</summary>
public sealed record ViewerPreferencesRecord
{
    public required ApiThemePreference Theme { get; init; }

    public required ApiPanePreference Pane { get; init; }

    /// <summary>
    /// Whether <see cref="Pane"/> is a choice somebody made rather than the default standing in for
    /// one. Section 12 defaults the pane by role, and that is only applicable while it is false.
    /// </summary>
    public bool PaneIsExplicit { get; init; }

    public required ApiTeachingLevel TeachingLevel { get; init; }

    /// <summary>Section 30.4. Null until the three onboarding screens are done.</summary>
    public DateTimeOffset? RequesterOnboardingCompletedAt { get; init; }
}

/// <summary>
/// Where a user's preferences are read and written.
/// </summary>
/// <remarks>
/// Section 3.1: there is no browser storage in this app, so a preference exists in exactly one place
/// and is refetched rather than cached across reloads. This is an interface so the onboarding work
/// can register a richer implementation without the API layer changing; the default below writes
/// every field the contract carries.
/// </remarks>
public interface IViewerPreferencesStore
{
    /// <summary>The current set, with defaults filled in for anything unset.</summary>
    Task<ViewerPreferencesRecord> GetAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Applies a partial and returns the full resolved set.</summary>
    Task<ViewerPreferencesRecord> UpdateAsync(
        Guid userId,
        UpdatePreferencesBody patch,
        CancellationToken cancellationToken = default);

    /// <summary>Section 30.4. Idempotent: completing twice keeps the first timestamp.</summary>
    Task<ViewerPreferencesRecord> CompleteRequesterOnboardingAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The store, backed by the <c>users</c> row.
/// </summary>
/// <remarks>
/// <para>
/// All four values are columns, so a <c>PATCH</c> lands in Postgres and survives the container
/// restarting under it (section 2.3). Nothing here caches anything in memory, for the same reason.
/// </para>
/// <para>
/// <c>users.pane</c> is nullable on purpose. Section 12 defaults the pane by role — requesters land
/// on pane 1, engineers on pane 3 — and that default can only be applied while nobody has chosen.
/// Writing <c>simple</c> at account creation would be indistinguishable from an engineer who picked
/// pane 1 deliberately. The role is not known at this layer, so an unchosen pane resolves to the
/// conservative default and <see cref="ViewerPreferencesRecord.PaneIsExplicit"/> tells a caller that
/// does know the role that it is still free to override it.
/// </para>
/// </remarks>
public sealed class UserRecordPreferencesStore : IViewerPreferencesStore
{
    private readonly CharterDbContext database;
    private readonly TimeProvider clock;

    public UserRecordPreferencesStore(CharterDbContext database)
        : this(database, TimeProvider.System)
    {
    }

    public UserRecordPreferencesStore(CharterDbContext database, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(clock);

        this.database = database;
        this.clock = clock;
    }

    /// <inheritdoc />
    public async Task<ViewerPreferencesRecord> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await database.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == userId, cancellationToken);

        return user is null ? Defaults : Resolve(user);
    }

    /// <inheritdoc />
    public async Task<ViewerPreferencesRecord> UpdateAsync(
        Guid userId,
        UpdatePreferencesBody patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);

        var user = await database.Users.SingleOrDefaultAsync(row => row.Id == userId, cancellationToken);

        if (user is null)
        {
            return Defaults;
        }

        // A partial: an absent member is "leave it alone", never "reset it".
        if (patch.TeachingLevel is { } level)
        {
            user.SetTeachingLevel(level.ToDomain());
        }

        if (patch.Theme is { } theme)
        {
            user.SetTheme(theme.ToDomain());
        }

        if (patch.Pane is { } pane)
        {
            user.SetPane(pane.ToDomain());
        }

        await database.SaveChangesAsync(cancellationToken);

        return Resolve(user);
    }

    /// <inheritdoc />
    public async Task<ViewerPreferencesRecord> CompleteRequesterOnboardingAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var user = await database.Users.SingleOrDefaultAsync(row => row.Id == userId, cancellationToken);

        if (user is null)
        {
            return Defaults;
        }

        // Idempotent in the aggregate: completing twice keeps the first timestamp.
        user.CompleteRequesterOnboarding(clock.GetUtcNow());
        await database.SaveChangesAsync(cancellationToken);

        return Resolve(user);
    }

    private static ViewerPreferencesRecord Defaults => new()
    {
        Theme = ApiThemePreference.System,
        Pane = ApiPanePreference.Simple,
        PaneIsExplicit = false,
        TeachingLevel = ApiTeachingLevel.ExplainEverything,
    };

    private static ViewerPreferencesRecord Resolve(User user) => new()
    {
        Theme = user.Theme.ToApi(),
        Pane = user.Pane?.ToApi() ?? ApiPanePreference.Simple,
        PaneIsExplicit = user.Pane is not null,
        TeachingLevel = user.TeachingLevel.ToApi(),
        RequesterOnboardingCompletedAt = user.RequesterOnboardingCompletedAt,
    };
}
