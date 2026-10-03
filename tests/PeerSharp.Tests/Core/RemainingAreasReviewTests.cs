using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.BEncoding;
using PeerSharp.Internals;
using PeerSharp.Internals.Bandwidth;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Utilities;
using PeerSharp.Interfaces;
using PeerSharp.Tests.Core.Peers;

namespace PeerSharp.Tests.Core;

public sealed class RemainingAreasReviewTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QueueDoesNotRestartSeedsThatHaveReachedTheirLimit(bool ratio)
    {
        var manager = new TorrentQueueManager(new QueueSettings { Enabled = true, EnforceAutoStop = true }, TimeProvider.System);
        var plan = manager.BuildPlan(new[] { new TorrentQueueManager.QueueItem
        {
            Hash = InfoHash.CreateRandom(), Finished = true, QueueAutoStart = true,
            Ratio = 2, RatioLimit = ratio ? 1 : null,
            SeedingTime = TimeSpan.FromHours(2), SeedTimeLimit = ratio ? null : TimeSpan.FromHours(1)
        }});
        Assert.Empty(plan.Start);
    }

    [Fact]
    public async Task QueueUsesIndependentIdentitiesForV2Torrents()
    {
        var metadata = new TorrentFileMetadata { Info = { Version = TorrentVersion.V2, HashV2 = new InfoHash(new byte[32]) } };
        var other = new TorrentFileMetadata { Info = { Version = TorrentVersion.V2, HashV2 = new InfoHash(Enumerable.Repeat((byte)1, 32).ToArray()) } };
        await using var first = TorrentTestUtility.CreateMinimal(metadata);
        await using var second = TorrentTestUtility.CreateMinimal(other);
        var manager = new TorrentQueueManager(new QueueSettings { Enabled = true }, TimeProvider.System);
        var plan = manager.BuildPlan(new[] { first, second });
        Assert.Equal(2, plan.Start.Distinct().Count());
        Assert.Contains(first.SessionHash, plan.Start);
        Assert.Contains(second.SessionHash, plan.Start);
    }

    [Fact]
    public async Task ExplicitUploadSlotLimitSurvivesRechokingPreviouslyUnchokedPeers()
    {
        await using var torrent = TorrentTestUtility.CreateMinimal();
        torrent.MaxUploadSlots = 1;
        var clock = new FakeTimeProvider();
        var peers = Enumerable.Range(0, 5).Select(_ => new PolicyTestPeer(torrent, clock)).ToArray();
        foreach (var peer in peers) { peer.SetInterested(true); peer.Unchoke(); }
        clock.Advance(TimeSpan.FromMinutes(1));
        var choker = new PeerChoker(torrent, clock, NullLogger.Instance);
        choker.Rechoke(peers, peers.Length);
        Assert.Single(peers, peer => !peer.AmChoking);
        foreach (var peer in peers) await peer.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentLimitChangesKeepManagerAndConfigurationInAgreement()
    {
        await using var torrent = TorrentTestUtility.CreateMinimal();
        await using var bandwidth = new BandwidthManager(10, TimeProvider.System);
        var config = new TorrentConfiguration(torrent, bandwidth);
        Parallel.For(1, 500, i =>
        {
            if (i % 2 == 0) { config.DownloadLimitBytesPerSecond = i; config.DiskReadLimitBytesPerSecond = i; }
            else { config.UploadLimitBytesPerSecond = i; config.DiskWriteLimitBytesPerSecond = i; }
        });
        Assert.Equal((config.DownloadLimitBytesPerSecond, config.UploadLimitBytesPerSecond), bandwidth.GetTorrentLimits(torrent));
        Assert.Equal((config.DiskReadLimitBytesPerSecond, config.DiskWriteLimitBytesPerSecond), bandwidth.GetTorrentDiskLimits(torrent));
    }

    [Fact]
    public async Task ThrowingEventSubscriberDoesNotSuppressTheNextSubscriberOrAlert()
    {
        await using var torrent = TorrentTestUtility.CreateMinimal();
        int delivered = 0;
        Action<ITorrent, Exception> callbacks = (_, _) => throw new InvalidOperationException("subscriber failure");
        callbacks += (_, _) => delivered++;
        torrent.Events = new TorrentEventsBuilder().OnError(callbacks).Build();
        torrent.FireErrorEvent(new IOException("test"));
        Assert.Equal(1, delivered);
    }

    [Fact]
    public void Ed25519RejectsASignatureThatNeedsNoPrivateKey()
    {
        byte[] identityKey = new byte[32]; identityKey[0] = 1;
        byte[] signature = new byte[64]; signature[0] = 1;
        Assert.False(Ed25519.Verify(signature, "arbitrary record"u8, identityKey));
        var item = new DhtMutableItem { PublicKey = identityKey, Signature = signature, SequenceNumber = 0, Value = new BString("record"u8.ToArray()) };
        Assert.Equal(DhtPutError.InvalidSignature, DhtItemCodec.Validate(item));
    }

    [Fact]
    public void DhtStoreOwnsItsValueAndSignatureBytes()
    {
        var store = new DhtItemStore(TimeProvider.System);
        var value = new BDict(); value.Dict["key"] = new BString("original"u8.ToArray());
        var item = DhtItemCodec.CreateSigned(Ed25519.GenerateSeed(), [], 1, value);
        var target = item.Target;
        Assert.Equal(DhtPutError.None, store.Store(item));
        value.Dict["key"] = new BString("changed"u8.ToArray());
        item.Signature[0] ^= 1;
        var stored = Assert.IsType<DhtMutableItem>(store.TryGet(target));
        Assert.True(stored.VerifySignature());
        Assert.Equal("original", ((BDict)stored.Value).GetString("key"));
        stored.PublicKey[0] ^= 1;
        Assert.True(Assert.IsType<DhtMutableItem>(store.TryGet(target)).VerifySignature());
    }

    [Fact]
    public void DhtRateCountersHaveABoundAndExpire()
    {
        var clock = new FakeTimeProvider();
        var store = new DhtItemStore(clock);
        for (int i = 0; i < DhtItemStore.MaxRateCounters; i++)
            Assert.True(store.IsPutAllowed(new IPAddress(new byte[] { 198, 18, (byte)(i >> 8), (byte)i })));
        Assert.False(store.IsPutAllowed(IPAddress.Parse("198.19.0.1")));
        clock.Advance(DhtItemStore.RateLimitWindow);
        Assert.True(store.IsPutAllowed(IPAddress.Parse("198.19.0.1")));
    }

    [Fact]
    public async Task DnsCacheEvictsFreshEntriesAndBoundsHangingLookups()
    {
        var clock = new FakeTimeProvider();
        int lookups = 0;
        var cache = new HostAddressCache(clock, (_, _) => { lookups++; return Task.FromResult(new[] { IPAddress.Loopback }); });
        for (int i = 0; i <= HostAddressCache.SweepThreshold; i++)
        {
            await cache.ResolveAsync($"host{i}.example");
            clock.Advance(TimeSpan.FromMilliseconds(1));
        }
        await cache.ResolveAsync("host0.example");
        Assert.Equal(HostAddressCache.SweepThreshold + 2, lookups);
        var hanging = new HostAddressCache(clock, (_, _) => new TaskCompletionSource<IPAddress[]>().Task);
        var request = hanging.ResolveAsync("hung.example");
        clock.Advance(HostAddressCache.LookupTimeout);
        Assert.Empty(await request.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ParallelAlertProducersCannotExceedTheQueueCapacity()
    {
        var manager = new AlertsManager(TimeProvider.System, new AlertSettings { MaxQueueSize = 4 });
        manager.RegisterAlerts(uint.MaxValue);
        for (int round = 0; round < 100; round++)
        {
            Parallel.For(0, 32, _ => manager.PostAlert(new ConfigAlert { Id = AlertId.ConfigChanged, ConfigType = "review" }));
            Assert.InRange(manager.PopAlerts().Count(alert => alert.Id != AlertId.AlertsDropped), 0, 4);
        }
    }

    [Fact]
    public void ForceProxyOverridesDirectUdpFlagsAndRejectsMissingConfiguration()
    {
        var proxy = new ProxySettings { ForceProxy = true, Type = ProxyType.Http, Host = "proxy.example", ProxyPeers = false, ProxyTrackers = false };
        Assert.Equal(UdpProxyPolicy.Decision.Refuse, UdpProxyPolicy.Decide(proxy, false));
        proxy.Type = ProxyType.Socks5;
        Assert.Equal(UdpProxyPolicy.Decision.TunnelThroughSocks5, UdpProxyPolicy.Decide(proxy, false));
        proxy.Type = ProxyType.None;
        Assert.Equal(UdpProxyPolicy.Decision.Refuse, UdpProxyPolicy.Decide(proxy, false));
        using var factory = new HttpClientFactory();
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient(proxy, true));
    }
}
