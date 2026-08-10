namespace Charter.Domain;

/// <summary>
/// The outbound channels of section 22, as the database spells them.
/// </summary>
/// <remarks>
/// The domain's own copy of <c>Charter.Notifications.NotificationChannel</c>, for the same reason
/// <see cref="CredentialKind"/> is not the model layer's enum: the column is a persistence contract
/// and must not move because a namespace above it was reorganised. All three are named even though
/// only email has an implementation, so neither Slack nor Discord needs a migration when it arrives
/// (section 22).
/// </remarks>
public enum NotificationChannelKind
{
    /// <summary>Email, the only channel with an implementation.</summary>
    Email,

    /// <summary>Slack. Declared, not implemented.</summary>
    Slack,

    /// <summary>Discord. Declared, not implemented.</summary>
    Discord,
}

/// <summary>
/// One person's answer for one channel (section 22).
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no per-state column, and there must not be one.</strong> Section 22 fires on
/// exactly two states, and somebody who wants neither wants no notifications — which is an empty
/// channel set, not a matrix of checkboxes.
/// </para>
/// <para>
/// An absent row means the default: email on, everything else off. That is what lets the default
/// ship without a backfill, and what keeps a fresh instance notifying — both notifying states are
/// ones where Charter is blocked on that person, so a default of silence leaves a request sitting in
/// <c>NeedsInput</c> until somebody happens to open the app.
/// </para>
/// </remarks>
public sealed class NotificationChannelPreference
{
    private NotificationChannelPreference()
    {
    }

    private NotificationChannelPreference(
        Guid userId,
        NotificationChannelKind channel,
        bool enabled,
        DateTimeOffset updatedAt)
    {
        UserId = userId;
        Channel = channel;
        Enabled = enabled;
        UpdatedAt = updatedAt;
    }

    /// <summary>Half of the primary key. There is no surrogate id: the pair is the identity.</summary>
    public Guid UserId { get; private set; }

    /// <summary>The other half.</summary>
    public NotificationChannelKind Channel { get; private set; }

    public bool Enabled { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Whether this channel is on when nobody has chosen (section 22).</summary>
    public static bool DefaultFor(NotificationChannelKind channel) => channel == NotificationChannelKind.Email;

    public static NotificationChannelPreference Set(
        Guid userId,
        NotificationChannelKind channel,
        bool enabled,
        DateTimeOffset? now = null)
        => new(userId, channel, enabled, DomainTime.Resolve(now));

    public void Update(bool enabled, DateTimeOffset? now = null)
    {
        Enabled = enabled;
        UpdatedAt = DomainTime.Resolve(now);
    }
}
