using PeerSharp.Internals.Bandwidth;
using Microsoft.Extensions.Time.Testing;

namespace PeerSharp.Tests.Core.Bandwidth;

public class BandwidthChannelTests
{
    private readonly FakeTimeProvider _timeProvider = new();

    [Fact]
    public void Constructor_SetsInfiniteLimitAndZeroQuota()
    {
        var channel = new BandwidthChannel(_timeProvider);
        Assert.Equal(0, channel.GetLimit());
        Assert.Equal(int.MaxValue, channel.AvailableQuota); // AvailableQuota returns int.MaxValue for limit 0
    }

    [Fact]
    public void SetLimit_UpdatesLimit()
    {
        var channel = new BandwidthChannel(_timeProvider);
        channel.SetLimit(1000);
        Assert.Equal(1000, channel.GetLimit());
    }

    [Fact]
    public void UpdateQuota_WithLimit_IncrementsQuota()
    {
        var channel = new BandwidthChannel(_timeProvider);
        channel.SetLimit(1000); // 1000 bytes/sec

        channel.UpdateQuota(1000); // 1 second passed
        Assert.Equal(1000, channel.AvailableQuota);

        channel.UpdateQuota(500); // 0.5 second passed
        Assert.Equal(1500, channel.AvailableQuota);
    }

    [Fact]
    public void UpdateQuota_SubQuota_AccumulatesCorrectly()
    {
        var channel = new BandwidthChannel(_timeProvider);
        channel.SetLimit(100); // 100 bytes/sec -> 0.1 bytes/ms

        channel.UpdateQuota(5); // 0.5 bytes -> 0 bytes added, 500 subquota
        Assert.Equal(0, channel.AvailableQuota);

        channel.UpdateQuota(5); // another 0.5 bytes -> 1 byte total added
        Assert.Equal(1, channel.AvailableQuota);
    }

    [Fact]
    public void UpdateQuota_CapsAtThreeTimesLimit()
    {
        var channel = new BandwidthChannel(_timeProvider);
        channel.SetLimit(1000);

        channel.UpdateQuota(5000); // 5 seconds -> 5000 bytes
        Assert.Equal(3000, channel.AvailableQuota);
    }

    [Fact]
    public void UseQuota_DecrementsQuota()
    {
        var channel = new BandwidthChannel(_timeProvider);
        channel.SetLimit(1000);
        channel.UpdateQuota(1000);

        channel.UseQuota(400);
        Assert.Equal(600, channel.AvailableQuota);
    }

    [Fact]
    public void UseQuota_CanGoNegativeButIsCapped()
    {
        var channel = new BandwidthChannel(_timeProvider);
        channel.SetLimit(1000);

        channel.UseQuota(5000);
        Assert.Equal(-3000, channel.AvailableQuota);
    }

    [Fact]
    public void ReturnQuota_IncrementsQuota()
    {
        var channel = new BandwidthChannel(_timeProvider);
        channel.SetLimit(1000);
        channel.UpdateQuota(1000);
        channel.UseQuota(500);

        channel.ReturnQuota(200);
        Assert.Equal(700, channel.AvailableQuota);
    }

    [Fact]
    public void ReturnQuota_IsCapped()
    {
        var channel = new BandwidthChannel(_timeProvider);
        channel.SetLimit(1000);
        channel.UpdateQuota(3000);

        channel.ReturnQuota(1000);
        Assert.Equal(3000, channel.AvailableQuota);
    }

    [Fact]
    public void CanUse_ReturnsCorrectBoolean()
    {
        var channel = new BandwidthChannel(_timeProvider);
        channel.SetLimit(1000);
        channel.UpdateQuota(500);

        Assert.True(channel.CanUse(400));
        Assert.True(channel.CanUse(500));
        Assert.False(channel.CanUse(501));
    }

    [Fact]
    public void ThreadSafety_ConcurrentUsage()
    {
        var channel = new BandwidthChannel(_timeProvider);
        channel.SetLimit(1000000); // High limit
        channel.UpdateQuota(1000); // 1MB quota

        int threads = 10;
        int usesPerThread = 1000;
        int amountPerUse = 10;

        Parallel.For(0, threads, _ =>
        {
            for (int i = 0; i < usesPerThread; i++)
            {
                channel.UseQuota(amountPerUse);
            }
        });

        // Expected: 1,000,000 - (10 * 1000 * 10) = 900,000
        Assert.Equal(900000, channel.AvailableQuota);
    }
}





