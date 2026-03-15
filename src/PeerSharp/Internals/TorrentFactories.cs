using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Trackers;

namespace PeerSharp.Internals;

/// <summary>
/// Grouping of factory services for torrent components.
/// </summary>
internal sealed class TorrentFactories
{
    public TorrentFactories(IPeerCommunicationFactory peer, ITrackerFactory tracker)
    {
        Peer = peer;
        Tracker = tracker;
    }

    public IPeerCommunicationFactory Peer { get; }
    public ITrackerFactory Tracker { get; }
}
