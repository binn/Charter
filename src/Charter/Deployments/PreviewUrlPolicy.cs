using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Deployments;

/// <summary>What the policy made of one candidate preview URL.</summary>
/// <param name="Allowed">True when the URL may be stored, fetched, and shown to a requester.</param>
/// <param name="Url">The parsed URL, present only when allowed.</param>
/// <param name="Reason">
/// A one-line explanation, safe to return to the caller that supplied the URL. Null when allowed.
/// </param>
public sealed record PreviewUrlVerdict(bool Allowed, Uri? Url, string? Reason)
{
    /// <summary>An allowed URL.</summary>
    public static PreviewUrlVerdict Ok(Uri url) => new(true, url, null);

    /// <summary>A refusal, with the sentence the caller is told.</summary>
    public static PreviewUrlVerdict Refused(string reason) => new(false, null, reason);
}

/// <summary>Turns a host name into the addresses Charter would actually reach.</summary>
/// <remarks>
/// An interface rather than a direct <see cref="Dns"/> call so the rules below can be tested without
/// a network, and so a test can pin the case that matters most: a perfectly ordinary-looking name
/// that resolves to somewhere on the operator's own network.
/// </remarks>
public interface IPreviewHostResolver
{
    /// <summary>
    /// Every address <paramref name="host"/> resolves to, or an empty list when it resolves to none.
    /// </summary>
    /// <remarks>Never throws: resolution failure is an empty list.</remarks>
    ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>The real resolver, bounded so a hostile name cannot hold a request open.</summary>
public sealed class DnsPreviewHostResolver : IPreviewHostResolver
{
    /// <summary>How long resolution is given before the name counts as unresolvable.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(
        string host,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            return await Dns.GetHostAddressesAsync(host, timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException or OperationCanceledException)
        {
            return [];
        }
    }
}

/// <summary>
/// The one gate every preview URL passes before Charter stores it, fetches it, or puts it in front of
/// a requester.
/// </summary>
/// <remarks>
/// <para>
/// A preview URL arrives from the execution plane — a hosting platform's webhook, or a comment on a
/// change request — and section 16.3 is unambiguous about what that makes it: a value that may be
/// displayed, but never one the control plane may act on unchecked. Charter acts on this one twice
/// over. It fetches it on a loop for the reachability dot, from inside the control plane's own
/// network, which makes an unchecked URL a recurring server-side request forgery against anything the
/// container can reach — including a cloud provider's metadata endpoint. And it renders it as the
/// requester's preview button underneath Charter's own sentence, <em>"Nothing you do here touches the
/// real one"</em>, which is a promise made on Charter's authority to the person least equipped to
/// evaluate a link.
/// </para>
/// <para>
/// So the check lives here, once, rather than at each use. <see cref="DeploymentBinder"/> calls it
/// before a URL is ever written, which covers every ingestion path at once — a second consumer added
/// later cannot forget it, because there is nowhere for a URL to enter that does not go through the
/// binder.
/// </para>
/// <para>
/// <strong>Two rules, not one.</strong> <see cref="IsDisplayable"/> is the structural rule: what is
/// never a legitimate preview link no matter how an instance is configured — a scheme that is not
/// http(s), credentials in the userinfo, the loopback interface, a link-local address. It needs no
/// configuration and no network, so it can run at the point of display, on a row written before this
/// check existed. <see cref="ValidateAsync"/> is the full rule and adds what depends on this instance
/// and on DNS: private ranges, and the addresses a name actually resolves to. A name can point
/// anywhere, so the name alone settles nothing.
/// </para>
/// </remarks>
public sealed class PreviewUrlPolicy
{
    /// <summary>The longest preview URL Charter will store.</summary>
    public const int MaxUrlLength = 2048;

    /// <summary>Host names that always name the machine Charter itself is running on.</summary>
    private static readonly string[] BlockedSuffixes =
    [
        ".localhost",
        ".local",
        ".internal",
        ".home.arpa",
    ];

    private readonly DeploymentOptions _options;
    private readonly IPreviewHostResolver _resolver;
    private readonly ILogger<PreviewUrlPolicy> _logger;

    public PreviewUrlPolicy(
        DeploymentOptions options,
        IPreviewHostResolver? resolver = null,
        ILogger<PreviewUrlPolicy>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _resolver = resolver ?? new DnsPreviewHostResolver();
        _logger = logger ?? NullLogger<PreviewUrlPolicy>.Instance;
    }

    /// <summary>The policy an instance with no explicit configuration runs.</summary>
    /// <remarks>
    /// Strict, because the default has to be. A caller that wants the instance's own configuration
    /// resolves the registered singleton instead of reaching for this.
    /// </remarks>
    public static PreviewUrlPolicy Default { get; } = new(DeploymentOptions.WebhookOnly);

    /// <summary>Whether private addresses are reachable previews on this instance.</summary>
    public bool AllowsPrivateHosts => _options.AllowPrivatePreviewHosts;

    /// <summary>
    /// The structural rule: is this a URL that could ever legitimately be a requester's preview link?
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately permissive about private ranges and deliberately absolute about everything else.
    /// A self-hoster whose previews live on their own network has a legitimate 10.x preview URL, and
    /// their requester's browser is on that network too. Nobody has a legitimate
    /// <c>http://127.0.0.1:8080</c> preview — that address means "this container" to Charter and "this
    /// laptop" to the requester, and the two are never the same machine. Nobody has a legitimate
    /// <c>http://169.254.169.254/latest/meta-data/</c> either.
    /// </para>
    /// <para>
    /// This is the rule the API projection and the SPA both apply at the point of display, so it takes
    /// no configuration and does no I/O: an artifact row written before this check existed is still
    /// checked before its URL becomes a button.
    /// </para>
    /// </remarks>
    public static bool IsDisplayable(string? url) => Inspect(url, allowPrivateHosts: true).Allowed;

    /// <summary>
    /// Everything that can be decided from the URL text alone.
    /// </summary>
    /// <param name="url">The candidate.</param>
    /// <param name="allowPrivateHosts">
    /// Whether an address inside a private range is acceptable. Loopback, link-local and multicast are
    /// refused either way.
    /// </param>
    public static PreviewUrlVerdict Inspect(string? url, bool allowPrivateHosts)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return PreviewUrlVerdict.Refused("no preview url was named");
        }

        var trimmed = url.Trim();

        if (trimmed.Length > MaxUrlLength)
        {
            return PreviewUrlVerdict.Refused(
                $"a preview url must be at most {MaxUrlLength} characters");
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
        {
            return PreviewUrlVerdict.Refused("a preview url must be an absolute url");
        }

        if (parsed.Scheme is not ("http" or "https"))
        {
            return PreviewUrlVerdict.Refused("a preview url must use http or https");
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            // Credentials in a link are how a link is made to look like somewhere it is not, and the
            // requester is being told this one is safe to click.
            return PreviewUrlVerdict.Refused("a preview url must not carry credentials");
        }

        if (string.IsNullOrEmpty(parsed.Host))
        {
            return PreviewUrlVerdict.Refused("a preview url must name a host");
        }

        var host = parsed.Host.Trim('[', ']');

        if (IPAddress.TryParse(host, out var literal))
        {
            return IsAllowedAddress(literal, allowPrivateHosts)
                ? PreviewUrlVerdict.Ok(parsed)
                : PreviewUrlVerdict.Refused(Describe(literal, allowPrivateHosts));
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return PreviewUrlVerdict.Refused("a preview url must not point at the loopback interface");
        }

        foreach (var suffix in BlockedSuffixes)
        {
            if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return PreviewUrlVerdict.Refused(
                    "a preview url must name a host outside this instance's own network");
            }
        }

        return PreviewUrlVerdict.Ok(parsed);
    }

    /// <summary>
    /// The full rule: structure, then the addresses the host actually resolves to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A name that resolves to nothing is allowed through. It cannot be proven unsafe, it cannot be
    /// reached, and refusing it would turn a transient DNS failure into a preview permanently marked
    /// broken. Nothing rests on that leniency: the fetch path resolves again and checks the address it
    /// is about to open a socket to (<see cref="PreviewHttpClient"/>), which is the only check that
    /// cannot be raced by a name whose answer changes between validation and use.
    /// </para>
    /// </remarks>
    public async Task<PreviewUrlVerdict> ValidateAsync(string? url, CancellationToken cancellationToken = default)
    {
        var structural = Inspect(url, _options.AllowPrivatePreviewHosts);

        if (!structural.Allowed || structural.Url is not { } parsed)
        {
            return structural;
        }

        if (parsed.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            // Already settled by Inspect: a literal address needs no resolution.
            return structural;
        }

        var addresses = await _resolver.ResolveAsync(parsed.IdnHost, cancellationToken);

        if (addresses.Count == 0)
        {
            _logger.LogDebug(
                "Preview host {Host} resolves to no address; the fetch path will check again",
                parsed.IdnHost);

            return structural;
        }

        foreach (var address in addresses)
        {
            if (!IsAllowedAddress(address, _options.AllowPrivatePreviewHosts))
            {
                return PreviewUrlVerdict.Refused(Describe(address, _options.AllowPrivatePreviewHosts));
            }
        }

        return structural;
    }

    /// <summary>Whether Charter may open a socket to <paramref name="address"/>.</summary>
    public bool IsAllowedAddress(IPAddress address)
        => IsAllowedAddress(address, _options.AllowPrivatePreviewHosts);

    /// <summary>Whether an address is somewhere a preview could legitimately live.</summary>
    /// <remarks>
    /// <para>
    /// Refused always: the loopback interface, the unspecified address, link-local (which is where
    /// every cloud provider's instance metadata service lives, at <c>169.254.169.254</c>), multicast,
    /// and anything above it.
    /// </para>
    /// <para>
    /// Refused unless <paramref name="allowPrivateHosts"/>: RFC 1918, carrier-grade NAT, and IPv6
    /// unique local addresses. These are the ranges a self-hoster's own preview environment
    /// legitimately sits in, which is why they are the only ones an operator can opt back in to.
    /// </para>
    /// </remarks>
    public static bool IsAllowedAddress(IPAddress address, bool allowPrivateHosts)
    {
        ArgumentNullException.ThrowIfNull(address);

        var target = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (target.AddressFamily == AddressFamily.InterNetwork)
        {
            var octets = target.GetAddressBytes();

            return octets[0] switch
            {
                0 => false,                                             // "this network"
                127 => false,                                           // loopback
                10 => allowPrivateHosts,                                // RFC 1918
                169 when octets[1] == 254 => false,                     // link-local, and the metadata service
                172 when octets[1] >= 16 && octets[1] <= 31 => allowPrivateHosts,
                192 when octets[1] == 168 => allowPrivateHosts,
                100 when octets[1] >= 64 && octets[1] <= 127 => allowPrivateHosts,
                >= 224 => false,                                        // multicast, reserved, broadcast
                _ => true,
            };
        }

        if (target.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        if (IPAddress.IsLoopback(target) || target.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        if (target.IsIPv6LinkLocal || target.IsIPv6Multicast || target.IsIPv6SiteLocal)
        {
            return false;
        }

        // fc00::/7 — unique local, the IPv6 equivalent of RFC 1918.
        return (target.GetAddressBytes()[0] & 0xFE) != 0xFC || allowPrivateHosts;
    }

    /// <summary>The sentence a refused address is explained with.</summary>
    private static string Describe(IPAddress address, bool allowPrivateHosts)
    {
        var target = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (IPAddress.IsLoopback(target))
        {
            return "a preview url must not point at the loopback interface";
        }

        if (target.AddressFamily == AddressFamily.InterNetwork
            && target.GetAddressBytes() is [169, 254, ..])
        {
            return "a preview url must not point at a link-local address";
        }

        if (target.IsIPv6LinkLocal)
        {
            return "a preview url must not point at a link-local address";
        }

        return allowPrivateHosts
            ? "a preview url must resolve to a routable address"
            : "a preview url must resolve to a public address, "
              + "or CHARTER_PREVIEW_ALLOW_PRIVATE_HOSTS must be set on this instance";
    }
}
