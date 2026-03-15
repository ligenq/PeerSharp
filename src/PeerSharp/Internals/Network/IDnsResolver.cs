using System.Net;

namespace PeerSharp.Internals.Network;

/// <summary>
/// Abstraction for DNS resolution to facilitate testing.
/// </summary>
internal interface IDnsResolver
{
    IPAddress[] GetHostAddresses(string hostNameOrAddress);
}

internal class SystemDnsResolver : IDnsResolver
{
    public IPAddress[] GetHostAddresses(string hostNameOrAddress)
    {
        return Dns.GetHostAddresses(hostNameOrAddress);
    }
}
