using Charter.Domain;

namespace Charter.Deployments;

/// <summary>
/// The change request a preview is wanted for (section 18).
/// </summary>
/// <remarks>
/// <para>
/// The head commit SHA is the key, not any Charter identifier and not a branch. Every hosting
/// provider already knows the commit it built without being told, which is what lets the generic
/// webhook of section 18 be first-class rather than a port of the Railway path — and it is the one
/// identifier that survives change spec 001 §A.7's warning not to assume a change request is a
/// branch. <see cref="HeadBranch"/> is carried because Railway and Render name their ephemeral
/// environments after it, and is nullable because a provider that has no branches would not supply
/// one.
/// </para>
/// <para>
/// <see cref="AuthorLogin"/> exists for one reason: Railway will not deploy a change request branch
/// from an account outside the workspace (section 18), and that failure presents as a preview that
/// simply never arrives. Naming the author is what turns it into a sentence an operator can act on.
/// </para>
/// </remarks>
/// <param name="RepoFullName"><c>owner/name</c>, as the version control provider spells it.</param>
/// <param name="Number">The change request number — pull request, merge request, changelist.</param>
/// <param name="HeadSha">The commit the preview must correspond to.</param>
/// <param name="HeadBranch">The branch, where the provider has such a thing.</param>
/// <param name="AuthorLogin">Who opened it, in the version control provider's namespace.</param>
/// <param name="HeadSeenAt">
/// When Charter last saw this head commit. A provider that can only detect a problem by absence — a
/// preview environment that was never created — needs to know how long absence has gone on before it
/// is willing to call it a problem. Null means "do not guess", and a provider must then stay on
/// <see cref="DeploymentAvailability.NotYet"/> however long it has been.
/// </param>
public sealed record DeploymentTarget(
    string RepoFullName,
    int Number,
    string HeadSha,
    string? HeadBranch = null,
    string? AuthorLogin = null,
    DateTimeOffset? HeadSeenAt = null);

/// <summary>What a provider was able to say about a preview.</summary>
public enum DeploymentAvailability
{
    /// <summary>
    /// Nothing yet, and nothing wrong. A build in flight, a comment that did not match, an
    /// environment that has not been created. Section 18: a parse failure is "not yet", never an
    /// error.
    /// </summary>
    NotYet,

    /// <summary>The provider reported a state, and <see cref="DeploymentObservation.Report"/> holds it.</summary>
    Reported,

    /// <summary>
    /// This provider cannot answer this question at all — a webhook-only platform asked to poll.
    /// Distinct from <see cref="NotYet"/> because retrying will never help.
    /// </summary>
    Unsupported,

    /// <summary>
    /// The preview is not coming and a human has to do something. The Railway case of section 18 —
    /// an author outside the workspace — is the reason this state exists.
    /// </summary>
    Blocked,
}

/// <summary>
/// One provider's account of a preview: where it is, what state it is in, and when it goes away.
/// </summary>
/// <param name="Provider">The provider id, lower-cased. Free text by design (section 18).</param>
/// <param name="State">Charter's common denominator, never the platform's own vocabulary.</param>
/// <param name="Url">The preview URL. Required when <paramref name="State"/> is ready.</param>
/// <param name="ExpiresAt">
/// When the preview stops working, where the provider knows. Section 27.7 makes expiry a designed
/// state rather than a 404, and the countdown has to be visible from first render — which requires
/// knowing the expiry at the moment the preview becomes ready, not when it lapses.
/// </param>
/// <param name="ExternalId">The provider's own handle, for logs and for teardown.</param>
/// <param name="Detail">One line, safe to log and to show an engineer. Never a stack trace.</param>
public sealed record DeploymentReport(
    string Provider,
    DeploymentState State,
    string? Url = null,
    DateTimeOffset? ExpiresAt = null,
    string? ExternalId = null,
    string? Detail = null);

/// <summary>The outcome of asking a provider about one change request.</summary>
/// <param name="Availability">What kind of answer this is.</param>
/// <param name="Report">Set when <paramref name="Availability"/> is
/// <see cref="DeploymentAvailability.Reported"/>, null otherwise.</param>
/// <param name="Explanation">
/// Plain language, for a log line or an engineer. Always present for
/// <see cref="DeploymentAvailability.Blocked"/>, because the whole value of that state is the
/// sentence.
/// </param>
public sealed record DeploymentObservation(
    DeploymentAvailability Availability,
    DeploymentReport? Report = null,
    string? Explanation = null)
{
    /// <summary>Nothing yet. The normal answer while a build is running.</summary>
    public static DeploymentObservation NotYet(string? explanation = null)
        => new(DeploymentAvailability.NotYet, null, explanation);

    /// <summary>A state to record.</summary>
    public static DeploymentObservation Reported(DeploymentReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new DeploymentObservation(DeploymentAvailability.Reported, report);
    }

    /// <summary>This provider does not answer this way; asking again will not change that.</summary>
    public static DeploymentObservation Unsupported(string explanation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(explanation);
        return new DeploymentObservation(DeploymentAvailability.Unsupported, null, explanation);
    }

    /// <summary>The preview is not coming until somebody does something.</summary>
    public static DeploymentObservation Blocked(string explanation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(explanation);
        return new DeploymentObservation(DeploymentAvailability.Blocked, null, explanation);
    }
}

/// <summary>A comment on a change request, as the fallback ingestion path of section 18 sees it.</summary>
/// <remarks>
/// The body is attacker-influenced input in the section 16 sense — anybody who can comment on a
/// change request can put text in one — so a parser reads it for a URL and a state and takes nothing
/// else from it. <see cref="AuthorLogin"/> is what lets a provider insist the comment came from its
/// own bot rather than from whoever pasted a convincing-looking line.
/// </remarks>
/// <param name="AuthorLogin">Who wrote it. <c>railway[bot]</c>, <c>render[bot]</c>, a person.</param>
/// <param name="Body">The comment text, verbatim.</param>
public sealed record DeploymentComment(string? AuthorLogin, string Body);

/// <summary>What tearing a preview down achieved.</summary>
/// <param name="TornDown">
/// True when the provider confirmed the environment is gone. False is not necessarily a failure: an
/// environment the platform already reclaimed cannot be torn down twice, and the artifact expires
/// either way.
/// </param>
/// <param name="Explanation">Plain language. Logged, never shown to a requester.</param>
public sealed record DeploymentTeardownResult(bool TornDown, string? Explanation = null)
{
    /// <summary>The environment is gone.</summary>
    public static DeploymentTeardownResult Confirmed { get; } = new(true);

    /// <summary>There was nothing to tear down.</summary>
    public static DeploymentTeardownResult NothingToDo(string explanation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(explanation);
        return new DeploymentTeardownResult(false, explanation);
    }
}

/// <summary>
/// What a deployment provider can actually do, so Charter degrades explicitly rather than assuming.
/// </summary>
/// <remarks>
/// The same shape as change spec 001 §A.3's version control capability model, and for the same
/// reason: the providers differ enormously, and a platform reached only by an inbound webhook is a
/// first-class configuration rather than a broken one. A provider with every flag false is still
/// usable — everything it reports arrives through <c>POST /api/deployments/{prSha}</c>.
/// </remarks>
/// <param name="Poll">Can be asked, on demand, what state a commit's preview is in.</param>
/// <param name="CommentParsing">Recognises its own bot's comment on a change request.</param>
/// <param name="Teardown">Can destroy an environment on request.</param>
/// <param name="NativeExpiry">Reports an expiry of its own, rather than Charter imposing one.</param>
public sealed record DeploymentProviderCapabilities(
    bool Poll,
    bool CommentParsing,
    bool Teardown,
    bool NativeExpiry)
{
    /// <summary>A platform that only ever calls the generic webhook.</summary>
    public static DeploymentProviderCapabilities WebhookOnly { get; } = new(false, false, false, false);
}

/// <summary>
/// The preview binding seam of section 18: bind a deployment to a change request's head commit,
/// report its state, expose its URL, and take it away again.
/// </summary>
/// <remarks>
/// <para>
/// Phase 1 ships exactly one implementation, <see cref="RailwayDeploymentProvider"/> (change spec
/// 001, implementation discipline). The interface exists from Phase 1 anyway, because retrofitting a
/// seam is expensive, and it is deliberately shaped around what <em>every</em> platform has rather
/// than what Railway has: a commit, a state, a URL, an expiry, and a way to stop paying for it.
/// </para>
/// <para>
/// An implementation reports; it never decides. It writes no rows, produces no verification
/// artifact, and holds no state between calls — the container can restart mid-session, so anything
/// remembered here would be lost (section 2.3). <see cref="DeploymentIngestor"/> and
/// <see cref="PreviewArtifactPublisher"/> own the consequences of what a provider says.
/// </para>
/// <para>
/// <strong>Neither ingestion path requires a provider.</strong> The generic webhook writes a
/// deployment on its own, which is what keeps a Render, Fly or Coolify self-hoster first-class. A
/// provider adds polling, comment recognition and teardown on top of that; a platform offering none
/// of the three is configured as no provider at all and still works.
/// </para>
/// </remarks>
public interface IDeploymentProvider
{
    /// <summary>
    /// The provider id, lower-cased: <c>railway</c>, <c>render</c>, <c>fly</c>. Stored verbatim on
    /// <see cref="Deployment.Provider"/>, so it is also what the generic webhook's callers send.
    /// </summary>
    string Id { get; }

    /// <summary>What this provider can be asked to do.</summary>
    DeploymentProviderCapabilities Capabilities { get; }

    /// <summary>
    /// How long a preview lives when the provider does not say, or <see langword="null"/> when
    /// previews are indefinite.
    /// </summary>
    /// <remarks>
    /// Section 27.7 requires the countdown to be visible from first render, so an expiry has to exist
    /// at the moment a preview becomes ready. Where a platform has no native expiry, this is the
    /// operator's configured lifetime — a stated policy rather than a guess, and the honest way to
    /// avoid the number one source of confusion in tools like this.
    /// </remarks>
    TimeSpan? PreviewLifetime { get; }

    /// <summary>Asks the provider what it has for this change request's head commit.</summary>
    /// <remarks>
    /// Returns <see cref="DeploymentAvailability.NotYet"/> rather than throwing for anything
    /// transient, and <see cref="DeploymentAvailability.Unsupported"/> when
    /// <see cref="DeploymentProviderCapabilities.Poll"/> is false.
    /// </remarks>
    Task<DeploymentObservation> ObserveAsync(DeploymentTarget target, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a change request comment — the fragile but universal fallback of section 18.
    /// </summary>
    /// <remarks>
    /// Synchronous and total: it never throws and never reports a failure. A comment it does not
    /// recognise is <see cref="DeploymentAvailability.NotYet"/>, because the overwhelmingly common
    /// case is a human talking about something else.
    /// </remarks>
    DeploymentObservation ReadComment(DeploymentComment comment, DeploymentTarget target);

    /// <summary>Destroys the preview environment for this change request.</summary>
    /// <remarks>
    /// Idempotent. Called when a change request closes and by the expiry sweep, both of which can run
    /// twice for the same environment after a restart.
    /// </remarks>
    Task<DeploymentTeardownResult> TeardownAsync(
        DeploymentTarget target,
        CancellationToken cancellationToken = default);
}
