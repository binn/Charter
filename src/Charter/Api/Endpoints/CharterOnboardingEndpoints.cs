using System.Globalization;
using Charter.Api.Contracts;
using Charter.Api.Repos;
using Charter.Auth.Authorization;
using Microsoft.AspNetCore.Routing;

namespace Charter.Api.Endpoints;

/// <summary>
/// The repo onboarding wizard of section 9, over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// Five steps and one gate: connect, recon, confirm scope — which opens the config pull request and
/// queues the smoke test — the smoke-test outcome, the primer, and the merge-gate assessment. The
/// wizard ends in proof rather than configuration, so the interesting read is
/// <c>GET /api/repos/{id}</c>: it says where a repository is and what the last run actually did.
/// </para>
/// <para>
/// <b>Nothing here makes a repository requestable.</b> There is no endpoint that sets
/// <c>ready</c> — the only path to it is a passing smoke test reported by the execution plane
/// through <c>IOnboardingRunCallbacks</c>, because section 9 says readiness is earned and an
/// administrative override would make that a convention rather than a property.
/// </para>
/// </remarks>
public static class CharterOnboardingEndpoints
{
    /// <summary>Maps the section 9 routes under <c>/api/repos</c>.</summary>
    public static IEndpointRouteBuilder MapCharterOnboardingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var repos = endpoints
            .MapGroup("/api/repos")
            .RequireAuthorization()
            .AddEndpointFilter<PlainLanguageFailureFilter>();

        repos.MapGet("/", async (
            HttpContext http,
            ICharterAuthorizationService authorization,
            RepoOnboardingService onboarding,
            CancellationToken cancellationToken) =>
        {
            var member = await CharterCaller.ResolveAsync(http.User, authorization, cancellationToken);
            if (member is null)
            {
                return ApiProblems.Unauthorized();
            }

            var (outcome, listed) = await onboarding.ListAsync(member, cancellationToken);

            return outcome.Succeeded && listed is not null ? Json(listed) : CharterApiEndpoints.ToResult(outcome);
        });

        repos.MapPost("/", async (
            ConnectRepoBody body,
            HttpContext http,
            ICharterAuthorizationService authorization,
            RepoOnboardingService onboarding,
            CancellationToken cancellationToken) =>
        {
            var member = await CharterCaller.ResolveAsync(http.User, authorization, cancellationToken);
            if (member is null)
            {
                return ApiProblems.Unauthorized();
            }

            var (outcome, connected) = await onboarding.ConnectAsync(
                member,
                body ?? new ConnectRepoBody(),
                cancellationToken);

            return outcome.Succeeded && connected is not null
                ? Results.Json(connected, CharterApiJson.Options, statusCode: StatusCodes.Status201Created)
                : CharterApiEndpoints.ToResult(outcome);
        });

        repos.MapGet("/{id}", async (
            string id,
            HttpContext http,
            ICharterAuthorizationService authorization,
            RepoOnboardingService onboarding,
            CancellationToken cancellationToken) =>
        {
            var (member, repoId, failure) = await ResolveAsync(http, authorization, id, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            var (outcome, described) = await onboarding.DescribeAsync(member!, repoId, cancellationToken);

            return outcome.Succeeded && described is not null ? Json(described) : CharterApiEndpoints.ToResult(outcome);
        });

        repos.MapPost("/{id}/recon", async (
                string id,
                HttpContext http,
                ICharterAuthorizationService authorization,
                RepoOnboardingService onboarding,
                CancellationToken cancellationToken) =>
            {
                var (member, repoId, failure) = await ResolveAsync(http, authorization, id, cancellationToken);
                if (failure is not null)
                {
                    return failure;
                }

                var (outcome, result) = await onboarding.StartReconAsync(member!, repoId, cancellationToken);

                return Answer(outcome, result);
            })

            // Recon is an agent run over the whole repository. Section 31 counts it as intake.
            .WithMetadata(IntakeRateLimitMetadata.Instance);

        repos.MapPost("/{id}/scope", async (
                string id,
                ConfirmScopeBody body,
                HttpContext http,
                ICharterAuthorizationService authorization,
                RepoOnboardingService onboarding,
                CancellationToken cancellationToken) =>
            {
                var (member, repoId, failure) = await ResolveAsync(http, authorization, id, cancellationToken);
                if (failure is not null)
                {
                    return failure;
                }

                var (outcome, result) = await onboarding.ConfirmScopeAsync(
                    member!,
                    repoId,
                    body ?? new ConfirmScopeBody(),
                    cancellationToken);

                return Answer(outcome, result);
            })

            // Confirming scope opens a pull request and queues the smoke test, which is a session.
            .WithMetadata(IntakeRateLimitMetadata.Instance);

        repos.MapGet("/{id}/smoke-test", async (
            string id,
            HttpContext http,
            ICharterAuthorizationService authorization,
            RepoOnboardingService onboarding,
            CancellationToken cancellationToken) =>
        {
            var (member, repoId, failure) = await ResolveAsync(http, authorization, id, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            var (outcome, smokeTest) = await onboarding.SmokeTestAsync(member!, repoId, cancellationToken);

            // A repository whose smoke test has never run answers `null` rather than 404: the
            // repository exists, the run does not, and the wizard renders that as a step to do.
            return outcome.Succeeded
                ? CharterApiJson.NullOr(smokeTest)
                : CharterApiEndpoints.ToResult(outcome);
        });

        repos.MapGet("/{id}/access", async (
            string id,
            HttpContext http,
            ICharterAuthorizationService authorization,
            RepoOnboardingService onboarding,
            CancellationToken cancellationToken) =>
        {
            var (member, repoId, failure) = await ResolveAsync(http, authorization, id, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            var (outcome, access) = await onboarding.DescribeAccessAsync(member!, repoId, cancellationToken);

            return outcome.Succeeded && access is not null ? Json(access) : CharterApiEndpoints.ToResult(outcome);
        });

        repos.MapPost("/{id}/access", async (
            string id,
            RepoAccessGrantBody body,
            HttpContext http,
            ICharterAuthorizationService authorization,
            RepoOnboardingService onboarding,
            CancellationToken cancellationToken) =>
        {
            var (member, repoId, failure) = await ResolveAsync(http, authorization, id, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            var (outcome, access) = await onboarding.SetAccessAsync(
                member!,
                repoId,
                body ?? new RepoAccessGrantBody(),
                cancellationToken);

            return outcome.Succeeded && access is not null ? Json(access) : CharterApiEndpoints.ToResult(outcome);
        });

        repos.MapPost("/{id}/primer", async (
            string id,
            PublishPrimerBody body,
            HttpContext http,
            ICharterAuthorizationService authorization,
            RepoOnboardingService onboarding,
            CancellationToken cancellationToken) =>
        {
            var (member, repoId, failure) = await ResolveAsync(http, authorization, id, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            var (outcome, result) = await onboarding.PublishPrimerAsync(
                member!,
                repoId,
                body ?? new PublishPrimerBody(),
                cancellationToken);

            return Answer(outcome, result);
        });

        return endpoints;
    }

    /// <summary>
    /// A refused step still carries the state machine's own explanation, because "that cannot happen
    /// from here" is only useful with the reason attached (section 11).
    /// </summary>
    private static IResult Answer(Requests.CommandOutcome outcome, OnboardingActionResponse? result)
        => outcome.Succeeded && result is not null ? Json(result) : CharterApiEndpoints.ToResult(outcome);

    private static async Task<(MemberSnapshot? Member, Guid RepoId, IResult? Failure)> ResolveAsync(
        HttpContext http,
        ICharterAuthorizationService authorization,
        string id,
        CancellationToken cancellationToken)
    {
        var member = await CharterCaller.ResolveAsync(http.User, authorization, cancellationToken);
        if (member is null)
        {
            return (null, Guid.Empty, ApiProblems.Unauthorized());
        }

        return Guid.TryParse(id, CultureInfo.InvariantCulture, out var repoId)
            ? (member, repoId, null)
            : (null, Guid.Empty, ApiProblems.NotFound());
    }

    private static IResult Json<T>(T value) => Results.Json(value, CharterApiJson.Options);
}
