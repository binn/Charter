using Charter.Domain;
using Charter.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Charter.Data.Notifications;

/// <summary>
/// Section 22's per-user channel preference, backed by <c>notification_channels</c>.
/// </summary>
/// <remarks>
/// <para>
/// The table is <c>(user_id, channel, enabled)</c> keyed on the pair, and that is the whole shape.
/// <strong>There is no per-state column</strong> and section 22 says there should not grow one:
/// exactly two states notify, and somebody who wants neither wants no notifications — an empty
/// channel set, not a matrix.
/// </para>
/// <para>
/// <strong>An absent row is the default</strong>: email on, Slack and Discord off. That is what lets
/// this ship without a backfill — every user who existed before the table did reads back exactly
/// what <c>DefaultNotificationPreferenceStore</c> gave them — and it is why the default must be
/// "on": both notifying states are ones where Charter is blocked on that person, so a default of
/// silence leaves a request sitting in <c>NeedsInput</c> until somebody happens to open the app.
/// </para>
/// </remarks>
public sealed class EfNotificationPreferenceStore : INotificationPreferenceStore
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;

    /// <summary>Creates the store.</summary>
    public EfNotificationPreferenceStore(IServiceScopeFactory scopes, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(clock);

        _scopes = scopes;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<NotificationPreference> GetAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharterDbContext>();

        var stored = await db.NotificationChannels
            .AsNoTracking()
            .Where(preference => preference.UserId == userId)
            .ToDictionaryAsync(
                preference => preference.Channel,
                preference => preference.Enabled,
                cancellationToken)
            .ConfigureAwait(false);

        var channels = new HashSet<NotificationChannel>();

        foreach (var channel in Enum.GetValues<NotificationChannel>())
        {
            var kind = ToDomain(channel);
            var enabled = stored.TryGetValue(kind, out var chosen)
                ? chosen
                : NotificationChannelPreference.DefaultFor(kind);

            if (enabled)
            {
                _ = channels.Add(channel);
            }
        }

        return new NotificationPreference { UserId = userId, Channels = channels };
    }

    /// <summary>
    /// Records one person's answer for one channel (the settings toggle of section 30.2).
    /// </summary>
    /// <remarks>
    /// A row is written even when the value matches the default. "Nobody has chosen" and "somebody
    /// chose the default" answer the same way today, but only the second survives a future change to
    /// what the default is, which is exactly the kind of change a person would experience as Charter
    /// overriding a decision they made.
    /// </remarks>
    public async Task SetAsync(
        Guid userId,
        NotificationChannel channel,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var kind = ToDomain(channel);

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharterDbContext>();

        var existing = await db.NotificationChannels
            .FirstOrDefaultAsync(
                preference => preference.UserId == userId && preference.Channel == kind,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            db.NotificationChannels.Add(
                NotificationChannelPreference.Set(userId, kind, enabled, _clock.GetUtcNow()));
        }
        else
        {
            existing.Update(enabled, _clock.GetUtcNow());
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // CS8524 is the unnamed-value arm, i.e. an arbitrary integer cast to the enum. The switch covers
    // every named value and has no default on purpose: adding a channel to either enum must be a
    // compile error here rather than a preference that silently reads back as off.
#pragma warning disable CS8524

    /// <summary>Maps section 22's channel onto the persisted one.</summary>
    internal static NotificationChannelKind ToDomain(NotificationChannel channel) => channel switch
    {
        NotificationChannel.Email => NotificationChannelKind.Email,
        NotificationChannel.Slack => NotificationChannelKind.Slack,
        NotificationChannel.Discord => NotificationChannelKind.Discord,
    };

    /// <summary>The inverse, for anything reading rows back out.</summary>
    internal static NotificationChannel ToChannel(NotificationChannelKind kind) => kind switch
    {
        NotificationChannelKind.Email => NotificationChannel.Email,
        NotificationChannelKind.Slack => NotificationChannel.Slack,
        NotificationChannelKind.Discord => NotificationChannel.Discord,
    };

#pragma warning restore CS8524
}
