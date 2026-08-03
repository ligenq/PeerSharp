using System.Net;

namespace PeerSharp.Internals.Extensions;

/// <summary>
/// The little a <see cref="PexBroadcaster"/> needs to know about a connection. Narrow on purpose: the
/// rules it applies - never for a private torrent, never an unconnectable address, never the same peer
/// twice - are worth testing without standing up a real swarm to do it.
/// </summary>
internal interface IPexPeer
{
    string Name { get; }

    /// <summary>Where this peer accepts connections, or null if it has not said. Never the connection's own port.</summary>
    IPEndPoint? RemoteListenEndPoint { get; }

    bool SupportsPex { get; }

    /// <summary>The ut_pex flag byte describing this peer to others.</summary>
    byte PexFlags { get; }

    void SendPex(List<IPEndPoint> added, List<byte> addedFlags, List<IPEndPoint> dropped);
}
