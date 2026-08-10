namespace Charter.Notifications;

/// <summary>
/// The outbound channels of section 22.
/// </summary>
/// <remarks>
/// All three are named because the preference is per user and per channel, and a preference model
/// that only knows about email has to be migrated the day Slack arrives. Only
/// <see cref="Email"/> has an implementation in this build - one seam, one implementation.
/// </remarks>
public enum NotificationChannel
{
    /// <summary>Email, the only channel that needs no third-party app.</summary>
    Email,

    /// <summary>Slack. Declared, not implemented.</summary>
    Slack,

    /// <summary>Discord. Declared, not implemented.</summary>
    Discord,
}

/// <summary>Which channels one person wants the two notify-worthy states on.</summary>
/// <remarks>
/// There is no per-state preference, and there should not be: there are only two states, and
/// somebody who wants neither wants no notifications, which is an empty channel set.
/// </remarks>
public sealed record NotificationPreference
{
    /// <summary>Who this belongs to.</summary>
    public required Guid UserId { get; init; }

    /// <summary>The channels they want. Empty means they have opted out entirely.</summary>
    public required IReadOnlySet<NotificationChannel> Channels { get; init; }

    /// <summary>True when at least one channel is on.</summary>
    public bool WantsAnything => Channels.Count > 0;

    /// <summary>
    /// Email on, everything else off.
    /// </summary>
    /// <remarks>
    /// The default has to be on, because the two states that notify are the two where Charter is
    /// blocked on the person. A default of silence means a request sits in <c>NeedsInput</c> until
    /// somebody happens to open the app.
    /// </remarks>
    public static NotificationPreference Default(Guid userId) => new()
    {
        UserId = userId,
        Channels = new HashSet<NotificationChannel> { NotificationChannel.Email },
    };
}

/// <summary>Where a person's channel preference is read from.</summary>
/// <remarks>
/// An interface with an in-code default, because the persistent version is a column on a table this
/// change does not own. The dispatcher depends on this rather than on a table, so the storage-backed
/// implementation replaces one registration and no call sites.
/// </remarks>
public interface INotificationPreferenceStore
{
    /// <summary>The preference for <paramref name="userId"/>, defaulted when nothing is stored.</summary>
    Task<NotificationPreference> GetAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The default store: everyone gets email, nobody has opted out.
/// </summary>
/// <remarks>
/// This is what runs until the preference column exists. It is deliberately not a stub that throws -
/// a notification path that fails because nobody has stored a preference yet would take out the
/// notify-worthy states on a fresh instance, which is every instance for its first week.
/// </remarks>
public sealed class DefaultNotificationPreferenceStore : INotificationPreferenceStore
{
    /// <inheritdoc />
    public Task<NotificationPreference> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(NotificationPreference.Default(userId));
    }
}
