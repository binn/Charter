namespace Charter.Models;

/// <summary>
/// The status and headers of the last non-success response seen on the current logical call.
/// </summary>
internal sealed class ModelHttpDiagnostics
{
    /// <summary>The status code, if a response was seen.</summary>
    public System.Net.HttpStatusCode? StatusCode { get; set; }

    /// <summary>The response headers, flattened to first-value-wins.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Makes response headers visible to code that only sees the SDK's exception type.
/// </summary>
/// <remarks>
/// <para>
/// The official Anthropic SDK surfaces the status code and body on its exceptions but not the
/// headers, and section 20b.4 needs the reset header off a <c>429</c> to record
/// <c>exhausted_until</c>. Rather than reimplement the Messages API to get at one header, the scope
/// hands a mutable holder to a delegating handler further down the pipeline, which fills it in.
/// </para>
/// <para>
/// The holder is allocated by the caller and mutated by the handler, rather than assigned by the
/// handler: an <see cref="AsyncLocal{T}"/> assignment inside the request's own execution context
/// would not flow back out to the caller, but a mutation of an object the caller already holds does.
/// </para>
/// </remarks>
internal sealed class ModelHttpDiagnosticsScope : IDisposable
{
    private static readonly AsyncLocal<ModelHttpDiagnostics?> Ambient = new();

    private readonly ModelHttpDiagnostics? _previous;
    private bool _disposed;

    /// <summary>Starts a scope.</summary>
    public ModelHttpDiagnosticsScope()
    {
        _previous = Ambient.Value;
        Diagnostics = new ModelHttpDiagnostics();
        Ambient.Value = Diagnostics;
    }

    /// <summary>The holder the handler writes into.</summary>
    public ModelHttpDiagnostics Diagnostics { get; }

    /// <summary>The holder for the current logical call, if any.</summary>
    public static ModelHttpDiagnostics? Current => Ambient.Value;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Ambient.Value = _previous;
    }
}

/// <summary>
/// Records the status and headers of non-success responses into the ambient
/// <see cref="ModelHttpDiagnosticsScope"/>. Reads nothing from the body and logs nothing.
/// </summary>
internal sealed class ModelHttpDiagnosticsHandler : DelegatingHandler
{
    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode && ModelHttpDiagnosticsScope.Current is { } diagnostics)
        {
            diagnostics.StatusCode = response.StatusCode;
            diagnostics.Headers = RateLimitResetParser.Flatten(response.Headers);
        }

        return response;
    }
}
