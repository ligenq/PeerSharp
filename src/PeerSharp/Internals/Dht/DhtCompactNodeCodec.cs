using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace PeerSharp.Internals.Dht;

internal static class DhtCompactNodeCodec
{
    private const int NodeIdLength = 20;
    private const int Ipv4NodeLength = 26;
    private const int Ipv6NodeLength = 38;

    public static byte[] Encode(IReadOnlyList<NodeInfo> nodes, bool ipv6 = false)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        int expectedAddressLength = ipv6 ? 16 : 4;
        int recordLength = NodeIdLength + expectedAddressLength + 2;
        Span<byte> record = stackalloc byte[Ipv6NodeLength];

        using var ms = new MemoryStream();
        foreach (var node in nodes)
        {
            if (node.Id.Length < NodeIdLength)
            {
                continue;
            }

            bool isV6 = node.EndPoint.AddressFamily == AddressFamily.InterNetworkV6;
            bool isV4 = node.EndPoint.AddressFamily == AddressFamily.InterNetwork;
            if ((ipv6 && !isV6) || (!ipv6 && !isV4))
            {
                continue;
            }

            // Stage the full record into a stack buffer first so a write failure
            // (e.g. address that doesn't fit the expected length) can't leave a
            // half-written record on the output stream.
            if (!node.EndPoint.Address.TryWriteBytes(record.Slice(NodeIdLength, expectedAddressLength), out int written)
                || written != expectedAddressLength)
            {
                continue;
            }

            node.Id.AsSpan(0, NodeIdLength).CopyTo(record);
            BinaryPrimitives.WriteUInt16BigEndian(record[(NodeIdLength + expectedAddressLength)..], (ushort)node.EndPoint.Port);
            ms.Write(record[..recordLength]);
        }

        return ms.ToArray();
    }

    public static List<NodeInfo> Parse(ReadOnlySpan<byte> data, bool ipv6 = false)
    {
        var list = new List<NodeInfo>();
        int nodeSize = ipv6 ? Ipv6NodeLength : Ipv4NodeLength;
        int ipSize = ipv6 ? 16 : 4;

        for (int i = 0; i <= data.Length - nodeSize; i += nodeSize)
        {
            ReadOnlySpan<byte> id = data.Slice(i, NodeIdLength);
            var ip = new IPAddress(data.Slice(i + NodeIdLength, ipSize));
            int port = BinaryPrimitives.ReadUInt16BigEndian(data[(i + NodeIdLength + ipSize)..]);
            list.Add(new NodeInfo(id, new IPEndPoint(ip, port)));
        }

        return list;
    }
}
