using System.Net;
using System.Net.Sockets;

namespace PeerSharp.Internals.Utilities;

/// <summary>
/// Stateless network utilities for IP address manipulation.
/// </summary>
internal static class NetworkUtils
{
    /// <summary>
    /// This machine's own globally routable IPv6 address, or null when it has none.
    ///
    /// <para>
    /// Globally routable is the whole point, so link-local (fe80::) is excluded: every machine has one
    /// of those whether or not it has IPv6 connectivity, and handing it to a tracker would publish an
    /// address no peer outside the local segment can reach. Unique-local (fc00::/7) is excluded for
    /// the same reason.
    /// </para>
    /// </summary>
    public static IPAddress? GetGlobalIPv6Address()
    {
        try
        {
            foreach (var iface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up
                    || iface.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in iface.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily == AddressFamily.InterNetworkV6
                        && !address.IsIPv6LinkLocal
                        && !address.IsIPv6SiteLocal
                        && !address.IsIPv6UniqueLocal
                        && !IPAddress.IsLoopback(address))
                    {
                        return address;
                    }
                }
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            // Enumerating interfaces can fail while the stack is being reconfigured. Having no answer
            // is the same as having no address: the caller omits the parameter.
        }

        return null;
    }

    /// <summary>
    /// Normalizes an IPv4-mapped IPv6 endpoint (e.g. [::ffff:1.2.3.4]:6881) to its plain IPv4 form.
    /// Dual-stack sockets report IPv4 peers in the mapped form while trackers and PEX hand out
    /// plain IPv4 addresses; without normalization the two forms compare as different endpoints.
    /// </summary>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(endPoint))]
    public static IPEndPoint? NormalizeEndPoint(IPEndPoint? endPoint)
    {
        if (endPoint == null)
        {
            return null;
        }

        return endPoint.Address.IsIPv4MappedToIPv6
            ? new IPEndPoint(endPoint.Address.MapToIPv4(), endPoint.Port)
            : endPoint;
    }

    /// <summary>
    /// Converts an IPAddress to a UInt128 for unified comparison.
    /// Handles both IPv4 and IPv6.
    /// </summary>
    public static UInt128 IpToUInt128(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // IPv4: Convert to UInt128.
            // Note: We use the lower 32 bits for IPv4.
            // This works as long as we only compare IPs of the same family.
            var bytes = address.GetAddressBytes();
            uint ipv4 = (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
            return ipv4;
        }
        else
        {
            // IPv6: Full 128-bit value
            var bytes = address.GetAddressBytes();
            ulong high = 0, low = 0;
            for (int i = 0; i < 8; i++)
            {
                high = (high << 8) | bytes[i];
            }
            for (int i = 8; i < 16; i++)
            {
                low = (low << 8) | bytes[i];
            }
            return new UInt128(high, low);
        }
    }

    /// <summary>
    /// Parses a CIDR notation string (e.g. "192.168.1.0/24") into a range.
    /// </summary>
    public static bool TryParseCidr(string cidr, out UInt128 start, out UInt128 end)
    {
        start = 0;
        end = 0;

        int slashIndex = cidr.IndexOf('/');
        if (slashIndex <= 0)
        {
            return false;
        }

        string ipPart = cidr[..slashIndex].Trim();
        string prefixPart = cidr[(slashIndex + 1)..].Trim();

        if (!IPAddress.TryParse(ipPart, out var ip))
        {
            return false;
        }

        if (!int.TryParse(prefixPart, out int prefixLength))
        {
            return false;
        }

        int maxPrefix = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefixLength < 0 || prefixLength > maxPrefix)
        {
            return false;
        }

        var ipValue = IpToUInt128(ip);
        int hostBits = maxPrefix - prefixLength;

        if (hostBits == 0)
        {
            start = ipValue;
            end = ipValue;
        }
        else if (hostBits >= 128)
        {
            start = 0;
            end = UInt128.MaxValue;
        }
        else
        {
            UInt128 mask = (UInt128.One << hostBits) - 1;
            start = ipValue & ~mask;
            end = ipValue | mask;
        }

        return true;
    }
}
