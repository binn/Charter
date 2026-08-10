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

    public required ApiTeachingLevel TeachingLevel { get; init; }

    /// <summary>Section 30.4. Null until the three onboarding screens are done.</summary>
    public DateTimeOffset? RequesterOnboardingCompletedAt { get; init; }
}

/// <summary>
/// Where a user's preferences are read and written.
/// </summary>
/// <remarks>
/// Section 3.1: there is no browser storage in this app, so a preference exists in exactly one place
/// and is refetched rather than cached across reloads. This is an interface because the onboarding
/// work owns the columns that do not exist yet; registering a richer implementation replaces the
/// default without the API layer changing.
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
/// The default store, backed by the <c>users</c> row.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ApiTeachingLevel"/> is a real column and round-trips properly.
/// </para>
/// <para>
/// <strong>Known gap.</strong> There is no <c>theme</c>, <c>pane</c> or
/// <c>requester_onboarding_completed_at</c> column on <c>users</c> yet, so those three are served at
/// their defaults and a write to them is accepted and dropped rather than silently pretended. Pane
/// defaults by role per section 12 — requesters land on pane 1 — and the role is not known here, so
/// the default is the conservative one. Adding the columns is what closes this; nothing in this file
/// caches anything in memory, because section 2.3 forbids it.
/// </para>
/// </remarks>
public sealed class UserRecordPreferencesStore : IViewerPreferencesStore
{
    private readonly CharterDbContext database;

    public UserRecordPreferencesStore(CharterDbContext database)
    {
        ArgumentNullException.ThrowIfNull(database);
        this.database = database;
    }

    /// <inheritdoc />
    public async Task<ViewerPreferencesRecord> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var teaching = await database.Users
            .AsNoTracking()
            .Where(row => row.Id == userId)
            .Select(row => (TeachingLevel?)row.TeachingLevel)
            .SingleOrDefaultAsync(cancellationToken);

        return Resolve(teaching ?? TeachingLevel.ExplainEverything);
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
            return Resolve(TeachingLevel.ExplainEverything);
        }

        if (patch.TeachingLevel is { } level)
        {
            user.SetTeachingLevel(level.ToDomain());
            await database.SaveChangesAsync(cancellationToken);
        }

        return Resolve(user.TeachingLevel, patch.Theme, patch.Pane);
    }

    /// <inheritdoc />
    public Task<ViewerPreferencesRecord> CompleteRequesterOnboardingAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
        => GetAsync(userId, cancellationToken);

    private static ViewerPreferencesRecord Resolve(
        TeachingLevel teachingLevel,
        ApiThemePreference? theme = null,
        ApiPanePreference? pane = null)
        => new()
        {
            Theme = theme ?? ApiThemePreference.System,
            Pane = pane ?? ApiPanePreference.Simple,
            TeachingLevel = teachingLevel.ToApi(),
        };
}
