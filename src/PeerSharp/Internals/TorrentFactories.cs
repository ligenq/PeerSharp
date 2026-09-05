using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Trackers;

namespace PeerSharp.Internals;

/// <summary>
/// Grouping of factory services for torrent components.
/// </summary>
internal sealed class TorrentFactories
{
    public TorrentFactories(IPeerCommunicationFactory peer, ITrackerFactory tracker, IHttpClientFactory httpClient)
        : this(peer, tracker, httpClient, NullLoggerFactory.Instance)
    {
    }

    public TorrentFactories(
        IPeerCommunicationFactory peer,
        ITrackerFactory tracker,
        IHttpClientFactory httpClient,
        ILoggerFactory loggerFactory)
    {
        Peer = peer;
        Tracker = tracker;
        HttpClient = httpClient;
        LoggerFactory = loggerFactory;
    }

    /// <summary>
    /// Shared HTTP client pool. Owned by the engine and disposed with it - components read it, they
    /// do not construct or dispose one.
    /// </summary>
    public IHttpClientFactory HttpClient { get; }

    public ILoggerFactory LoggerFactory { get; }
    public IPeerCommunicationFactory Peer { get; }
    public ITrackerFactory Tracker { get; }
}
