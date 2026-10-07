using PeerSharp.Interfaces;
using PeerSharp.Internals.Network;

namespace PeerSharp.Tests.Core;

public class LifecycleReviewTests
{
    private sealed class RecordingLsd : ILsdManager
    {
        public int Announces { get; private set; }
        public Task AnnounceAsync(InfoHash infoHash, CancellationToken token = default)
        {
            Announces++;
            return Task.CompletedTask;
        }
        public void Start() { }
        public void Stop() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact(Timeout = 30000)]
    public async Task ThrowingStateCallback_DoesNotPreventStopOrDisposal()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        await using var torrent = TorrentTestUtility.CreateMinimal(downloadPath: path);
        torrent.Events = new TorrentEventsBuilder().OnStateChanged((_, _) => throw new InvalidOperationException("subscriber")).Build();
        await torrent.StartAsync();
        await torrent.StopAsync();
        Assert.False(torrent.Started);
        await torrent.StartAsync();
        await torrent.DisposeAsync();
        Assert.False(torrent.Started);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => torrent.StartAsync());
    }

    [Fact(Timeout = 30000)]
    public async Task PrivateTorrent_DoesNotAnnounceThroughLocalDiscovery()
    {
        await using var torrent = TorrentTestUtility.CreateMinimal(downloadPath: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        torrent.InfoFile.Info.IsPrivate = true;
        var lsd = new RecordingLsd();
        torrent.Network.Lsd = lsd;
        await torrent.StartAsync();
        Assert.Equal(0, lsd.Announces);
    }

    [Fact(Timeout = 30000)]
    public async Task MetadataWaiter_Dispose_CancelsOutstandingWait()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        Assert.False(torrent.HasMetadata);
        var wait = torrent.WaitForMetadataAsync();
        await torrent.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }
}
