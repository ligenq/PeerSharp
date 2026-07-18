using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace PeerSharp.Internals.Dht;

internal static class DhtSecurity
{
    // CRC32-C (Castagnoli) lookup table - same polynomial as BEP 40
    private static readonly uint[] Crc32CTable = GenerateCrc32CTable();

    // IPv4 mask: 0x030f3fff (network byte order)
    private static readonly byte[] IPv4Mask = { 0x03, 0x0f, 0x3f, 0xff };

    // IPv6 mask: 0x0103070f1f3f7fff (first 8 bytes)
    private static readonly byte[] IPv6Mask = { 0x01, 0x03, 0x07, 0x0f, 0x1f, 0x3f, 0x7f, 0xff };

    /// <summary>
    /// Generate a random node ID (non-BEP 42 compliant).
    /// Used when external IP is unknown.
    /// </summary>
    public static byte[] GenerateRandomNodeId()
    {
        byte[] nodeId = new byte[20];
        RandomNumberGenerator.Fill(nodeId);
        return nodeId;
    }

    /// <summary>
    /// Generate a BEP 42 compliant node ID based on the external IP address.
    /// The first 21 bits are derived from CRC32-C(masked_ip || r).
    /// </summary>
    /// <param name="externalIp">The node's external IP address</param>
    /// <returns>A 20-byte node ID</returns>
    public static byte[] GenerateSecureNodeId(IPAddress externalIp)
    {
        byte[] nodeId = new byte[20];

        // Fill with random bytes first
        RandomNumberGenerator.Fill(nodeId);

        // Determine r value from last byte
        // For IPv4: r is in bits 0-2 (0-7)
        // For IPv6: r is in bits 0-6 (0-127)
        byte r;
        if (externalIp.AddressFamily == AddressFamily.InterNetwork)
        {
            r = (byte)(nodeId[19] & 0x07);
        }
        else
        {
            r = (byte)(nodeId[19] & 0x7f);
        }

        // Compute CRC32-C of masked IP concatenated with r
        uint crc = ComputeNodeIdCrc(externalIp, r);

        // Set first 21 bits of node ID from CRC result
        // CRC bits 31-24 -> nodeId[0]
        // CRC bits 23-16 -> nodeId[1]
        // CRC bits 15-11 -> nodeId[2] bits 7-3 (preserve bits 2-0 from random)
        nodeId[0] = (byte)((crc >> 24) & 0xff);
        nodeId[1] = (byte)((crc >> 16) & 0xff);
        nodeId[2] = (byte)(((crc >> 8) & 0xf8) | (uint)(nodeId[2] & 0x07));

        return nodeId;
    }

    /// <summary>
    /// Check if an IP address should be validated for BEP 42.
    /// Local/private addresses are exempt from validation.
    /// </summary>
    public static bool ShouldValidate(IPAddress ip)
    {
        if (ip == null)
        {
            return false;
        }

        // Don't validate local/private addresses
        if (IPAddress.IsLoopback(ip))
        {
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> bytes = stackalloc byte[4];
            if (!ip.TryWriteBytes(bytes, out _))
            {
                return false;
            }

            // 10.x.x.x
            if (bytes[0] == 10)
            {
                return false;
            }
            // 172.16.x.x - 172.31.x.x
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            {
                return false;
            }
            // 192.168.x.x
            if (bytes[0] == 192 && bytes[1] == 168)
            {
                return false;
            }
            // 169.254.x.x (link-local)
            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return false;
            }
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Link-local (fe80::/10)
            if (ip.IsIPv6LinkLocal)
            {
                return false;
            }
            // Site-local (fec0::/10) - deprecated but still check
            if (ip.IsIPv6SiteLocal)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Validate that a node ID matches the expected value for the given IP address.
    /// Returns true if the first 21 bits match the expected CRC32-C value.
    /// </summary>
    /// <param name="nodeId">The node ID to validate (20 bytes)</param>
    /// <param name="ip">The node's IP address</param>
    /// <returns>True if the node ID is valid for the IP</returns>
    public static bool ValidateNodeId(ReadOnlySpan<byte> nodeId, IPAddress ip)
    {
        if (nodeId.Length != 20)
        {
            return false;
        }

        // Extract r from byte 19
        byte r;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            r = (byte)(nodeId[19] & 0x07);
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            r = (byte)(nodeId[19] & 0x7f);
        }
        else
        {
            // Unknown address family - can't validate
            return true;
        }

        // Compute expected CRC
        uint expectedCrc = ComputeNodeIdCrc(ip, r);

        // Extract first 21 bits from expected CRC
        byte expectedByte0 = (byte)((expectedCrc >> 24) & 0xff);
        byte expectedByte1 = (byte)((expectedCrc >> 16) & 0xff);
        byte expectedByte2Masked = (byte)((expectedCrc >> 8) & 0xf8);

        // Compare first 21 bits
        if (nodeId[0] != expectedByte0)
        {
            return false;
        }
        if (nodeId[1] != expectedByte1)
        {
            return false;
        }
        if ((nodeId[2] & 0xf8) != expectedByte2Masked)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Compute CRC32-C (Castagnoli) checksum
    /// </summary>
    private static uint ComputeCrc32C(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc = Crc32CTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>
    /// Compute CRC32-C for node ID generation/validation.
    /// Input is masked IP bytes concatenated with random value r.
    /// </summary>
    private static uint ComputeNodeIdCrc(IPAddress ip, byte r)
    {
        Span<byte> ipBytes = stackalloc byte[16];
        if (!ip.TryWriteBytes(ipBytes, out int ipWritten))
        {
            return 0;
        }

        ReadOnlySpan<byte> mask;
        int maskLen;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            mask = IPv4Mask;
            maskLen = 4;
        }
        else
        {
            mask = IPv6Mask;
            maskLen = 8;
        }

        // BEP 42: mask the first maskLen octets of the IP, fold the low 3 bits of r
        // into the top of the first masked octet, then CRC32-C exactly those octets:
        //   for i in 0..num: ip[i] &= mask[i]
        //   ip[0] |= (r & 0x7) << 5
        //   crc32c(ip[0..num])
        // The previous implementation appended r as a trailing byte and hashed one
        // extra octet, which was self-consistent but non-conformant: our secure node
        // IDs read as insecure to other clients and vice-versa.
        Span<byte> data = stackalloc byte[8];
        for (int i = 0; i < maskLen; i++)
        {
            byte ipByte = i < ipWritten ? ipBytes[i] : (byte)0;
            data[i] = (byte)(ipByte & mask[i]);
        }
        data[0] |= (byte)((r & 0x7) << 5);

        return ComputeCrc32C(data[..maskLen]);
    }

    /// <summary>
    /// Generate CRC32-C (Castagnoli) lookup table using polynomial 0x82F63B78
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
}
