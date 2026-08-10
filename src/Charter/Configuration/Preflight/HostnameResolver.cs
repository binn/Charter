using System.Net;
using System.Net.Sockets;

namespace Charter.Configuration.Preflight;

/// <summary>
/// Name resolution, behind a seam so the base URL check can be tested without DNS.
/// </summary>
public interface IHostnameResolver
{
    /// <summary>True when <paramref name="host"/> resolves to at least one address.</summary>
    Task<bool> CanResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IHostnameResolver"/> over the platform resolver.
/// </summary>
/// <remarks>
/// This is why base URL validation is a preflight check and not part of the config parser: a DNS
/// lookup can hang, and a startup parser that hangs is worse than one that reports a bad value.
/// </remarks>
public sealed class SystemHostnameResolver : IHostnameResolver
{
    /// <inheritdoc />
    public async Task<bool> CanResolveAsync(string host, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        if (IPAddress.TryParse(host, out _))
        {
            return true;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            return addresses.Length > 0;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
