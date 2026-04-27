using PeerSharp.WebTorrent.Network;

namespace PeerSharp.WebTorrent.Trackers;

internal sealed class TrackerRuntime
{
    public TrackerRuntime(string url)
    {
        Url = url;
    }

    public string Url { get; }
    public object SyncRoot { get; } = new();
    public IWebSocketConnection? Socket { get; set; }
    public DateTimeOffset NextAnnounce { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset NextReconnectAt { get; set; } = DateTimeOffset.MinValue;
    public TimeSpan ReannounceInterval { get; set; } = TimeSpan.FromSeconds(120);
    public Task? ReceiveTask { get; set; }
    public bool IsConnected { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string? LastError { get; set; }
    public bool CompletedSent { get; set; }
    public bool ReconnectInProgress { get; set; }

    // Incremented every time ConnectTrackerAsync binds a fresh socket. Receive loops
    // capture the generation at entry and refuse to schedule reconnects once the runtime
    // has moved on to a newer socket — otherwise a stale loop could clobber fresh state.
    public int Generation { get; set; }
}
