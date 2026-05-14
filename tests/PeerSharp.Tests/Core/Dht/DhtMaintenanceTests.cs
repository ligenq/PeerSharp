using PeerSharp.Internals;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Network;
using System.Net;

namespace PeerSharp.Tests.Core.Dht;

/// <summary>
/// Minimal no-op IUdpListener stub for constructing DhtManager in tests.
/// </summary>
internal sealed class NullUdpListener : IUdpListener
{
    public int Port => 0;

    public void RegisterReceiver(IUdpReceiver receiver) { }

    public Task SendAsync(ReadOnlyMemory<byte> data, IPEndPoint endpoint, CancellationToken ct)
        => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public void Stop() { }

    public Task StopAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class DhtMaintenanceTests
{
    private static DhtManager CreateManager()
    {
        var listener = new NullUdpListener();
        var settings = new Settings();
        var id = InfoHash.Empty;
        return new DhtManager(id, listener, settings, TimeProvider.System);
    }

    // ── Transaction cleanup ──────────────────────────────────────────────────

    [Fact]
    public void PerformMaintenance_StaleTransaction_IsRemoved()
    {
        var manager = CreateManager();
        var now = DateTimeOffset.UtcNow;
        var staleTimestamp = now.AddMinutes(-(ProtocolConstants.DhtTransactionTimeoutMinutes + 1));

        manager.InjectTransaction("tx-stale", staleTimestamp);

        manager.PerformMaintenance(now);

        Assert.Equal(0, manager.TransactionCount);
    }

    [Fact]
    public void PerformMaintenance_FreshTransaction_IsKept()
    {
        var manager = CreateManager();
        var now = DateTimeOffset.UtcNow;
        var freshTimestamp = now.AddSeconds(-1);

        manager.InjectTransaction("tx-fresh", freshTimestamp);

        manager.PerformMaintenance(now);

        Assert.Equal(1, manager.TransactionCount);
    }

    [Fact]
    public void PerformMaintenance_MixedTransactions_OnlyRemovesStale()
    {
        var manager = CreateManager();
        var now = DateTimeOffset.UtcNow;

        manager.InjectTransaction("tx-stale", now.AddMinutes(-(ProtocolConstants.DhtTransactionTimeoutMinutes + 1)));
        manager.InjectTransaction("tx-fresh", now.AddSeconds(-1));

        manager.PerformMaintenance(now);

        Assert.Equal(1, manager.TransactionCount);
    }

    // ── Recent-query dedup cleanup ───────────────────────────────────────────

    [Fact]
    public void PerformMaintenance_StaleQuery_IsRemoved()
    {
        var manager = CreateManager();
        var now = DateTimeOffset.UtcNow;

        manager.InjectRecentQuery("query-stale", now.AddMinutes(-(ProtocolConstants.DhtTransactionTimeoutMinutes + 1)));

        manager.PerformMaintenance(now);

        Assert.Equal(0, manager.RecentQueryCount);
    }

    [Fact]
    public void PerformMaintenance_FreshQuery_IsKept()
    {
        var manager = CreateManager();
        var now = DateTimeOffset.UtcNow;

        manager.InjectRecentQuery("query-fresh", now.AddSeconds(-1));

        manager.PerformMaintenance(now);

        Assert.Equal(1, manager.RecentQueryCount);
    }

    // ── Hard cap (MaxRecentQueries = 10 000) ────────────────────────────────

    [Fact]
    public void PerformMaintenance_ExceedsHardCap_RemovesOldEntries()
    {
        var manager = CreateManager();
        var now = DateTimeOffset.UtcNow;
        // All entries older than 1 minute (hard-cap cutoff)
        var oldTimestamp = now.AddMinutes(-2);

        for (int i = 0; i < 10_001; i++)
        {
            manager.InjectRecentQuery($"q{i}", oldTimestamp);
        }

        manager.PerformMaintenance(now);

        // All entries are older than 1 minute so the hard-cap sweep removes them all.
        Assert.True(manager.RecentQueryCount < 10_001,
            $"Expected count < 10001 after hard-cap sweep but got {manager.RecentQueryCount}");
    }

    [Fact]
    public void PerformMaintenance_ExceedsHardCap_KeepsRecentEntries()
    {
        var manager = CreateManager();
        var now = DateTimeOffset.UtcNow;

        // Half the entries are older than 1 minute (will be removed by hard-cap sweep)
        var oldTimestamp = now.AddMinutes(-2);
        // The other half are very recent (within the last second — survive both sweeps)
        var recentTimestamp = now.AddSeconds(-1);

        for (int i = 0; i < 5_001; i++)
        {
            manager.InjectRecentQuery($"old-{i}", oldTimestamp);
        }
        for (int i = 0; i < 5_000; i++)
        {
            manager.InjectRecentQuery($"new-{i}", recentTimestamp);
        }

        manager.PerformMaintenance(now);

        // The 5 000 recent entries must all survive.
        Assert.Equal(5_000, manager.RecentQueryCount);
    }

    // ── Peer cache cleanup ───────────────────────────────────────────────────

    [Fact]
    public void PerformMaintenance_StalePeer_IsRemovedAndKeyEvicted()
    {
        var manager = CreateManager();
        var now = DateTimeOffset.UtcNow;
        var ep = new IPEndPoint(IPAddress.Loopback, 6881);
        var staleLastSeen = now.AddMinutes(-(ProtocolConstants.DhtPeerCacheTimeoutMinutes + 1));

        manager.InjectPeer("AABBCCDDEEFF00112233445566778899AABBCCDD", ep, staleLastSeen);

        manager.PerformMaintenance(now);

        Assert.Equal(0, manager.PeerCacheEntryCount);
    }

    [Fact]
    public void PerformMaintenance_FreshPeer_IsKept()
    {
        var manager = CreateManager();
        var now = DateTimeOffset.UtcNow;
        var ep = new IPEndPoint(IPAddress.Loopback, 6881);
        var freshLastSeen = now.AddMinutes(-1);

        manager.InjectPeer("AABBCCDDEEFF00112233445566778899AABBCCDD", ep, freshLastSeen);

        manager.PerformMaintenance(now);

        Assert.Equal(1, manager.PeerCacheEntryCount);
    }

    [Fact]
    public void PerformMaintenance_MultiplePeers_OnlyStalePeerRemovedButKeyKept()
    {
        var manager = CreateManager();
        var now = DateTimeOffset.UtcNow;
        const string infoHash = "AABBCCDDEEFF00112233445566778899AABBCCDD";

        var stalePeer = new IPEndPoint(IPAddress.Loopback, 6881);
        var freshPeer = new IPEndPoint(IPAddress.Loopback, 6882);

        var staleLastSeen = now.AddMinutes(-(ProtocolConstants.DhtPeerCacheTimeoutMinutes + 1));
        var freshLastSeen = now.AddMinutes(-1);

        manager.InjectPeer(infoHash, stalePeer, staleLastSeen);
        manager.InjectPeer(infoHash, freshPeer, freshLastSeen);

        manager.PerformMaintenance(now);

        // Key must still exist because the fresh peer is in the list.
        Assert.Equal(1, manager.PeerCacheEntryCount);
    }
}
