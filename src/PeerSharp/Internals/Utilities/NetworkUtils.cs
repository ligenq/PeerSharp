using System.Net;
using System.Net.Sockets;

namespace PeerSharp.Internals.Utilities;

/// <summary>
/// Stateless network utilities for IP address manipulation.
/// </summary>
internal static class NetworkUtils
{
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
