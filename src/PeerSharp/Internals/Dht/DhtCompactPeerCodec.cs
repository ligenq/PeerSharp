using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace PeerSharp.Internals.Dht;

internal static class DhtCompactPeerCodec
{
    private const int Ipv4PeerLength = 6;
    private const int Ipv6PeerLength = 18;

    public static List<byte[]> Encode(IEnumerable<IPEndPoint> peers, bool ipv6 = false, int maxPeers = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(peers);

        var result = new List<byte[]>();
        Span<byte> port = stackalloc byte[2];

        foreach (var peer in peers)
        {
            bool isV6 = peer.AddressFamily == AddressFamily.InterNetworkV6;
            bool isV4 = peer.AddressFamily == AddressFamily.InterNetwork;
            if ((ipv6 && !isV6) || (!ipv6 && !isV4))
            {
                continue;
            }

            byte[] addressBytes = peer.Address.GetAddressBytes();
            int addressLength = ipv6 ? 16 : 4;
            if (addressBytes.Length != addressLength)
            {
                continue;
            }

            byte[] compact = new byte[addressLength + 2];
            addressBytes.CopyTo(compact, 0);
            BinaryPrimitives.WriteUInt16BigEndian(port, (ushort)peer.Port);
            port.CopyTo(compact.AsSpan(addressLength));
            result.Add(compact);

            if (result.Count >= maxPeers)
            {
                break;
            }
        }

        return result;
    }

    public static List<IPEndPoint> Parse(IEnumerable<ReadOnlyMemory<byte>> values, bool ipv6 = false)
    {
        ArgumentNullException.ThrowIfNull(values);

        var result = new List<IPEndPoint>();
        int peerLength = ipv6 ? Ipv6PeerLength : Ipv4PeerLength;
        int addressLength = ipv6 ? 16 : 4;

        foreach (var value in values)
        {
            if (value.Length != peerLength)
            {
                continue;
            }

            var ip = new IPAddress(value[..addressLength].Span);
            int port = BinaryPrimitives.ReadUInt16BigEndian(value.Span[addressLength..]);
            result.Add(new IPEndPoint(ip, port));
        }

        return result;
    }
}
