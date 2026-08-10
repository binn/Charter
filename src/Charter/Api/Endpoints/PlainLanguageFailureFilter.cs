using Microsoft.Extensions.Logging;

namespace Charter.Api.Endpoints;

/// <summary>
/// Turns an unhandled failure into one plain sentence.
/// </summary>
/// <remarks>
/// <para>
/// Section 11: <em>"A non-engineer who sees a stack trace once never files again."</em> The exception
/// is logged in full — that is what the engineer needs — and the response carries nothing but a
/// sentence, because the person on the other end of this API is frequently not an engineer and the
/// difference between a dignified failure and a wall of red text is whether they ever come back.
/// </para>
/// <para>
/// This is a filter on the API group rather than <c>UseExceptionHandler</c> in the host pipeline, so
/// it applies to exactly these routes and does not change what the rest of the process does with an
/// exception.
/// </para>
/// </remarks>
public sealed class PlainLanguageFailureFilter : IEndpointFilter
{
    private readonly ILogger<PlainLanguageFailureFilter> logger;

    public PlainLanguageFailureFilter(ILogger<PlainLanguageFailureFilter> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        this.logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        try
        {
            return await next(context);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "Unhandled failure in {Method} {Path}",
                context.HttpContext.Request.Method,
                context.HttpContext.Request.Path);

            return ApiProblems.Unexpected();
        }
    }
}
