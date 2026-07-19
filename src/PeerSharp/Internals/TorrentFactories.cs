using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Trackers;

namespace PeerSharp.Internals;

/// <summary>
/// Grouping of factory services for torrent components.
/// </summary>
internal sealed class TorrentFactories
{
    public TorrentFactories(IPeerCommunicationFactory peer, ITrackerFactory tracker)
        : this(peer, tracker, NullLoggerFactory.Instance)
    {
    }

    public TorrentFactories(IPeerCommunicationFactory peer, ITrackerFactory tracker, ILoggerFactory loggerFactory)
    {
        Peer = peer;
        Tracker = tracker;
        LoggerFactory = loggerFactory;
    }

    public ILoggerFactory LoggerFactory { get; }
    public IPeerCommunicationFactory Peer { get; }
    public ITrackerFactory Tracker { get; }
}
