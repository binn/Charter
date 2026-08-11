using Charter.Api.Changes;
using Charter.Configuration;
using Charter.Domain;
using Charter.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace Charter.Hosting;

/// <summary>
/// Something tried to leave the machine while <c>CHARTER_DEMO=true</c> (section 30.6).
/// </summary>
/// <remarks>
/// Thrown rather than quietly returning a canned response, and that is the whole design. A demo mode
/// that silently swallows egress is indistinguishable from one that is broken, and the security
/// claim - <em>this instance contacts nobody</em> - would rest on a promise instead of on evidence.
/// A thrown exception is loud, is attributable to a request URI, and fails a test.
/// </remarks>
public sealed class DemoModeException : Exception
{
    /// <summary>Creates an exception with <paramref name="message"/>.</summary>
    public DemoModeException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with <paramref name="message"/> and <paramref name="innerException"/>.</summary>
    public DemoModeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates an exception with no message. Present for the framework's benefit only.</summary>
    public DemoModeException()
    {
    }

    /// <summary>The sentence every refusal carries, so one grep finds all of them.</summary>
    public const string Explanation =
        "CHARTER_DEMO=true: this instance makes no outbound calls (section 30.6).";

    /// <summary>A refusal naming what was attempted.</summary>
    public static DemoModeException For(string attempt)
        => new($"{Explanation} Blocked: {attempt}.");
}

/// <summary>
/// The outbound HTTP kill switch of section 30.6.
/// </summary>
/// <remarks>
/// <para>
/// Installed on <em>every</em> named client rather than on an enumerated list of them. Every HTTP
/// egress in Charter goes through <c>IHttpClientFactory</c> - the model clients, the OpenRouter price
/// catalog, the GitHub App, the OAuth token exchange, the Railway provider and the preview
/// reachability probe - and a list would be correct on the day it was written and wrong on the day
/// somebody adds the seventh. Blocking at the factory means a new client is blocked before its author
/// has heard of demo mode.
/// </para>
/// <para>
/// This deliberately does not touch Postgres, or the Seq and OTLP sinks. Those are the operator's own
/// infrastructure, addressed by the operator's own configuration; section 30.6's promise is that
/// evaluating Charter costs no token and needs no GitHub App, not that a container may not talk to
/// the log server the operator pointed it at.
/// </para>
/// </remarks>
public sealed class DemoModeHttpHandler(ILogger<DemoModeHttpHandler> logger) : DelegatingHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Host only: a query string can carry a token, and this line is going to a log platform.
        var target = request.RequestUri is null ? "an unnamed host" : request.RequestUri.GetLeftPart(UriPartial.Authority);

        logger.LogWarning(
            "Demo mode blocked an outbound {Method} to {Target}. {Explanation}",
            request.Method.Method,
            target,
            DemoModeException.Explanation);

        return Task.FromException<HttpResponseMessage>(
            DemoModeException.For($"{request.Method.Method} to {target}"));
    }
}

/// <summary>
/// An <see cref="ISmtpTransport"/> that opens no socket.
/// </summary>
/// <remarks>
/// Belt and braces over forcing <see cref="NullEmailProvider"/>: the provider is the switch that
/// makes email degrade cleanly, and this is the one that makes a future caller resolving the
/// transport directly fail loudly instead of connecting to a mail server. SMTP is the one egress path
/// in Charter that does not go through <c>IHttpClientFactory</c>, so it needs its own stop.
/// </remarks>
public sealed class DemoModeSmtpTransport : ISmtpTransport
{
    /// <inheritdoc />
    public Task<SmtpTransportResult> SendAsync(
        SmtpEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        return Task.FromException<SmtpTransportResult>(DemoModeException.For("an SMTP delivery"));
    }
}

/// <summary>
/// Pane 3's file reader under demo mode: unavailable, rather than a blocked outbound call.
/// </summary>
/// <remarks>
/// <para>
/// <c>GitHubRepositoryFileText</c> fetches file contents from the provider per file, so under
/// <c>CHARTER_DEMO=true</c> it meets the kill switch and throws <see cref="DemoModeException"/>. That
/// escapes as a 500: the seeded repository does not exist on GitHub, so the call was never going to
/// succeed on its own terms, and a thrown egress refusal is the right answer to a *real* instance
/// reaching out but the wrong answer here.
/// </para>
/// <para>
/// <c>FileDiffService</c> already has a designed degrade path for exactly this case, so demo mode
/// takes it: every other pane keeps working - the file list, the recap, the transcript, the status
/// thread - and opening a file explains itself instead of erroring.
/// </para>
/// </remarks>
public sealed class DemoModeRepositoryFileText : IRepositoryFileText
{
    private const string Sentence =
        "This demo instance has no repository behind it, so there is no file content to diff. "
        + "The list of changed files, the recap and the transcript are all real seeded data.";

    /// <inheritdoc />
    public Task<FileText?> ReadAsync(
        Repo repo,
        string revision,
        string path,
        CancellationToken cancellationToken = default)
        => Task.FromException<FileText?>(new FileTextUnavailableException(Sentence));
}

/// <summary>Registers demo mode (section 30.6): the kill switch and the seed data.</summary>
public static class DemoModeServiceCollectionExtensions
{
    /// <summary>
    /// Disables every outbound call and seeds demonstration data when <c>CHARTER_DEMO=true</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A no-op otherwise, and that is the only branch in the application that reads
    /// <see cref="CharterConfig.DemoMode"/>. Section 7.2's rule applies here as much as to personal
    /// mode: demo mode must not become a second code path threaded through the app, so it is one
    /// composition-time decision - different registrations, identical code - rather than a flag every
    /// subsystem has to remember to check.
    /// </para>
    /// <para>
    /// Must be called before <c>AddCharterApi</c>, which reaches <c>AddCharterNotifications</c>: the
    /// email registrations there are <c>TryAdd</c>, so the demo transport and provider have to be in
    /// place first to win.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddCharterDemoMode(this IServiceCollection services, CharterConfig config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        if (config.OutboundCallsAllowed)
        {
            return services;
        }

        // Every named HttpClient, including ones registered after this call. A fresh handler per
        // chain: a DelegatingHandler instance may only ever have one inner handler assigned.
        services.ConfigureAll<HttpClientFactoryOptions>(options =>
            options.HttpMessageHandlerBuilderActions.Add(builder =>
                builder.AdditionalHandlers.Add(
                    new DemoModeHttpHandler(
                        builder.Services.GetRequiredService<ILogger<DemoModeHttpHandler>>()))));

        // The SMTP socket, which is not an HttpClient. NullEmailProvider is the same clean-degrade
        // path CHARTER_EMAIL_PROVIDER=none already takes, so nothing above it changes behaviour.
        services.AddSingleton<ISmtpTransport, DemoModeSmtpTransport>();
        services.AddSingleton<IEmailProvider, NullEmailProvider>();

        // Pane 3's per-file diff, for the same reason and by the same rule: AddCharterApi registers
        // the GitHub-backed reader with TryAddScoped, so this has to be in place first to win.
        services.AddScoped<IRepositoryFileText, DemoModeRepositoryFileText>();

        services.AddHostedService<Charter.Data.Demo.DemoSeedHostedService>();

        return services;
    }
}
