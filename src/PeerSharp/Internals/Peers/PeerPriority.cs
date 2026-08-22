using System.Net;
using System.Net.Sockets;

namespace PeerSharp.Internals.Peers;

/// <summary>
/// BEP 40 canonical peer priority: a value both ends of a connection compute for themselves and
/// agree on.
/// </summary>
/// <remarks>
/// <para>
/// Agreement is the entire point. When two peers connect to each other at the same moment, or when
/// one has to drop a connection to make room, both sides deciding the same way is what stops them
/// making opposite choices and churning. A priority only this client agrees with keeps the
/// determinism and loses the interoperability, which is most of the value.
/// </para>
/// <para>
/// The formula is <c>crc32c(sort(masked_client_ip, masked_peer_ip))</c>. The closer the two
/// addresses are, the more of them survives masking, so peers on the same network are still ordered
/// against each other rather than collapsing to one value. Ports stand in when the addresses are
/// identical, and the info hash takes no part - it is the same for everyone in the swarm and so
/// cannot order anything within it.
/// </para>
/// </remarks>
internal static class PeerPriority
{
    // CRC32-C (Castagnoli) lookup table
    private static readonly uint[] Crc32CTable = GenerateCrc32CTable();

    /// <summary>
    /// The BEP 40 priority for a connection between us and a peer.
    /// </summary>
    /// <remarks>
    /// Both endpoints are needed, and each end passes them the other way round; sorting the masked
    /// values is what makes the two calls agree.
    /// </remarks>
    public static uint Calculate(IPEndPoint ourEndPoint, IPEndPoint peerEndPoint)
    {
        ArgumentNullException.ThrowIfNull(ourEndPoint);
        ArgumentNullException.ThrowIfNull(peerEndPoint);

        byte[] ours = ourEndPoint.Address.GetAddressBytes();
        byte[] peer = peerEndPoint.Address.GetAddressBytes();

        // BEP 40 says nothing about one end being v4 and the other v6, which cannot happen on a
        // connection that exists. Fall back rather than invent a rule other clients will not share.
        if (ours.Length != peer.Length)
        {
            return CalculateWithoutLocalAddress(peerEndPoint.Address, []);
        }

        if (ours.AsSpan().SequenceEqual(peer))
        {
            // "If the IP addresses are the same, the port numbers should be used instead."
            return ComputeCrc32C(Concatenate(
                [(byte)(ourEndPoint.Port >> 8), (byte)ourEndPoint.Port],
                [(byte)(peerEndPoint.Port >> 8), (byte)peerEndPoint.Port]));
        }

        byte[] mask = MaskFor(ours, peer);
        return ComputeCrc32C(Concatenate(Apply(ours, mask), Apply(peer, mask)));
    }

    /// <summary>
    /// A stable per-peer ordering for when this client does not know its own public address.
    /// </summary>
    /// <remarks>
    /// Not BEP 40 and not interoperable: without our own address the canonical value cannot be
    /// computed at all. It keeps the local decisions deterministic - the same peer on the same
    /// torrent always sorts the same way - which is enough to stop this client thrashing on its own,
    /// and no help whatsoever in agreeing with the peer on the other end.
    /// </remarks>
    public static uint CalculateWithoutLocalAddress(IPAddress peerIp, byte[] infoHash)
    {
        ArgumentNullException.ThrowIfNull(peerIp);
        ArgumentNullException.ThrowIfNull(infoHash);

        return ComputeCrc32C(Concatenate(peerIp.GetAddressBytes(), infoHash));
    }

    /// <summary>
    /// Compare two peers by priority. Returns positive if a has higher priority than b.
    /// </summary>
    public static int Compare(uint priorityA, uint priorityB)
    {
        // Higher priority value = more preferred
        return priorityA.CompareTo(priorityB);
    }

    private static byte[] Apply(byte[] address, byte[] mask)
    {
        byte[] masked = new byte[address.Length];
        for (int i = 0; i < address.Length; i++)
        {
            masked[i] = (byte)(address[i] & mask[i]);
        }

        return masked;
    }

    /// <summary>
    /// Concatenates two byte strings in ascending order, so both ends of a connection produce the
    /// same input whichever way round they hold the pair.
    /// </summary>
    private static byte[] Concatenate(byte[] left, byte[] right)
    {
        bool leftFirst = left.AsSpan().SequenceCompareTo(right) <= 0;
        byte[] first = leftFirst ? left : right;
        byte[] second = leftFirst ? right : left;

        byte[] combined = new byte[first.Length + second.Length];
        first.CopyTo(combined, 0);
        second.CopyTo(combined, first.Length);
        return combined;
    }

    /// <summary>
    /// The BEP 40 mask for a pair of addresses: whole bytes for the prefix they share plus one
    /// more, then 0x55 for the rest.
    /// </summary>
    /// <remarks>
    /// Stated in the BEP as a table - FF.FF.55.55, FF.FF.FF.55 for a shared /16 and FF.FF.FF.FF for
    /// a shared /24, with the IPv6 equivalents anchored at /48 and /56 - which is this one rule
    /// written out. Keeping a byte beyond the shared prefix is what stops every peer inside our own
    /// network masking down to the same value and so sorting identically.
    /// </remarks>
    private static byte[] MaskFor(byte[] ours, byte[] peer)
    {
        bool ipv6 = ours.Length == 16;
        int minimumWholeBytes = ipv6 ? 6 : 2;
        int maximumWholeBytes = ipv6 ? 8 : 4;

        int shared = 0;
        while (shared < ours.Length && ours[shared] == peer[shared])
        {
            shared++;
        }

        int wholeBytes = Math.Clamp(shared + 1, minimumWholeBytes, maximumWholeBytes);

        byte[] mask = new byte[ours.Length];
        for (int i = 0; i < mask.Length; i++)
        {
            mask[i] = i < wholeBytes ? (byte)0xFF : (byte)0x55;
        }

        return mask;
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
}
