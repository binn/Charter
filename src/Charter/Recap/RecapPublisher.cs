using Charter.Domain;
using Charter.VersionControl;
using Microsoft.Extensions.Logging;

namespace Charter.Recaps;

/// <summary>Where the recap ended up.</summary>
public enum RecapSurface
{
    /// <summary>Posted as a comment on the change request. Section 14's default and preference.</summary>
    ProviderComment,

    /// <summary>
    /// Rendered in Charter's session view only, because the provider could not take it. The body
    /// says so; a recap that quietly failed to post is a recap nobody reads.
    /// </summary>
    SessionView,
}

/// <summary>The outcome of one publication attempt.</summary>
/// <param name="Surface">Where it landed.</param>
/// <param name="BodyMarkdown">The body as published, including the fallback notice where there is one.</param>
/// <param name="Reason">
/// Why it fell back, or <see langword="null"/> when it did not. One line, safe to show an engineer.
/// </param>
public sealed record RecapPublication(RecapSurface Surface, string BodyMarkdown, string? Reason)
{
    /// <summary>Whether the provider took the comment.</summary>
    public bool PostedToProvider => Surface == RecapSurface.ProviderComment;
}

/// <summary>Posts the engineer recap where engineers actually review (section 14).</summary>
public interface IRecapPublisher
{
    /// <summary>
    /// Posts the recap as a change request comment, falling back to the session view.
    /// </summary>
    /// <param name="recap">The generated recap.</param>
    /// <param name="repo">The repository the session ran against.</param>
    /// <param name="changeRequestNumber">
    /// The change request to comment on, or <see langword="null"/> when the session never opened one.
    /// </param>
    Task<RecapPublication> PublishAsync(
        RecapResult recap,
        Repo repo,
        int? changeRequestNumber,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Section 14, rule one: <em>post it as a change request comment where the provider has one, and in
/// the session view where it does not. Not just in Charter — engineers review on the provider.</em>
/// </summary>
/// <remarks>
/// <para>
/// A recap that only exists inside Charter is a recap an engineer has to go and find, and they will
/// not, because the review is happening in a browser tab pointed at the provider. So the provider is
/// the primary surface and Charter is the copy, not the other way round.
/// </para>
/// <para>
/// Every way this can fail ends in the recap still existing. No comment capability (part A.3), no
/// change request, a provider that returns false, a provider that throws — each falls back to the
/// session view <em>and says so in the body</em>. Silently dropping it would be the one outcome that
/// makes the feature untrustworthy: an engineer who has seen a recap appear once expects it always.
/// </para>
/// </remarks>
public sealed class RecapPublisher : IRecapPublisher
{
    private readonly IVersionControlProviderRegistry _registry;
    private readonly ILogger<RecapPublisher> _logger;

    /// <summary>Creates a publisher.</summary>
    public RecapPublisher(IVersionControlProviderRegistry registry, ILogger<RecapPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RecapPublication> PublishAsync(
        RecapResult recap,
        Repo repo,
        int? changeRequestNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recap);
        ArgumentNullException.ThrowIfNull(repo);

        IVersionControlProvider provider;
        RepoRef reference;
        try
        {
            provider = _registry.For(repo);
            reference = _registry.ReferenceFor(repo);
        }
        catch (VersionControlCapabilityException exception)
        {
            _logger.LogWarning(
                exception,
                "No version control provider is registered for {Repository}; the recap for session "
                + "{SessionId} stays in Charter",
                repo.FullName,
                recap.SessionId);

            return Fallback(recap, "no version control provider is configured for this repository");
        }

        var terms = provider.Terms;

        if (changeRequestNumber is not { } number)
        {
            return Fallback(recap, $"this session has no open {terms.ChangeRequest}");
        }

        if (!provider.Capabilities.ChangeRequests || !provider.Capabilities.ChangeRequestComments)
        {
            // Part A.3: the capability is a declaration about the provider, and section 14 says
            // explicitly where it is false the recap goes in the session view instead.
            return Fallback(
                recap,
                $"{provider.DisplayName} has no comment surface on a {terms.ChangeRequest}");
        }

        try
        {
            var posted = await provider
                .CommentOnChangeRequestAsync(
                    new ChangeRequestRef(reference, number),
                    recap.BodyMarkdown,
                    cancellationToken)
                .ConfigureAwait(false);

            if (posted)
            {
                _logger.LogInformation(
                    "Posted the recap for session {SessionId} on {Term} #{Number}",
                    recap.SessionId,
                    terms.ChangeRequest,
                    number);

                return new RecapPublication(RecapSurface.ProviderComment, recap.BodyMarkdown, Reason: null);
            }

            return Fallback(
                recap,
                $"{provider.DisplayName} declined the comment on {terms.ChangeRequest} #{number}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Could not comment the recap for session {SessionId} on {Term} #{Number}",
                recap.SessionId,
                terms.ChangeRequest,
                number);

            return Fallback(recap, $"{provider.DisplayName} could not be reached: {exception.Message}");
        }
    }

    private static RecapPublication Fallback(RecapResult recap, string reason)
    {
        var notice =
            $"> This recap could not be posted where the change is being reviewed — {reason}. It is "
            + "visible in Charter only.";

        return new RecapPublication(
            RecapSurface.SessionView,
            notice + "\n\n" + recap.BodyMarkdown,
            reason);
    }
}
