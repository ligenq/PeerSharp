using PeerSharp.WebTorrent.Trackers;
using RtcForge;

namespace PeerSharp.WebTorrent.Peers;

internal sealed class PendingPeer
{
    public PendingPeer(string offerId, IWebRtcConnection connection, IWebRtcDataChannel? channel, bool initiator, TrackerRuntime runtime, DateTimeOffset expiresAt)
    {
        OfferId = offerId;
        Connection = connection;
        Channel = channel;
        Initiator = initiator;
        Runtime = runtime;
        ExpiresAt = expiresAt;
    }

    private readonly CancellationTokenSource _lifetimeCts = new();
    private int _attached;

    public List<WebRtcIceCandidateDescription> BufferedLocalCandidates { get; } = [];
    public List<string> BufferedRemoteCandidates { get; } = [];
    public IWebRtcDataChannel? Channel { get; }
    public IWebRtcConnection Connection { get; }
    public bool Initiator { get; }
    public bool IsAttached => Volatile.Read(ref _attached) == 1;
    public DateTimeOffset ExpiresAt { get; set; }
    public string OfferId { get; }
    public bool LocalCandidateSignalingReady { get; set; }
    public CancellationToken LifetimeToken => _lifetimeCts.Token;
    public bool RemoteDescriptionSet { get; set; }
    public string? RemotePeerId { get; set; }
    public TrackerRuntime Runtime { get; }
    public object SyncRoot { get; } = new();

    public bool TryMarkAttached() => Interlocked.Exchange(ref _attached, 1) == 0;

    public void CancelLifetime()
    {
        try
        {
            _lifetimeCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed; nothing to cancel.
        }
    }

    public void DisposeLifetime() => _lifetimeCts.Dispose();
}
