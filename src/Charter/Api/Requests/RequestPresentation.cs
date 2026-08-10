using System.Globalization;
using System.Text.Json;
using Charter.Api.Contracts;
using Charter.Domain;

namespace Charter.Api.Requests;

/// <summary>
/// The plain-language vocabulary of sections 6 and 11.
/// </summary>
/// <remarks>
/// Every string a requester reads about state lives here, in one table, because section 6 pins the
/// wording ("Only two states notify", "<em>This turned out to be bigger than expected</em>") and a
/// second copy of that wording somewhere else is how a UI ends up telling two different stories
/// about the same request.
/// </remarks>
public static class RequestPresentation
{
    /// <summary>
    /// Section 11. The one sentence every failure arrives as: budget exhaustion, a stuck agent and
    /// failing checks are indistinguishable here on purpose. The real detail goes to the engineer
    /// view. "A non-engineer who sees a stack trace once never files again."
    /// </summary>
    public const string FailureSummary =
        "This turned out to be bigger than expected. An engineer has been told and will pick it up — "
        + "you do not need to do anything.";

    /// <summary>Section 27.4: do not pretend parity.</summary>
    public const string NoVerificationExplanation =
        "This kind of change cannot be checked by clicking a link — there is nothing to open. An "
        + "engineer checks it directly and you will be told when it is live.";

    /// <summary>Section 6, the requester-facing label column.</summary>
    public static string StatusLabel(RequestStatus status, string? approverName = null) => status switch
    {
        RequestStatus.Draft => "Not sent yet",
        RequestStatus.Refining => "Let's figure out what you need",
        RequestStatus.SpecReady => approverName is { Length: > 0 }
            ? $"Waiting on {approverName} to approve"
            : "Waiting for approval",
        RequestStatus.Rejected => "Sent back for another look",
        RequestStatus.Queued or RequestStatus.Running or RequestStatus.PrOpen => "Building this now",
        RequestStatus.NeedsInput => "Question for you",
        RequestStatus.PreviewReady => "Ready to try",
        RequestStatus.InReview => "An engineer is checking it",
        RequestStatus.Merged => "This is live",
        RequestStatus.Failed => "This turned out to be bigger than expected",
        RequestStatus.Cancelled => "You stopped this",
        RequestStatus.Stale => "This needs redoing against the latest code",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown request status."),
    };

    /// <summary>
    /// Section 6: only <c>NeedsInput</c> and <c>PreviewReady</c> notify. "Notifying on all of them
    /// gets Charter muted within a week." Computed here so the badge, the email and the list row can
    /// never disagree.
    /// </summary>
    public static bool NeedsAttention(RequestStatus status)
        => status is RequestStatus.NeedsInput or RequestStatus.PreviewReady;

    /// <summary>Section 11, the four promoted milestones.</summary>
    public static (ApiMilestoneKind Kind, string Label) Milestone(MilestoneLabel label) => label switch
    {
        MilestoneLabel.UnderstandingSetup => (ApiMilestoneKind.Understanding, "Understanding the current setup"),
        MilestoneLabel.MakingChanges => (ApiMilestoneKind.Changing, "Making the changes"),
        MilestoneLabel.CheckingItWorks => (ApiMilestoneKind.Checking, "Checking it works"),
        MilestoneLabel.PuttingItTogether => (ApiMilestoneKind.Assembling, "Putting it together"),
        _ => throw new ArgumentOutOfRangeException(nameof(label), label, "Unknown milestone label."),
    };

    /// <summary>The tab label for one artifact kind (section 27.7).</summary>
    public static string ArtifactLabel(VerificationArtifactKind kind) => kind switch
    {
        VerificationArtifactKind.HostedPreview => "Preview",
        VerificationArtifactKind.BuildArtifact => "Download",
        VerificationArtifactKind.DistributionChannel => "Install",
        VerificationArtifactKind.Capture => "Screenshots",
        VerificationArtifactKind.EphemeralInstance => "Sandbox",
        VerificationArtifactKind.TestReport => "Checks",
        VerificationArtifactKind.HilReport => "Bench run",
        VerificationArtifactKind.None => "How this gets checked",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown artifact kind."),
    };

    /// <summary>The first line of the raw request, for a request that has no spec title yet.</summary>
    public static string TitleFrom(string rawText)
    {
        ArgumentNullException.ThrowIfNull(rawText);

        var line = rawText.AsSpan();
        var end = line.IndexOfAny('\r', '\n');
        if (end >= 0)
        {
            line = line[..end];
        }

        var trimmed = line.Trim().ToString();

        if (trimmed.Length == 0)
        {
            return "Untitled request";
        }

        return trimmed.Length <= 80 ? trimmed : string.Concat(trimmed.AsSpan(0, 79).TrimEnd(), "…");
    }

    /// <summary>
    /// A one-line, requester-safe rendering of a transcript event.
    /// </summary>
    /// <remarks>
    /// Pane 2 is engineer-only (section 7.4), so this is allowed to name files and commands. It still
    /// summarises rather than dumping the payload, because the payload carries repository content and
    /// section 19 is explicit that it is not to be treated as loggable prose.
    /// </remarks>
    public static string TranscriptSummary(string type, string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(type);

        var payload = ReadPayload(payloadJson);

        return type switch
        {
            EventTypes.Message => Text(payload, "text") ?? "The agent said something.",
            EventTypes.ToolUse => $"Used {Text(payload, "name") ?? "a tool"}.",
            EventTypes.FileWrite => $"Wrote {Text(payload, "path") ?? "a file"}.",
            EventTypes.Command => $"Ran {Text(payload, "command") ?? "a command"}.",
            EventTypes.CheckResult => Text(payload, "summary") ?? "A check finished.",
            EventTypes.NetworkCall => $"Called {Text(payload, "host") ?? "an external service"}.",
            EventTypes.Error => Text(payload, "message") ?? "Something went wrong.",
            EventTypes.Cost => "Recorded token cost.",
            EventTypes.SessionStarted => "The session started.",
            EventTypes.SessionEnded => "The session ended.",
            _ => type,
        };
    }

    /// <summary>The path a file-write event touched, if it named one.</summary>
    public static string? TranscriptPath(string type, string payloadJson)
        => string.Equals(type, EventTypes.FileWrite, StringComparison.Ordinal)
            ? Text(ReadPayload(payloadJson), "path")
            : null;

    /// <summary>
    /// Section 14: auth, migrations, money math and external calls float to the top of pane 3.
    /// </summary>
    public static ApiChangeRisk ClassifyPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var lowered = path.ToLowerInvariant();

        if (Mentions(lowered, "auth", "login", "password", "token", "secret", "credential", "permission")
            || Mentions(lowered, "migration", "migrations", "schema")
            || Mentions(lowered, "payment", "billing", "invoice", "price", "pricing", "tax", "ledger"))
        {
            return ApiChangeRisk.High;
        }

        if (Mentions(lowered, "http", "client", "webhook", "api", "integration", "config"))
        {
            return ApiChangeRisk.Medium;
        }

        return ApiChangeRisk.Low;
    }

    /// <summary>
    /// A hostname-and-a-bit rendering of a preview URL, for the chip (section 27.7).
    /// </summary>
    /// <remarks>
    /// The scheme goes first: it costs eight characters of a chip that has about forty, and nobody
    /// reading "is this the right preview" is reading it for the protocol. The full URL is what gets
    /// opened and copied.
    /// </remarks>
    public static string TruncateUrl(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        var shown = scheme >= 0 ? url[(scheme + 3)..] : url;

        if (shown.Length <= 40)
        {
            return shown;
        }

        return string.Concat(shown.AsSpan(0, 26), "…", shown.AsSpan(shown.Length - 13));
    }

    private static bool Mentions(string haystack, params ReadOnlySpan<string> needles)
    {
        foreach (var needle in needles)
        {
            if (haystack.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static JsonElement? ReadPayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement? payload, string name)
    {
        if (payload is not { } element
            || !element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        return text.Length <= 160
            ? text
            : string.Concat(text.AsSpan(0, 159).TrimEnd(), "…");
    }

    /// <summary>Elapsed milliseconds between two instants. Elapsed only — never remaining (section 6).</summary>
    public static long ElapsedMs(DateTimeOffset? from, DateTimeOffset? to)
    {
        if (from is not { } start || to is not { } end || end < start)
        {
            return 0;
        }

        return (long)(end - start).TotalMilliseconds;
    }

    /// <summary>A stable, human-readable id for a criterion that the domain stores as bare text.</summary>
    public static string CriterionId(Guid specId, int index)
        => string.Create(CultureInfo.InvariantCulture, $"{specId:N}-ac-{index + 1}");
}
