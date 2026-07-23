using System.Net;

namespace PeerSharp.Internals.Network;

/// <summary>
/// Abstraction for DNS resolution to facilitate testing.
/// </summary>
internal interface IDnsResolver
{
    Task<IPAddress[]> GetHostAddressesAsync(string hostNameOrAddress, CancellationToken cancellationToken = default);
}

internal class SystemDnsResolver : IDnsResolver
{
    public async Task<IPAddress[]> GetHostAddressesAsync(string hostNameOrAddress, CancellationToken cancellationToken = default)
    {
        return await Dns.GetHostAddressesAsync(hostNameOrAddress, cancellationToken).ConfigureAwait(false);
    }
}
