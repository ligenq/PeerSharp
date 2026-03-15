using PeerSharp.Internals.Bandwidth;
using PeerSharp.Internals.Framework;
using PeerSharp.PieceWriter;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Trackers;

namespace PeerSharp.Internals;

/// <summary>
/// Container for services injected into a Torrent.
/// </summary>
internal sealed class TorrentServices
{
    public TorrentServices(
        IBandwidthManager bandwidth,
        IAlertsManager alerts,
        IFileHandleCache fileHandleCache,
        IConnectionGovernor connectionGovernor,
        IGeoIpService geoIp,
        TorrentFactories factories,
        TimeProvider timeProvider)
    {
        Bandwidth = bandwidth;
        Alerts = alerts;
        FileHandleCache = fileHandleCache;
        ConnectionGovernor = connectionGovernor;
        GeoIp = geoIp;
        Factories = factories;
        TimeProvider = timeProvider;
    }

    public IAlertsManager Alerts { get; }
    public IBandwidthManager Bandwidth { get; }
    public IConnectionGovernor ConnectionGovernor { get; }
    public TorrentFactories Factories { get; }
    public IFileHandleCache FileHandleCache { get; }
    public IGeoIpService GeoIp { get; }
    public IPeerCommunicationFactory PeerFactory => Factories.Peer;
    public TimeProvider TimeProvider { get; }
    public ITrackerFactory TrackerFactory => Factories.Tracker;
}
