using System.Text.Json;
using System.Text.Json.Serialization;
using Charter.Configuration;

namespace Charter.Updates;

/// <summary>
/// What the last release check found, cached in Postgres (section 28).
/// </summary>
/// <remarks>
/// <para>
/// Section 28 requires the result to be cached in Postgres, and section 2.3 forbids holding it in
/// memory: the container restarts, and an operator should not lose the notice that their instance is
/// three security releases behind because a deploy happened. The cache is the payload of the pending
/// <see cref="Charter.Domain.JobType.UpdateCheck"/> row — the queue is already the durable, replicated,
/// prunable place scheduled work lives, so the answer rides on the question rather than needing a
/// table of its own.
/// </para>
/// <para>
/// Nothing here is sent anywhere. Every field is derived locally by comparing a public response
/// against the compiled-in build version.
/// </para>
/// </remarks>
public sealed record UpdateStatus
{
    /// <summary>Release notes longer than this are stored truncated.</summary>
    /// <remarks>
    /// A release body is attacker-influenced only in the sense that whoever publishes a release writes
    /// it, but it lands in a database column and later in a browser, so it is bounded here rather than
    /// wherever it is finally rendered.
    /// </remarks>
    public const int MaxNotesLength = 8_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The channel the check ran against (<c>CHARTER_UPDATE_CHANNEL</c>).</summary>
    public required string Channel { get; init; }

    /// <summary>The build version this instance is running.</summary>
    public required string CurrentVersion { get; init; }

    /// <summary>When a check last completed successfully, or <see langword="null"/> if never.</summary>
    public DateTimeOffset? CheckedAt { get; init; }

    /// <summary>The newest release tag seen on the channel, if any.</summary>
    public string? LatestTag { get; init; }

    /// <summary>The version parsed out of <see cref="LatestTag"/>.</summary>
    public string? LatestVersion { get; init; }

    /// <summary>True when the newest release is ahead of the running build.</summary>
    public bool UpdateAvailable { get; init; }

    /// <summary>True when that release is a security release (non-dismissible, section 28).</summary>
    public bool Security { get; init; }

    /// <summary>True when upgrading applies schema migrations, so a backup is warranted.</summary>
    public bool Migrations { get; init; }

    /// <summary>The release page.</summary>
    public string? ReleaseUrl { get; init; }

    /// <summary>The release notes, truncated to <see cref="MaxNotesLength"/>.</summary>
    public string? Notes { get; init; }

    /// <summary>When that release was published.</summary>
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>The state before any check has run, and after one that could not reach GitHub.</summary>
    public static UpdateStatus Unknown(UpdateChannel channel, string currentVersion) => new()
    {
        Channel = channel.ToWireName(),
        CurrentVersion = currentVersion,
    };

    /// <summary>The state after a check that reached GitHub and found nothing newer.</summary>
    public static UpdateStatus UpToDate(
        UpdateChannel channel,
        string currentVersion,
        DateTimeOffset checkedAt) => new()
        {
            Channel = channel.ToWireName(),
            CurrentVersion = currentVersion,
            CheckedAt = checkedAt,
        };

    /// <summary>The state after a check that found a release ahead of this build.</summary>
    public static UpdateStatus Available(
        UpdateChannel channel,
        string currentVersion,
        Release release,
        DateTimeOffset checkedAt)
    {
        ArgumentNullException.ThrowIfNull(release);

        return new UpdateStatus
        {
            Channel = channel.ToWireName(),
            CurrentVersion = currentVersion,
            CheckedAt = checkedAt,
            LatestTag = release.Tag,
            LatestVersion = release.Version.ToString(),
            UpdateAvailable = true,
            Security = release.IsSecurity,
            Migrations = release.IncludesMigrations,
            ReleaseUrl = release.Url,
            Notes = Truncate(release.Notes),
            PublishedAt = release.PublishedAt,
        };
    }

    /// <summary>
    /// Carries a previous result forward across a check that could not reach GitHub.
    /// </summary>
    /// <remarks>
    /// <see cref="CheckedAt"/> deliberately does not move: it records when Charter last <em>learned</em>
    /// something, not when it last tried. An operator reading "checked an hour ago" on an air-gapped
    /// instance would be reading a lie.
    /// </remarks>
    public UpdateStatus CarriedForward(UpdateChannel channel, string currentVersion)
        => this with { Channel = channel.ToWireName(), CurrentVersion = currentVersion };

    /// <summary>Reads a status out of a job payload, or <see langword="null"/> if it is not one.</summary>
    public static UpdateStatus? TryParse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<UpdateStatus>(payload, Json);

            // A payload that parses but carries neither required field is somebody else's shape.
            // Both properties are non-nullable to the compiler and can still arrive null from JSON.
            return parsed is not null
                   && !string.IsNullOrEmpty(parsed.Channel)
                   && !string.IsNullOrEmpty(parsed.CurrentVersion)
                ? parsed
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Serialises this status for the job payload column.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    private static string Truncate(string? notes)
    {
        if (string.IsNullOrEmpty(notes))
        {
            return string.Empty;
        }

        return notes.Length <= MaxNotesLength ? notes : notes[..MaxNotesLength];
    }
}
