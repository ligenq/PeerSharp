using PeerSharp.Internals.Bandwidth;
using Microsoft.Extensions.Time.Testing;

namespace PeerSharp.Tests.Core.Bandwidth;

public class BandwidthManagerTests
{
    private class MockBandwidthUser : IBandwidthUser
    {
        public string Name { get; set; } = "MockUser";
        public void AssignBandwidth(int amount) { }
    }

    private readonly FakeTimeProvider _timeProvider = new();

    [Fact]
    public void SetGlobalLimits_UpdatesChannels()
    {
        var manager = new BandwidthManager(10, _timeProvider);
        manager.SetGlobalLimits(1000, 2000);

        Assert.Equal(1000, manager.GetChannel(BandwidthManager.GlobalDownload).GetLimit());
        Assert.Equal(2000, manager.GetChannel(BandwidthManager.GlobalUpload).GetLimit());
    }

    [Fact(Timeout = 30000)]
    public async Task RequestBandwidthAsync_FastPath_SucceedsImmediately()
    {
        var manager = new BandwidthManager(10, _timeProvider);
        var user = new MockBandwidthUser();

        // No limits -> infinite bandwidth
        int result = await manager.RequestBandwidthAsync(user, 1000, 0, new[] { BandwidthManager.GlobalDownload });

        Assert.Equal(1000, result);
    }

    [Fact(Timeout = 30000)]
    public async Task RequestBandwidthAsync_SlowPath_QueuesAndSatisfiesOnUpdate()
    {
        var manager = new BandwidthManager(10, _timeProvider);
        manager.SetGlobalLimits(1000, 1000);

        var user = new MockBandwidthUser();

        // Initial quota is 0. Request 500.
        var task = manager.RequestBandwidthAsync(user, 500, 0, new[] { BandwidthManager.GlobalDownload });

        Assert.False(task.IsCompleted);

        // Advance time by 500ms
        _timeProvider.Advance(TimeSpan.FromMilliseconds(500));
        manager.Update(null);

        Assert.True(task.IsCompleted);
        Assert.Equal(500, await task);
    }

    [Fact(Timeout = 30000)]
    public async Task Fairness_MultipleUsers_RoundRobin()
    {
        var manager = new BandwidthManager(10, _timeProvider);
        manager.SetGlobalLimits(100, 100); // 100 bytes/sec

        var user1 = new MockBandwidthUser { Name = "User1" };
        var user2 = new MockBandwidthUser { Name = "User2" };

        var task1 = manager.RequestBandwidthAsync(user1, 100, 0, new[] { BandwidthManager.GlobalDownload });
        var task2 = manager.RequestBandwidthAsync(user2, 100, 0, new[] { BandwidthManager.GlobalDownload });

        // Advance 1 second -> 100 bytes available.
        // Round-robin should satisfy the first request in the queue completely.
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        manager.Update(null);

        Assert.True(task1.IsCompleted);
        Assert.False(task2.IsCompleted);
        Assert.Equal(100, await task1);

        // Advance another second -> 100 bytes.
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        manager.Update(null);

        Assert.True(task2.IsCompleted);
        Assert.Equal(100, await task2);
    }

    [Fact(Timeout = 30000)]
    public async Task PartialSatisfaction_WhenQuotaInsufficient()
    {
        var manager = new BandwidthManager(10, _timeProvider);
        manager.SetGlobalLimits(100, 100);

        var user = new MockBandwidthUser();
        var task = manager.RequestBandwidthAsync(user, 100, 0, new[] { BandwidthManager.GlobalDownload });

        // Only 50ms passed -> 5 bytes available.
        _timeProvider.Advance(TimeSpan.FromMilliseconds(50));
        manager.Update(null);

        Assert.True(task.IsCompleted);
        Assert.Equal(5, await task);
    }

    [Fact]
    public void ReturnBandwidth_IncrementsQuota()
    {
        var manager = new BandwidthManager(10, _timeProvider);
        manager.SetGlobalLimits(1000, 1000);

        // Use 500
        var ch = manager.GetChannel(BandwidthManager.GlobalDownload);
        ch.UpdateQuota(1000); // 1000 avail
        ch.UseQuota(500); // 500 avail

        manager.ReturnBandwidth(200, new[] { BandwidthManager.GlobalDownload });

        Assert.Equal(700, ch.AvailableQuota);
    }

    [Fact]
    public void SetTorrentLimits_CreatesSpecificChannels()
    {
        var manager = new BandwidthManager(10, _timeProvider);
        var torrent = TorrentTestUtility.CreateMinimal();

        manager.SetTorrentLimits(torrent, 500, 600);

        var limits = manager.GetTorrentLimits(torrent);
        Assert.Equal(500, limits.DownloadLimit);
        Assert.Equal(600, limits.UploadLimit);

        string hash = torrent.Hash.ToHexStringUpper();
        Assert.Equal(500, manager.GetChannel($"{hash}_DL").GetLimit());
    }

    [Fact]
    public void RemoveTorrentChannels_RemovesSpecificChannels()
    {
        var manager = new BandwidthManager(10, _timeProvider);
        var torrent = TorrentTestUtility.CreateMinimal();

        manager.SetTorrentLimits(torrent, 500, 600);

        string hash = torrent.Hash.ToHexStringUpper();
        Assert.Equal(500, manager.GetChannel($"{hash}_DL").GetLimit());

        manager.RemoveTorrentChannels(torrent);

        // Accessing the channel again will recreate it, but with the default limit (0, unlimited)
        Assert.Equal(0, manager.GetChannel($"{hash}_DL").GetLimit());
    }
}





