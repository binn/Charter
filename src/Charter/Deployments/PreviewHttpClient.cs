using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace Charter.Deployments;

/// <summary>
/// The handler the control plane fetches a preview URL with.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PreviewUrlPolicy"/> checks a URL before it is stored; this checks the address a socket
/// is about to be opened to. Both are needed, and neither replaces the other: DNS can answer
/// differently between the two moments, deliberately so — the whole technique has a name — and a name
/// that resolved to a public address when the webhook arrived can resolve to <c>169.254.169.254</c>
/// fifteen seconds later when the reconcile loop probes it.
/// </para>
/// <para>
/// Three settings, each closing something the bare <see cref="HttpClient"/> left open:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <strong>Redirects are not followed.</strong> A redirect is a second URL that never passed the
/// policy, chosen by the same party that supplied the first. The probe only needs to know whether
/// something answered, and a <c>302</c> answers that as well as a <c>200</c> does.
/// </description>
/// </item>
/// <item>
/// <description>
/// <strong>Every connection is checked at the socket.</strong> The address is resolved here, filtered
/// through the policy, and connected to only if it survives — so the address that was checked is the
/// address that is dialled, with no window in between.
/// </description>
/// </item>
/// <item>
/// <description>
/// <strong>Nothing is sent that could authenticate Charter.</strong> No cookies, no default
/// credentials, no proxy — a proxy would carry the request to an address this handler never saw, which
/// would make the check above decorative.
/// </description>
/// </item>
/// </list>
/// </remarks>
public static class PreviewHttpClient
{
    /// <summary>What Charter identifies itself as when probing somebody's preview.</summary>
    public const string UserAgent = "Charter-preview-probe";

    /// <summary>The most of a response body that is ever buffered. The probe reads none of it.</summary>
    public const int MaxResponseBytes = 64 * 1024;

    /// <summary>How long a single connection attempt is given.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Builds the guarded handler.</summary>
    public static SocketsHttpHandler CreateHandler(PreviewUrlPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            Credentials = null,
            PreAuthenticate = false,
            ConnectTimeout = ConnectTimeout,
            MaxResponseHeadersLength = 32,
            ConnectCallback = (context, cancellationToken) => ConnectAsync(policy, context, cancellationToken),
        };
    }

    /// <summary>Applies the header defaults every preview request carries.</summary>
    public static void Configure(HttpClient client, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(client);

        client.Timeout = timeout;
        client.MaxResponseContentBufferSize = MaxResponseBytes;
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(UserAgent, "1.0"));
        client.DefaultRequestHeaders.ExpectContinue = false;
    }

    /// <summary>
    /// Resolves, checks, and connects — refusing rather than dialling an address the policy rejects.
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(
        PreviewUrlPolicy policy,
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(context);

        var endpoint = context.DnsEndPoint;

        var candidates = IPAddress.TryParse(endpoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken);

        var allowed = candidates.Where(policy.IsAllowedAddress).ToArray();

        if (allowed.Length == 0)
        {
            // Named rather than generic: this is the line an operator reads when a preview they
            // believe is fine will not probe, and "connection refused" would send them to the wrong
            // half of the system.
            throw new HttpRequestException(
                $"Charter will not open a connection to '{endpoint.Host}': it resolves to an address " +
                "inside this instance's own network.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync(allowed, endpoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
