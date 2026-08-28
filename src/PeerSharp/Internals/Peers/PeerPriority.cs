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

        byte[] peer = peerEndPoint.Address.GetAddressBytes();
        byte[] ours = NormaliseToPeerFamily(ourEndPoint.Address, peerEndPoint.Address).GetAddressBytes();

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
    /// Compare two peers by priority. Returns positive if a has higher priority than b.
    /// </summary>
    public static int Compare(uint priorityA, uint priorityB)
    {
        // Higher priority value = more preferred
        return priorityA.CompareTo(priorityB);
    }

    /// <summary>
    /// Our address as it must be to pair with this peer's: the same family, and the unspecified
    /// address when we do not know our own.
    /// </summary>
    /// <remarks>
    /// A client that has not yet learned its public address still has to rank peers, so the
    /// unspecified address stands in and the calculation stays the specified one - the same thing
    /// libtorrent does, whose external address table starts out holding exactly that. The value is
    /// not canonical until the real address is known, because the peer at the other end is using our
    /// actual address, but it is a well-formed priority in the meantime and becomes canonical by
    /// itself once the address arrives. Nothing here is cached, so that transition needs no help.
    /// </remarks>
    private static IPAddress NormaliseToPeerFamily(IPAddress ours, IPAddress peer)
    {
        if (ours.AddressFamily == peer.AddressFamily)
        {
            return ours;
        }

        return peer.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
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
    /// The BEP 40 mask for a pair of addresses, followed by 0x55 bytes for the remaining suffix.
    /// </summary>
    /// <remarks>
    /// IPv4 keeps at least two whole bytes and includes the first byte that differs. IPv6 keeps at
    /// least six whole bytes and, as libtorrent's BEP 40 implementation does, one byte beyond the
    /// first differing byte. The latter then advances through /48, /56, /64, /72 and so on rather
    /// than stopping at /64. Keeping bytes beyond the shared prefix is what stops every peer inside
    /// our own network masking down to the same value and so sorting identically.
    /// </remarks>
    private static byte[] MaskFor(byte[] ours, byte[] peer)
    {
        int shared = 0;
        while (shared < ours.Length && ours[shared] == peer[shared])
        {
            shared++;
        }

        bool ipv6 = ours.Length == 16;
        int wholeBytes = ipv6
            ? Math.Clamp(shared + 2, 6, 16)
            : Math.Clamp(shared + 1, 2, 4);

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
