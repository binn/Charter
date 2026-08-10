using System.Collections.Frozen;
using Charter.Domain;

namespace Charter.Notifications;

/// <summary>
/// The two states that notify, as a closed set (section 6).
/// </summary>
/// <remarks>
/// <para>
/// Section 6 is unusually blunt about this: <em>only two states notify. Notifying on all of them
/// gets Charter muted within a week.</em> That is a product decision with a short half-life if it is
/// left as a convention, because every one of the other eleven states looks individually reasonable
/// to somebody at the moment they are adding it - <c>Merged</c> feels like good news, <c>Failed</c>
/// feels urgent, <c>SpecReady</c> feels like it needs an approver. The set is closed here so that
/// adding a third means editing this file and the test that pins it, rather than adding a call site.
/// </para>
/// <para>
/// <c>Failed</c> is the instructive omission. Section 6 renders it to a requester as <em>this turned
/// out to be bigger than expected — an engineer has been notified</em>: somebody is told, and it is
/// not the requester, and it is not by this path.
/// </para>
/// </remarks>
public static class NotifyWorthyStates
{
    private static readonly FrozenSet<RequestStatus> Set = new[]
    {
        RequestStatus.NeedsInput,
        RequestStatus.PreviewReady,
    }.ToFrozenSet();

    /// <summary>The whole set. Two members, and it stays two until section 6 changes.</summary>
    public static IReadOnlySet<RequestStatus> All => Set;

    /// <summary>Whether reaching <paramref name="status"/> sends anybody a message.</summary>
    public static bool Notifies(RequestStatus status) => Set.Contains(status);

    /// <summary>
    /// The requester-facing label from the section 6 table, for the states that notify.
    /// </summary>
    public static string Label(RequestStatus status) => status switch
    {
        RequestStatus.NeedsInput => "Question for you",
        RequestStatus.PreviewReady => "Ready to try",
        _ => throw new ArgumentOutOfRangeException(
            nameof(status),
            status,
            "Only NeedsInput and PreviewReady notify (section 6)."),
    };
}
