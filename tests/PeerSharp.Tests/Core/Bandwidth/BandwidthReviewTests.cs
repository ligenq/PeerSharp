using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals.Bandwidth;
using PeerSharp.PieceWriter;

namespace PeerSharp.Tests.Core.Bandwidth;

public sealed class BandwidthReviewTests
{
    private sealed class User : IBandwidthUser
    {
        public string Name => "review";
        public void AssignBandwidth(int amount) { }
    }

    [Fact]
    public async Task ConcurrentReservationsCannotSpendTheSameQuota()
    {
        await using var manager = new BandwidthManager(10, new FakeTimeProvider());
        manager.SetGlobalLimits(1000, 0);
        var channel = manager.GetChannel(BandwidthManager.GlobalDownload);
        channel.UpdateQuota(1000);
        using var cts = new CancellationTokenSource();
        var requests = new Task<int>[1000];
        Parallel.For(0, requests.Length, i => requests[i] = manager.RequestBandwidthAsync(new User(), 16, 0, [BandwidthManager.GlobalDownload], cts.Token));
        Assert.Equal(62, requests.Count(task => task.IsCompletedSuccessfully));
        Assert.Equal(8, channel.AvailableQuota);
        await cts.CancelAsync();
        foreach (var request in requests)
        {
            if (request.IsCanceled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            else Assert.Equal(16, await request);
        }
    }

    [Fact]
    public async Task UsersWithBacklogsCannotStarveOtherUsers()
    {
        var clock = new FakeTimeProvider();
        await using var manager = new BandwidthManager(10, clock);
        manager.SetGlobalLimits(1, 0);
        var first = new User();
        var a = manager.RequestBandwidthAsync(first, 1, 0, [BandwidthManager.GlobalDownload]);
        var a2 = manager.RequestBandwidthAsync(first, 1, 0, [BandwidthManager.GlobalDownload]);
        var b = manager.RequestBandwidthAsync(new User(), 1, 0, [BandwidthManager.GlobalDownload]);
        clock.Advance(TimeSpan.FromSeconds(1));
        manager.Update(null);
        Assert.Equal(1, await a);
        Assert.False(a2.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        manager.Update(null);
        Assert.Equal(1, await b);
        Assert.False(a2.IsCompleted);
    }

    [Fact]
    public async Task DisposalCompletesQueuedRequestsAndRejectsNewRequests()
    {
        var manager = new BandwidthManager(10, new FakeTimeProvider());
        manager.SetGlobalLimits(1, 0);
        var request = manager.RequestBandwidthAsync(new User(), 100, 0, [BandwidthManager.GlobalDownload]);
        await manager.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => request);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.RequestBandwidthAsync(new User(), 1, 0, [BandwidthManager.GlobalDownload]));
        Assert.Throws<ObjectDisposedException>(manager.Start);
    }

    [Fact]
    public void DecreasingOrReenablingALimitCannotRetainTheOldBurst()
    {
        var channel = new BandwidthChannel(new FakeTimeProvider());
        channel.SetLimit(1000);
        channel.UpdateQuota(3000);
        channel.SetLimit(1);
        Assert.Equal(3, channel.AvailableQuota);
        channel.SetLimit(0);
        channel.SetLimit(1);
        Assert.Equal(0, channel.AvailableQuota);
        channel.UpdateQuota(0);
        channel.UpdateQuota(-1);
        Assert.Equal(0, channel.AvailableQuota);
    }

    [Fact]
    public async Task AnUnrelatedBlockedChannelDoesNotThrottleUnlimitedTraffic()
    {
        await using var manager = new BandwidthManager(10, new FakeTimeProvider());
        manager.SetGlobalDiskLimits(1, 0);
        var blocked = manager.RequestBandwidthAsync(new User(), 100, 0, [BandwidthManager.GlobalDiskRead]);
        Assert.Equal(100, await manager.RequestBandwidthAsync(new User(), 100, 0, [BandwidthManager.GlobalUpload]));
        Assert.False(blocked.IsCompleted);
        await manager.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => blocked);
    }

    [Fact]
    public async Task DuplicateChannelNamesAreChargedAndRefundedOnce()
    {
        await using var manager = new BandwidthManager(10, new FakeTimeProvider());
        var channel = manager.GetChannel(BandwidthManager.GlobalDownload);
        channel.SetLimit(100);
        channel.UpdateQuota(1000);
        string[] names = [BandwidthManager.GlobalDownload, BandwidthManager.GlobalDownload];
        Assert.Equal(25, await manager.RequestBandwidthAsync(new User(), 25, 0, names));
        Assert.Equal(75, channel.AvailableQuota);
        manager.ReturnBandwidth(25, names);
        Assert.Equal(100, channel.AvailableQuota);
    }

    [Fact]
    public async Task V2TorrentsHaveIndependentNetworkAndDiskLimits()
    {
        var a = new PeerSharp.Internals.TorrentFileMetadata();
        var b = new PeerSharp.Internals.TorrentFileMetadata();
        a.Info.Version = b.Info.Version = PeerSharp.Internals.TorrentVersion.V2;
        a.Info.HashV2 = new InfoHash(System.Security.Cryptography.SHA256.HashData(new byte[] { 1 }));
        b.Info.HashV2 = new InfoHash(System.Security.Cryptography.SHA256.HashData(new byte[] { 2 }));
        await using var first = TorrentTestUtility.CreateMinimal(a);
        await using var second = TorrentTestUtility.CreateMinimal(b);
        await using var manager = new BandwidthManager(10, new FakeTimeProvider());
        manager.SetTorrentLimits(first, 100, 200);
        manager.SetTorrentLimits(second, 300, 400);
        manager.SetTorrentDiskLimits(first, 10, 20);
        manager.SetTorrentDiskLimits(second, 30, 40);
        Assert.Equal((100L, 200L), manager.GetTorrentLimits(first));
        Assert.Equal((300L, 400L), manager.GetTorrentLimits(second));
        Assert.Equal((10L, 20L), manager.GetTorrentDiskLimits(first));
        Assert.Equal((30L, 40L), manager.GetTorrentDiskLimits(second));
        Assert.Equal(first.HashV2.TruncateToV1(), ((PeerSharp.Interfaces.IPeerTransportHost)first).Hash);
        manager.RemoveTorrentChannels(first);
        Assert.Equal((300L, 400L), manager.GetTorrentLimits(second));
    }

    [Fact]
    public async Task RemovingATorrentCompletesItsQueuedDiskRequest()
    {
        await using var manager = new BandwidthManager(10, new FakeTimeProvider());
        await using var torrent = TorrentTestUtility.CreateMinimal();
        manager.SetTorrentDiskLimits(torrent, 1, 1);
        var limiter = new DiskBandwidthLimiter(manager, torrent.Hash.ToHexStringUpper());
        var request = limiter.RequestReadAsync(100, CancellationToken.None);
        manager.RemoveTorrentChannels(torrent);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => request);
    }

    [Fact]
    public async Task BlockedDiskReadsDoNotBlockWrites()
    {
        var clock = new FakeTimeProvider();
        await using var manager = new BandwidthManager(10, clock);
        manager.SetGlobalDiskLimits(1, 1000);
        var limiter = new DiskBandwidthLimiter(manager, "review");
        using var cts = new CancellationTokenSource();
        var read = limiter.RequestReadAsync(100, cts.Token);
        var write = limiter.RequestWriteAsync(100, cts.Token);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        manager.Update(null);
        Assert.Equal(100, await write);
        Assert.False(read.IsCompleted);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }
}
