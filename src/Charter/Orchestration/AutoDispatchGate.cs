using Charter.Auth.Authorization;
using Charter.Domain;
using Microsoft.Extensions.Logging;

namespace Charter.Orchestration;

/// <summary>Whether a specification nobody approved may become a session anyway (section 7.5).</summary>
/// <param name="Enabled">True when the spend gate opens without a human.</param>
/// <param name="Reason">
/// Plain language, safe to show the requester as <em>waiting on someone to approve</em>.
/// </param>
public sealed record AutoDispatchPermission(bool Enabled, string Reason)
{
    /// <summary>The refusal every "nothing applies" path returns. Deny by default.</summary>
    public static AutoDispatchPermission Blocked(string reason) => new(false, reason);
}

/// <summary>
/// The orchestrator's view of section 7.5's spend gate.
/// </summary>
/// <remarks>
/// A seam rather than a direct call into <see cref="ICharterAuthorizationService"/>, for one reason:
/// the orchestrator asks the question about a <em>request row</em>, and the authorization service
/// answers it about a member and a repository. Resolving one into the other in every caller is how a
/// dispatch path ends up with its own subtly different idea of who is trusted.
/// </remarks>
public interface IAutoDispatchGate
{
    /// <summary>Resolves the effective policy for whoever filed <paramref name="request"/>.</summary>
    Task<AutoDispatchPermission> PermitsAsync(Request request, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class AutoDispatchGate : IAutoDispatchGate
{
    private readonly ICharterAuthorizationService _authorization;
    private readonly ILogger<AutoDispatchGate> _logger;

    public AutoDispatchGate(ICharterAuthorizationService authorization, ILogger<AutoDispatchGate> logger)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(logger);

        _authorization = authorization;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AutoDispatchPermission> PermitsAsync(
        Request request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var member = await _authorization.ResolveMemberAsync(
            request.OrgId,
            request.RequesterId,
            cancellationToken);

        if (member is null)
        {
            // The person who filed it is no longer a member. Nothing auto-dispatches on behalf of
            // somebody who has left; an approver can still press the button.
            return AutoDispatchPermission.Blocked(
                "the person who filed this is no longer a member of the organisation");
        }

        var decision = await _authorization.ResolveAutoDispatchAsync(member, request.RepoId, cancellationToken);

        if (!decision.Enabled)
        {
            _logger.LogInformation(
                "Request {RequestId} does not auto-dispatch: {Reason}",
                request.Id,
                decision.Reason);
        }

        return new AutoDispatchPermission(decision.Enabled, decision.Reason);
    }
}
