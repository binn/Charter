using System.Runtime.Serialization;

namespace Charter.Domain;

/// <summary>
/// What lets a human judge whether the change is right (section 27.1). "Preview environment"
/// generalised: clicking a URL only works for web apps, and Charter has to serve firmware, Unity,
/// desktop and mobile too.
/// </summary>
public enum VerificationArtifactKind
{
    /// <summary>Ephemeral deployed URL. Web, API, Blazor Server.</summary>
    HostedPreview,

    /// <summary>Downloadable binary — APK, IPA, .exe, .app, .elf, .uf2.</summary>
    BuildArtifact,

    /// <summary>TestFlight, Play Internal Testing, Firebase App Distribution, Expo EAS Update.</summary>
    DistributionChannel,

    /// <summary>Screenshots or video from a simulator, emulator or play-mode run.</summary>
    Capture,

    /// <summary>Running server plus a connect string. Game servers, MQTT brokers, gRPC.</summary>
    EphemeralInstance,

    /// <summary>Structured pass/fail plus logs and captured signals.</summary>
    TestReport,

    /// <summary>Hardware-in-the-loop run against a real device.</summary>
    [EnumMember(Value = "hil_report")]
    HilReport,

    /// <summary>Engineer review only. Section 27.4: do not pretend parity.</summary>
    None,
}

/// <summary>The card states of section 27.7.</summary>
public enum VerificationArtifactState
{
    Pending,

    Ready,

    /// <summary>
    /// Under an hour of life left. Never persisted — <see cref="VerificationArtifact.DisplayStateAt"/>
    /// derives it from <see cref="VerificationArtifact.ExpiresAt"/>, because a stored copy would
    /// disagree with the clock within the hour.
    /// </summary>
    Expiring,

    Expired,

    Failed,
}

/// <summary>Who the artifact is for. Authorisation is not a rendering concern (section 27.7).</summary>
public enum VerificationArtifactAudience
{
    Requester,

    EngineerOnly,
}

/// <summary>
/// The payoff for the entire pipeline (section 27.1). A session may produce several: Expo yields an
/// EAS Update channel <em>and</em> a simulator capture.
/// </summary>
public sealed class VerificationArtifact
{
    /// <summary>Section 27.7: under an hour remaining turns the countdown amber.</summary>
    public static readonly TimeSpan ExpiringWindow = TimeSpan.FromHours(1);

    private VerificationArtifact()
    {
    }

    private VerificationArtifact(
        Guid id,
        Guid sessionId,
        VerificationArtifactKind kind,
        VerificationArtifactState state,
        VerificationArtifactAudience audience,
        DateTimeOffset createdAt)
    {
        Id = id;
        SessionId = sessionId;
        Kind = kind;
        State = state;
        Audience = audience;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid SessionId { get; private set; }

    public VerificationArtifactKind Kind { get; private set; }

    public VerificationArtifactState State { get; private set; }

    public string? Url { get; private set; }

    /// <summary>
    /// Object storage key, never a container path. An IPA is not a database row and there is no
    /// durable local disk (sections 2.3, 27.5).
    /// </summary>
    public string? FileRef { get; private set; }

    public string? ConnectString { get; private set; }

    /// <summary>
    /// The kind-specific body of section 27.7's card, as jsonb.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A URL, a storage key and a connect string cover the <em>where</em> of an artifact and nothing
    /// of the <em>what</em>: a checksum, a file size, a capture list, a pass/fail count, a device
    /// identifier. Section 27.7 gives each of the eight kinds a different body, and without somewhere
    /// to put that detail the card renders blanks for the very fields that let a person judge whether
    /// the change is right.
    /// </para>
    /// <para>
    /// JSON text rather than eight sets of columns, and rather than a mapped object graph: the kinds
    /// share almost no fields, most artifacts are one kind, and the domain owns no serialiser — the
    /// same reasoning as <c>Event.Payload</c> and <c>Spec.AcceptanceCriteria</c>. <c>null</c> means
    /// nothing was recorded, and the projection leaves the corresponding fields empty rather than
    /// inventing them.
    /// </para>
    /// </remarks>
    public string? Payload { get; private set; }

    /// <summary>How to actually use this thing (section 27.1).</summary>
    public string? InstructionsMd { get; private set; }

    /// <summary>
    /// Mandatory for anything stored, so the pruning job can run and storage costs cannot run away
    /// (section 27.5).
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    public VerificationArtifactAudience Audience { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static VerificationArtifact Pending(
        Guid sessionId,
        VerificationArtifactKind kind,
        VerificationArtifactAudience audience = VerificationArtifactAudience.Requester,
        DateTimeOffset? now = null,
        Guid? id = null)
        => new(
            id ?? Guid.CreateVersion7(),
            sessionId,
            kind,
            VerificationArtifactState.Pending,
            audience,
            DomainTime.Resolve(now));

    public void MarkReady(
        string? url = null,
        string? fileRef = null,
        string? connectString = null,
        string? instructionsMd = null,
        DateTimeOffset? expiresAt = null,
        string? payload = null)
    {
        if (Kind != VerificationArtifactKind.None
            && string.IsNullOrWhiteSpace(url)
            && string.IsNullOrWhiteSpace(fileRef)
            && string.IsNullOrWhiteSpace(connectString))
        {
            throw new ArgumentException(
                "A ready verification artifact needs a URL, a file reference or a connect string.",
                nameof(url));
        }

        Url = url?.Trim();
        FileRef = fileRef?.Trim();
        ConnectString = connectString?.Trim();
        InstructionsMd = instructionsMd;
        ExpiresAt = DomainTime.ResolveOptional(expiresAt);

        // Detail already recorded survives a re-ready with none supplied: the checksum does not stop
        // being true because the reachability check ran again.
        if (!string.IsNullOrWhiteSpace(payload))
        {
            RecordPayload(payload);
        }

        State = VerificationArtifactState.Ready;
    }

    /// <summary>
    /// Attaches the kind-specific detail of section 27.7, as the JSON the caller serialised.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="MarkReady"/> because detail can arrive after the URL does — a
    /// checksum is computed once the upload finishes, a test report is parsed after the run.
    /// </remarks>
    public void RecordPayload(string? payload)
        => Payload = string.IsNullOrWhiteSpace(payload) ? null : payload;

    /// <summary>
    /// Puts the artifact back to <see cref="VerificationArtifactState.Pending"/> — a rebuild, or a
    /// fresh deployment of the same change (sections 18, 27.7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The URL and the expiry are cleared, because that is the whole point: <c>Rebuild</c> is the
    /// primary action on an expired preview, and an artifact that went back to <c>pending</c> while
    /// still carrying the dead host would render a skeleton above a link that does not work. What was
    /// recorded about the artifact's <em>content</em> survives, since a rebuild of the same change
    /// produces the same thing at a new address.
    /// </para>
    /// <para>
    /// A no-op on an artifact that is already pending, so a reconciler that runs every few seconds
    /// does not rewrite a row per pass.
    /// </para>
    /// </remarks>
    public void MarkPending()
    {
        if (State == VerificationArtifactState.Pending)
        {
            return;
        }

        Url = null;
        ConnectString = null;
        ExpiresAt = null;
        State = VerificationArtifactState.Pending;
    }

    public void MarkFailed() => State = VerificationArtifactState.Failed;

    public void MarkExpired() => State = VerificationArtifactState.Expired;

    /// <summary>
    /// The state to render. Expiry is a designed state rather than a 404: expired previews are the
    /// number one source of confusion in tools like this (section 27.7).
    /// </summary>
    public VerificationArtifactState DisplayStateAt(DateTimeOffset now)
    {
        if (State != VerificationArtifactState.Ready || ExpiresAt is not { } expiresAt)
        {
            return State;
        }

        if (expiresAt <= now)
        {
            return VerificationArtifactState.Expired;
        }

        return expiresAt - now <= ExpiringWindow
            ? VerificationArtifactState.Expiring
            : VerificationArtifactState.Ready;
    }
}
