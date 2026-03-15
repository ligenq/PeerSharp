using System.Net;
using System.Net.Sockets;

namespace PeerSharp.Internals.Peers;

/// <summary>
/// BEP 40: Canonical Peer Priority
/// Calculates deterministic peer priority to reduce connection churn in the swarm.
/// Higher priority peers should be preferred for connections.
/// </summary>
internal static class PeerPriority
{
    // CRC32-C (Castagnoli) lookup table
    private static readonly uint[] Crc32CTable = GenerateCrc32CTable();

    /// <summary>
    /// Calculate BEP 40 peer priority between two IP addresses for a given torrent.
    /// Priority = CRC32-C(XOR(our_ip_masked, peer_ip_masked) || info_hash)
    /// </summary>
    /// <param name="ourIp">Our IP address</param>
    /// <param name="peerIp">Peer's IP address</param>
    /// <param name="infoHash">Torrent info hash (20 bytes)</param>
    /// <returns>Priority value (higher = more preferred)</returns>
    public static uint Calculate(IPAddress ourIp, IPAddress peerIp, byte[] infoHash)
    {
        // BEP 40: Apply masks - /16 for IPv4, /48 for IPv6
        byte[] ourMasked = MaskIp(ourIp);
        byte[] peerMasked = MaskIp(peerIp);

        // Ensure both are same length (for mixed IPv4/IPv6 scenarios)
        if (ourMasked.Length != peerMasked.Length)
        {
            // If IPs are different types, use a default priority
            // This handles IPv4 client connecting to IPv6 peer or vice versa
            return CalculateFallback(peerIp, infoHash);
        }

        // XOR the masked IPs
        byte[] xored = new byte[ourMasked.Length];
        for (int i = 0; i < ourMasked.Length; i++)
        {
            xored[i] = (byte)(ourMasked[i] ^ peerMasked[i]);
        }

        // Concatenate XOR result with info hash
        byte[] data = new byte[xored.Length + infoHash.Length];
        xored.CopyTo(data, 0);
        infoHash.CopyTo(data, xored.Length);

        // Calculate CRC32-C (Castagnoli)
        return ComputeCrc32C(data);
    }

    /// <summary>
    /// Calculate priority when we don't know our own IP (use peer IP + info hash only).
    /// </summary>
    public static uint Calculate(IPAddress peerIp, byte[] infoHash)
    {
        return CalculateFallback(peerIp, infoHash);
    }

    /// <summary>
    /// Compare two peers by priority. Returns positive if a has higher priority than b.
    /// </summary>
    public static int Compare(uint priorityA, uint priorityB)
    {
        // Higher priority value = more preferred
        return priorityA.CompareTo(priorityB);
    }

    /// <summary>
    /// Fallback priority calculation using just peer IP and info hash.
    /// Used when our IP is unknown or when IPs are mixed IPv4/IPv6.
    /// </summary>
    private static uint CalculateFallback(IPAddress peerIp, byte[] infoHash)
    {
        byte[] peerBytes = peerIp.GetAddressBytes();
        byte[] data = new byte[peerBytes.Length + infoHash.Length];
        peerBytes.CopyTo(data, 0);
        infoHash.CopyTo(data, peerBytes.Length);
        return ComputeCrc32C(data);
    }

    /// <summary>
    /// Compute CRC32-C (Castagnoli) checksum
    /// </summary>
    private static uint ComputeCrc32C(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc = Crc32CTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>
    /// Generate CRC32-C (Castagnoli) lookup table using polynomial 0x1EDC6F41
    /// </summary>
    private static uint[] GenerateCrc32CTable()
    {
        const uint polynomial = 0x82F63B78; // Reversed Castagnoli polynomial

        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 1) != 0)
                {
                    crc = (crc >> 1) ^ polynomial;
                }
                else
                {
                    crc >>= 1;
                }
            }
            table[i] = crc;
        }
        return table;
    }

    /// <summary>
    /// Apply BEP 40 IP mask: /16 for IPv4, /48 for IPv6
    /// </summary>
    private static byte[] MaskIp(IPAddress ip)
    {
        byte[] bytes = ip.GetAddressBytes();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            // IPv4: /16 mask - keep first 2 bytes, zero the rest
            return new byte[] { bytes[0], bytes[1], 0, 0 };
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // IPv6: /48 mask - keep first 6 bytes, zero the rest
            byte[] masked = new byte[16];
            for (int i = 0; i < 6 && i < bytes.Length; i++)
            {
                masked[i] = bytes[i];
            }
            return masked;
        }

        // Unknown address family - return as-is
        return bytes;
    }
}
