using Microsoft.AspNetCore.Http;

namespace Charter.Api;

/// <summary>
/// Every non-2xx this API produces, as <c>application/problem+json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Section 11: <em>"A non-engineer who sees a stack trace once never files again."</em> Every
/// <c>detail</c> below is a sentence written for the person reading it. Exception text, SQL, internal
/// identifiers and authorisation internals never reach this type — the plain-language reason from
/// <see cref="Charter.Auth.Authorization.AuthorizationDecision.Reason"/> is safe to show and is used
/// verbatim; anything else is replaced.
/// </para>
/// <para>
/// Section 7.3: a missing record and an unauthorised one both return <see cref="NotFound"/> with the
/// same words, so the API never becomes an existence oracle for work in another organisation.
/// </para>
/// </remarks>
public static class ApiProblems
{
    /// <summary>The sentence a requester sees when something went wrong on our side.</summary>
    public const string UnexpectedFailureDetail =
        "Something went wrong on our side. Nothing you did caused it, and it has been logged for us to "
        + "look at. Try again in a moment.";

    /// <summary>Not signed in, or the session expired.</summary>
    public static IResult Unauthorized() => Results.Problem(
        title: "You are not signed in",
        detail: "Sign in again and we will bring you back here.",
        statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>
    /// The caller is signed in but this is not theirs. Also the answer for "does not exist", so the
    /// two are indistinguishable from outside (section 7.3).
    /// </summary>
    public static IResult NotFound() => Results.Problem(
        title: "We could not find that",
        detail: "It may have been removed, or it may belong to someone else.",
        statusCode: StatusCodes.Status404NotFound);

    /// <summary>A refusal the caller is allowed to know the reason for.</summary>
    public static IResult Forbidden(string reason) => Results.Problem(
        title: "You do not have access to this",
        detail: string.IsNullOrWhiteSpace(reason)
            ? "Ask an administrator to give you access."
            : Sentence(reason),
        statusCode: StatusCodes.Status403Forbidden);

    /// <summary>The request body did not make sense.</summary>
    public static IResult BadRequest(string detail) => Results.Problem(
        title: "We could not use that",
        detail: Sentence(detail),
        statusCode: StatusCodes.Status400BadRequest);

    /// <summary>The state machine does not allow this here (section 6).</summary>
    public static IResult Conflict(string detail) => Results.Problem(
        title: "That cannot happen right now",
        detail: Sentence(detail),
        statusCode: StatusCodes.Status409Conflict);

    /// <summary>Section 31: intake rate limiting, per user and per organisation.</summary>
    public static IResult TooManyRequests() => Results.Problem(
        title: "That is a lot of requests at once",
        detail: "You have filed several requests in a very short time. Give it a minute and try again — "
                + "nothing you already sent has been lost.",
        statusCode: StatusCodes.Status429TooManyRequests);

    /// <summary>
    /// Something this instance is not set up to do. Not the caller's mistake, and not a crash.
    /// </summary>
    /// <remarks>
    /// Pane 3 needs to read repository content, and an instance with no version-control client
    /// wired cannot. Answering 404 would say the file does not exist and 500 would say Charter
    /// broke; both send somebody looking in the wrong place.
    /// </remarks>
    public static IResult Unavailable(string detail) => Results.Problem(
        title: "Charter cannot do that here",
        detail: string.IsNullOrWhiteSpace(detail)
            ? "This instance is not configured for that. An administrator can change it in settings."
            : Sentence(detail),
        statusCode: StatusCodes.Status503ServiceUnavailable);

    /// <summary>An unhandled failure, rendered without a stack trace (section 11).</summary>
    public static IResult Unexpected() => Results.Problem(
        title: "Something went wrong",
        detail: UnexpectedFailureDetail,
        statusCode: StatusCodes.Status500InternalServerError);

    private static string Sentence(string text)
    {
        var trimmed = text.Trim();

        if (trimmed.Length == 0)
        {
            return UnexpectedFailureDetail;
        }

        var capitalised = char.IsUpper(trimmed[0])
            ? trimmed
            : string.Concat(char.ToUpperInvariant(trimmed[0]).ToString(), trimmed.AsSpan(1));

        return capitalised.EndsWith('.') || capitalised.EndsWith('!') || capitalised.EndsWith('?')
            ? capitalised
            : capitalised + ".";
    }
}
