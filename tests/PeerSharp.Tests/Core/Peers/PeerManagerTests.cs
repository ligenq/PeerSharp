using PeerSharp.Internals;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Peers;
using PeerSharp.Messages;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace PeerSharp.Tests.Core.Peers;

public class PeerManagerTests
{
    [Fact(Timeout = 30000)]
    public async Task AddIncomingPeerAsync_ForceProxy_Rejects()
    {
        var ctx = CreateContext();
        ctx.Torrent.Settings.Proxy.Type = ProxyType.Http;
        ctx.Torrent.Settings.Proxy.Host = "proxy";
        ctx.Torrent.Settings.Proxy.ForceProxy = true;

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var serverClient = await listener.AcceptTcpClientAsync();

        byte[] handshake = BuildHandshake(ctx.Torrent.InfoFile.Info.Hash.Span, ctx.Torrent.Settings.PeerId);
        await ctx.Manager.AddIncomingPeerAsync(serverClient, handshake);

        Assert.Equal(0, ctx.Manager.ConnectedCount);
        Assert.Equal(0, ctx.Governor.AcquiredConnections);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task AddIncomingPeerAsync_Blocklist_Rejects()
    {
        var ctx = CreateContext();
        var blocklist = new IpBlocklist();
        blocklist.AddRange(IPAddress.Loopback, IPAddress.Loopback);
        blocklist.Enabled = true;
        ctx.Torrent.Blocklist = blocklist;

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var serverClient = await listener.AcceptTcpClientAsync();

        byte[] handshake = BuildHandshake(ctx.Torrent.InfoFile.Info.Hash.Span, ctx.Torrent.Settings.PeerId);
        await ctx.Manager.AddIncomingPeerAsync(serverClient, handshake);

        Assert.Equal(0, ctx.Manager.ConnectedCount);
        Assert.Equal(0, ctx.Governor.AcquiredConnections);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task AddIncomingPeerAsync_InvalidHandshake_ReleasesSlot()
    {
        var ctx = CreateContext();

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var serverClient = await listener.AcceptTcpClientAsync();

        byte[] wrongHash = Enumerable.Repeat((byte)0xAB, 20).ToArray();
        byte[] handshake = BuildHandshake(wrongHash, ctx.Torrent.Settings.PeerId);
        await ctx.Manager.AddIncomingPeerAsync(serverClient, handshake);

        Assert.Equal(0, ctx.Manager.ConnectedCount);
        Assert.Equal(1, ctx.Governor.AcquiredConnections);
        Assert.Equal(1, ctx.Governor.ReleasedConnections);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task ApplyConnectionBackoff_SetsNextAttemptWithExponentialDelay()
    {
        var ctx = CreateContext();
        ctx.Torrent.Settings.Connection.PeerReconnectBaseSeconds = 5;
        ctx.Torrent.Settings.Connection.PeerReconnectMaxSeconds = 300;
        ctx.Torrent.Settings.Connection.PeerReconnectJitterMs = 0;

        var history = new PeerHistory { EndPoint = new IPEndPoint(IPAddress.Loopback, 12345) };
        history.FruitlessConnectionCount = 3; // 5 * 2^(3-1) = 20s

        InvokePrivate(ctx.Manager, "ApplyConnectionBackoff", history);

        var now = TimeProvider.System.GetUtcNow();
        Assert.True(history.NextConnectAttempt >= now.AddSeconds(18));
        Assert.True(history.NextConnectAttempt <= now.AddSeconds(25));

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task CheckPeerHealthAsync_DropsSlowPeerAfterGrace()
    {
        var ctx = CreateContext();
        ctx.Torrent.Settings.Connection.SlowPeerMinConnectedPeers = 1;
        ctx.Torrent.Settings.Connection.SlowPeerMinDownloadSpeedBytesPerSec = 1000;
        ctx.Torrent.Settings.Connection.SlowPeerGraceSeconds = 0;

        var peer = new PeerCommunication(ctx.Torrent, new TestPeerListener(), TimeProvider.System);
        SetPrivateField(peer, "_smoothedDownloadSpeed", 0);
        SetPrivateField(peer, "_amInterested", 1);
        SetPrivateField(peer, "_peerChoking", 0);
        SetPrivateField(peer, "_lastActivityTicksValue", Environment.TickCount64);

        var connectedPeers = GetPrivateField<ConcurrentDictionary<PeerCommunication, byte>>(ctx.Manager, "_connectedPeers");
        connectedPeers.TryAdd(peer, 0);
        SetPrivateField(ctx.Manager, "_connectedPeersCount", 1);

        var slowPeers = GetPrivateField<ConcurrentDictionary<PeerCommunication, long>>(ctx.Manager, "_slowPeers");
        slowPeers.TryAdd(peer, Environment.TickCount64 - 10_000);

        await (Task)InvokePrivate(ctx.Manager, "CheckPeerHealthAsync")!;

        Assert.False(slowPeers.ContainsKey(peer));

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task PruneKnownPeersCache_RemovesOldestTwentyPercentAndPeerSources()
    {
        var ctx = CreateContext();
        var cache = GetPrivateField<ConcurrentDictionary<IPEndPoint, PeerHistory>>(ctx.Manager, "_knownPeersCache");
        var peerSources = GetPrivateField<ConcurrentDictionary<IPEndPoint, PeerCommunication>>(ctx.Manager, "_peerSources");
        var source = new PeerCommunication(ctx.Torrent, new TestPeerListener(), TimeProvider.System);
        var baseTime = TimeProvider.System.GetUtcNow();
        var endpoints = Enumerable.Range(0, 10)
            .Select(i => new IPEndPoint(IPAddress.Parse($"10.0.0.{i + 1}"), 6881))
            .ToArray();

        for (int i = 0; i < endpoints.Length; i++)
        {
            cache[endpoints[i]] = new PeerHistory
            {
                EndPoint = endpoints[i],
                LastAttempt = baseTime.AddMinutes(i)
            };
            peerSources[endpoints[i]] = source;
        }
        SetPrivateField(ctx.Manager, "_knownPeersCacheCount", endpoints.Length);

        InvokePrivate(ctx.Manager, "PruneKnownPeersCache");

        Assert.Equal(8, cache.Count);
        Assert.False(cache.ContainsKey(endpoints[0]));
        Assert.False(cache.ContainsKey(endpoints[1]));
        Assert.False(peerSources.ContainsKey(endpoints[0]));
        Assert.False(peerSources.ContainsKey(endpoints[1]));
        Assert.True(cache.ContainsKey(endpoints[2]));

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task ApplyPexFlags_SetsSeedAndUtpHints()
    {
        var history = new PeerHistory { EndPoint = new IPEndPoint(IPAddress.Loopback, 1234) };

        // Reach into the static helper directly
        var method = typeof(PeerManager).GetMethod("ApplyPexFlags",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        method!.Invoke(null, [history, (byte)(UtPex.Peer.Seed | UtPex.Peer.Utp)]);

        Assert.True(history.IsSeed);
        Assert.True(history.UtpHinted);
        Assert.True(history.UtpSupported);
        await Task.CompletedTask;
    }

    [Fact(Timeout = 30000)]
    public async Task ApplyPexFlags_NoFlags_LeavesHistoryUnchanged()
    {
        var history = new PeerHistory { EndPoint = new IPEndPoint(IPAddress.Loopback, 1234) };
        history.IsSeed = false;
        history.UtpHinted = false;

        var method = typeof(PeerManager).GetMethod("ApplyPexFlags",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        method!.Invoke(null, [history, (byte)0]);

        Assert.False(history.IsSeed);
        Assert.False(history.UtpHinted);
        await Task.CompletedTask;
    }

    [Fact(Timeout = 30000)]
    public async Task GetOptimisticUnchokeIntervalSeconds_FloorAtFiveSeconds()
    {
        var ctx = CreateContext();
        ctx.Torrent.Settings.Connection.OptimisticUnchokeIntervalSeconds = 1;

        int seconds = (int)InvokePrivate(ctx.Manager, "GetOptimisticUnchokeIntervalSeconds")!;
        Assert.Equal(5, seconds);

        ctx.Torrent.Settings.Connection.OptimisticUnchokeIntervalSeconds = 30;
        seconds = (int)InvokePrivate(ctx.Manager, "GetOptimisticUnchokeIntervalSeconds")!;
        Assert.Equal(30, seconds);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task GetUploadSlots_NoLimitFallsBackToConnectedPeerCount()
    {
        var ctx = CreateContext();
        ctx.Torrent.Settings.Connection.UploadSlotsMin = 4;
        ctx.Torrent.Settings.Connection.UploadSlotsMax = 8;
        ctx.Torrent.Settings.Transfer.MaxUploadSpeed = 0;

        // No connected peers and no upload limit: result = min(max, max(min, count)) = min(8, max(4,0)) = 4
        int slots = (int)InvokePrivate(ctx.Manager, "GetUploadSlots")!;
        Assert.Equal(4, slots);

        // With 6 connected peers, min<=slots<=max
        SetPrivateField(ctx.Manager, "_connectedPeersCount", 6);
        slots = (int)InvokePrivate(ctx.Manager, "GetUploadSlots")!;
        Assert.Equal(6, slots);

        // With 12 connected peers, capped by max
        SetPrivateField(ctx.Manager, "_connectedPeersCount", 12);
        slots = (int)InvokePrivate(ctx.Manager, "GetUploadSlots")!;
        Assert.Equal(8, slots);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task GetUploadSlots_ScalesWithUploadLimit()
    {
        var ctx = CreateContext();
        ctx.Torrent.Settings.Connection.UploadSlotsMin = 1;
        ctx.Torrent.Settings.Connection.UploadSlotsMax = 16;
        ctx.Torrent.Settings.Connection.TargetUploadPerSlotBytesPerSec = 100_000;
        ctx.Torrent.Settings.Transfer.MaxUploadSpeed = 1_000_000;
        SetPrivateField(ctx.Manager, "_connectedPeersCount", 32);

        int slots = (int)InvokePrivate(ctx.Manager, "GetUploadSlots")!;
        // 1_000_000 / 100_000 = 10 slots
        Assert.Equal(10, slots);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task GetUtpRatioPercent_EmptySwarm_ReturnsZero()
    {
        var ctx = CreateContext();
        int ratio = (int)InvokePrivate(ctx.Manager, "GetUtpRatioPercent")!;
        Assert.Equal(0, ratio);
        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task IsSpeedStable_NeverEnteredStable_ReturnsFalse()
    {
        var ctx = CreateContext();
        SetPrivateField(ctx.Manager, "_stableSpeedSince", DateTimeOffset.MinValue);
        bool stable = (bool)InvokePrivate(ctx.Manager, "IsSpeedStable", DateTimeOffset.UtcNow)!;
        Assert.False(stable);
        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task IsSpeedStable_InsideStabilityWindow_ReturnsFalse()
    {
        var ctx = CreateContext();
        ctx.Torrent.Settings.Connection.StableSpeedSeconds = 20;
        var since = DateTimeOffset.UtcNow;
        SetPrivateField(ctx.Manager, "_stableSpeedSince", since);

        bool stable = (bool)InvokePrivate(ctx.Manager, "IsSpeedStable", since.AddSeconds(5))!;
        Assert.False(stable);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task IsSpeedStable_StableSecondsZero_ReturnsTrueOnceTriggered()
    {
        var ctx = CreateContext();
        ctx.Torrent.Settings.Connection.StableSpeedSeconds = 0;
        var since = DateTimeOffset.UtcNow;
        SetPrivateField(ctx.Manager, "_stableSpeedSince", since);

        bool stable = (bool)InvokePrivate(ctx.Manager, "IsSpeedStable", since)!;
        Assert.True(stable);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task IsSpeedStable_PastStabilityWindow_ReturnsTrue()
    {
        var ctx = CreateContext();
        ctx.Torrent.Settings.Connection.StableSpeedSeconds = 10;
        var since = DateTimeOffset.UtcNow;
        SetPrivateField(ctx.Manager, "_stableSpeedSince", since);

        bool stable = (bool)InvokePrivate(ctx.Manager, "IsSpeedStable", since.AddSeconds(11))!;
        Assert.True(stable);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task UpdateStableSpeedState_DisabledThreshold_AlwaysClearsStableSince()
    {
        var ctx = CreateContext();
        ctx.Torrent.Settings.Connection.StableSpeedThresholdBytesPerSec = 0;
        SetPrivateField(ctx.Manager, "_stableSpeedSince", DateTimeOffset.UtcNow);

        InvokePrivate(ctx.Manager, "UpdateStableSpeedState", DateTimeOffset.UtcNow, 5_000_000);

        var since = (DateTimeOffset)GetPrivateInstanceField(ctx.Manager, "_stableSpeedSince")!;
        Assert.Equal(DateTimeOffset.MinValue, since);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task UpdateStableSpeedState_AboveThreshold_StartsClock()
    {
        var ctx = CreateContext();
        ctx.Torrent.Settings.Connection.StableSpeedThresholdBytesPerSec = 1_000_000;
        SetPrivateField(ctx.Manager, "_stableSpeedSince", DateTimeOffset.MinValue);

        var now = DateTimeOffset.UtcNow;
        InvokePrivate(ctx.Manager, "UpdateStableSpeedState", now, 2_000_000);

        var since = (DateTimeOffset)GetPrivateInstanceField(ctx.Manager, "_stableSpeedSince")!;
        Assert.Equal(now, since);

        // Subsequent above-threshold updates do not move the clock back
        InvokePrivate(ctx.Manager, "UpdateStableSpeedState", now.AddSeconds(5), 3_000_000);
        since = (DateTimeOffset)GetPrivateInstanceField(ctx.Manager, "_stableSpeedSince")!;
        Assert.Equal(now, since);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task UpdateStableSpeedState_BelowThreshold_ResetsClock()
    {
        var ctx = CreateContext();
        ctx.Torrent.Settings.Connection.StableSpeedThresholdBytesPerSec = 1_000_000;
        SetPrivateField(ctx.Manager, "_stableSpeedSince", DateTimeOffset.UtcNow);

        InvokePrivate(ctx.Manager, "UpdateStableSpeedState", DateTimeOffset.UtcNow, 500_000);

        var since = (DateTimeOffset)GetPrivateInstanceField(ctx.Manager, "_stableSpeedSince")!;
        Assert.Equal(DateTimeOffset.MinValue, since);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task TryGetLowestPriorityPeer_EmptySwarm_ReturnsNull()
    {
        var ctx = CreateContext();
        var peer = InvokePrivate(ctx.Manager, "TryGetLowestPriorityPeer");
        Assert.Null(peer);
        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task TryGetLowestPriorityPeer_PicksLowest()
    {
        var ctx = CreateContext();
        var p1 = new PeerCommunication(ctx.Torrent, new TestPeerListener(), TimeProvider.System) { Priority = 100 };
        var p2 = new PeerCommunication(ctx.Torrent, new TestPeerListener(), TimeProvider.System) { Priority = 50 };
        var p3 = new PeerCommunication(ctx.Torrent, new TestPeerListener(), TimeProvider.System) { Priority = 75 };

        var connected = GetPrivateField<ConcurrentDictionary<PeerCommunication, byte>>(ctx.Manager, "_connectedPeers");
        connected.TryAdd(p1, 0);
        connected.TryAdd(p2, 0);
        connected.TryAdd(p3, 0);

        var lowest = (PeerCommunication?)InvokePrivate(ctx.Manager, "TryGetLowestPriorityPeer");
        Assert.Same(p2, lowest);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task CleanupPendingConnections_RemovesExpiredEntries()
    {
        var ctx = CreateContext();
        var pending = GetPrivateField<ConcurrentDictionary<IPEndPoint, long>>(ctx.Manager, "_pendingConnections");

        var ep1 = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 6881);
        var ep2 = new IPEndPoint(IPAddress.Parse("10.0.0.2"), 6881);

        long now = Environment.TickCount64;
        pending[ep1] = now - 60_000; // > 10s old, should be removed
        pending[ep2] = now - 1_000; // recent, kept

        InvokePrivate(ctx.Manager, "CleanupPendingConnections");

        Assert.False(pending.ContainsKey(ep1));
        Assert.True(pending.ContainsKey(ep2));

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task BuildTransportPlan_TcpDisabled_ReturnsUtpOnly()
    {
        var ctx = CreateContext();
        var settings = ctx.Torrent.Settings.Connection;
        settings.EnableTcpOut = false;
        settings.EnableUtpOut = true;
        settings.PreferUtp = true;
        settings.UtpWarmupSeconds = 0;

        // Provide a UtpManager - PeerManager checks for it
        SetUtpManagerStub(ctx.Torrent);

        var plan = InvokeBuildTransportPlan(ctx.Manager, settings, history: null, forceUtp: false);
        Assert.Equal([TransportPreference.Utp], plan);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task BuildTransportPlan_UtpDisabled_ReturnsTcpOnly()
    {
        var ctx = CreateContext();
        var settings = ctx.Torrent.Settings.Connection;
        settings.EnableUtpOut = false;
        settings.EnableTcpOut = true;
        SetUtpManagerStub(ctx.Torrent);

        var plan = InvokeBuildTransportPlan(ctx.Manager, settings, history: null, forceUtp: false);
        Assert.Equal([TransportPreference.Tcp], plan);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task BuildTransportPlan_NoUtpManager_PreventsUtpOnPlanEvenWhenAvailable()
    {
        var ctx = CreateContext();
        var settings = ctx.Torrent.Settings.Connection;
        settings.EnableTcpOut = true;
        settings.EnableUtpOut = true;
        // Don't set UtpManager - should remove utp from plan
        var plan = InvokeBuildTransportPlan(ctx.Manager, settings, history: null, forceUtp: false);
        Assert.Equal([TransportPreference.Tcp], plan);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task BuildTransportPlan_GlobalUtpPenalty_PreventsUtp()
    {
        var ctx = CreateContext();
        var settings = ctx.Torrent.Settings.Connection;
        settings.EnableTcpOut = true;
        settings.EnableUtpOut = true;
        SetUtpManagerStub(ctx.Torrent);

        SetPrivateField(ctx.Manager, "_globalUtpPenaltyUntil", DateTimeOffset.UtcNow.AddMinutes(5));

        var plan = InvokeBuildTransportPlan(ctx.Manager, settings, history: null, forceUtp: false);
        Assert.Equal([TransportPreference.Tcp], plan);

        await CleanupAsync(ctx);
    }

    private static IReadOnlyList<TransportPreference> InvokeBuildTransportPlan(
        PeerManager manager,
        ConnectionSettings settings,
        PeerHistory? history,
        bool forceUtp)
    {
        var method = typeof(PeerManager).GetMethod(
            "BuildTransportPlan",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (IReadOnlyList<TransportPreference>)method!.Invoke(manager, [settings, history, forceUtp])!;
    }

    [Fact(Timeout = 30000)]
    public async Task PortReceivedAsync_NoDhtManager_NoOps()
    {
        var ctx = CreateContext();
        var peer = new PeerCommunication(ctx.Torrent, new TestPeerListener(), TimeProvider.System)
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 6881)
        };

        await ctx.Manager.PortReceivedAsync(peer, 6882);
        // Asserting no exception is sufficient here.

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task PortReceivedAsync_NoRemoteEndpoint_NoOps()
    {
        var ctx = CreateContext();
        var peer = new PeerCommunication(ctx.Torrent, new TestPeerListener(), TimeProvider.System);

        await ctx.Manager.PortReceivedAsync(peer, 6882);
        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task HolepunchMessageReceivedAsync_NonConnectMessage_DoesNotInitiateConnection()
    {
        var ctx = CreateContext();
        var peer = new PeerCommunication(ctx.Torrent, new TestPeerListener(), TimeProvider.System);
        var endpoint = new IPEndPoint(IPAddress.Parse("203.0.113.5"), 6881);

        // Should not throw and should not connect (not a Connect msg).
        await ctx.Manager.HolepunchMessageReceivedAsync(peer, UtHolepunch.MsgId.Rendezvous, endpoint, UtHolepunch.ErrorCode.None);

        // No pending connection should have been created
        var pending = GetPrivateField<ConcurrentDictionary<IPEndPoint, long>>(ctx.Manager, "_pendingConnections");
        Assert.False(pending.ContainsKey(endpoint));

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task GetOrAddKnownPeerHistory_ReturnsSameInstanceOnRepeatedCalls()
    {
        var ctx = CreateContext();
        var ep = new IPEndPoint(IPAddress.Parse("198.51.100.7"), 6881);

        var h1 = (PeerHistory)InvokePrivate(ctx.Manager, "GetOrAddKnownPeerHistory", ep)!;
        var h2 = (PeerHistory)InvokePrivate(ctx.Manager, "GetOrAddKnownPeerHistory", ep)!;

        Assert.Same(h1, h2);
        Assert.Equal(ep, h1.EndPoint);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task GetPieceAvailability_AggregatesOverConnectedPeers()
    {
        var ctx = CreateContext();
        // Add 2 peers each having different pieces
        var peer1 = new PeerCommunication(ctx.Torrent, new TestPeerListener(), TimeProvider.System);
        var peer2 = new PeerCommunication(ctx.Torrent, new TestPeerListener(), TimeProvider.System);
        peer1.PeerPieces.AddPiece(0);
        peer2.PeerPieces.AddPiece(0);

        var connected = GetPrivateField<ConcurrentDictionary<PeerCommunication, byte>>(ctx.Manager, "_connectedPeers");
        connected.TryAdd(peer1, 0);
        connected.TryAdd(peer2, 0);

        int[] availability = ctx.Manager.GetPieceAvailability();
        Assert.Equal(ctx.Torrent.Pieces.Count, availability.Length);
        Assert.Equal(2, availability[0]);

        await CleanupAsync(ctx);
    }

    private static void SetUtpManagerStub(Torrent torrent)
    {
        torrent.UtpManager = new FakeUtpManager();
    }

    private sealed class FakeUtpManager : PeerSharp.Internals.Utp.IUtpManager
    {
        public Action<PeerSharp.Internals.Utp.UtpStream>? OnNewConnection { get; set; }
        public void CloseStream(PeerSharp.Internals.Utp.UtpStream stream) { }
        public PeerSharp.Internals.Utp.UtpStream CreateStream(IPEndPoint remote) => null!;
        public Task SendAsync(ReadOnlyMemory<byte> packet, IPEndPoint remote, CancellationToken ct) => Task.CompletedTask;
        public void Start(PeerSharp.Internals.Network.IUdpListener listener) { }
        public void Stop() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static object? GetPrivateInstanceField(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return field?.GetValue(target);
    }

    [Fact(Timeout = 30000)]
    public async Task PruneKnownPeersCache_WithFewerThanFivePeers_DoesNothing()
    {
        var ctx = CreateContext();
        var cache = GetPrivateField<ConcurrentDictionary<IPEndPoint, PeerHistory>>(ctx.Manager, "_knownPeersCache");
        var baseTime = TimeProvider.System.GetUtcNow();
        for (int i = 0; i < 4; i++)
        {
            var endpoint = new IPEndPoint(IPAddress.Parse($"10.1.0.{i + 1}"), 6881);
            cache[endpoint] = new PeerHistory
            {
                EndPoint = endpoint,
                LastAttempt = baseTime.AddMinutes(i)
            };
        }
        SetPrivateField(ctx.Manager, "_knownPeersCacheCount", 4);

        InvokePrivate(ctx.Manager, "PruneKnownPeersCache");

        Assert.Equal(4, cache.Count);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task HandshakeFinishedAsync_NoPieces_SendsNothing()
    {
        var ctx = CreateContext();
        var peer = new PeerCommunication(ctx.Torrent, ctx.Manager, TimeProvider.System);
        SetPrivateField(peer, "_connected", 1);
        // Pieces.ReceivedCount = 0 by default

        await ctx.Manager.HandshakeFinishedAsync(peer);

        var queue = GetPrivateField<MessageQueue>(peer, "_sendQueue");
        // No bitfield/HaveAll/HaveNone expected when we have no pieces
        // Port may or may not be enqueued depending on DHT config, so filter those out
        var nonPortMessages = new List<PeerMessage>();
        while (queue.TryDequeue(out var msg))
        {
            if (msg!.Id != MessageId.Port)
            {
                nonPortMessages.Add(msg);
            }
        }
        Assert.Empty(nonPortMessages);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task HandshakeFinishedAsync_AllPieces_FastPeer_SendsHaveAll()
    {
        var ctx = CreateContext();
        ctx.Torrent.Pieces.SetHaveAll();

        var peer = new PeerCommunication(ctx.Torrent, ctx.Manager, TimeProvider.System);
        SetPrivateField(peer, "_connected", 1);
        SetPrivateProperty(peer, "RemoteSupportsFastExtension", true);

        await ctx.Manager.HandshakeFinishedAsync(peer);

        var queue = GetPrivateField<MessageQueue>(peer, "_sendQueue");
        var messages = new List<PeerMessage>();
        while (queue.TryDequeue(out var msg))
        {
            messages.Add(msg!);
        }

        Assert.Contains(messages, m => m.Id == MessageId.HaveAll);
        Assert.DoesNotContain(messages, m => m.Id == MessageId.Bitfield);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task HandshakeFinishedAsync_AllPieces_NonFastPeer_SendsBitfield()
    {
        var ctx = CreateContext();
        ctx.Torrent.Pieces.SetHaveAll();

        var peer = new PeerCommunication(ctx.Torrent, ctx.Manager, TimeProvider.System);
        SetPrivateField(peer, "_connected", 1);
        // RemoteSupportsFastExtension = false (default)

        await ctx.Manager.HandshakeFinishedAsync(peer);

        var queue = GetPrivateField<MessageQueue>(peer, "_sendQueue");
        var messages = new List<PeerMessage>();
        while (queue.TryDequeue(out var msg))
        {
            messages.Add(msg!);
        }

        Assert.Contains(messages, m => m.Id == MessageId.Bitfield);
        Assert.DoesNotContain(messages, m => m.Id == MessageId.HaveAll);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task HandshakeFinishedAsync_SomePieces_SendsBitfield()
    {
        // Need 2 pieces so AddPiece(0) gives ReceivedCount=1 < TotalPieces=2
        var metadata = new TorrentFileMetadata();
        metadata.Info.Version = TorrentVersion.V1;
        metadata.Info.Hash = new InfoHash(Enumerable.Range(0, 20).Select(i => (byte)i).ToArray());
        metadata.Info.PieceSize = ProtocolConstants.BlockSize;
        metadata.Info.FullSize = ProtocolConstants.BlockSize * 2;
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "file.bin", Size = ProtocolConstants.BlockSize * 2, Offset = 0 });

        string path = CreateTempPath();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);
        torrent.Settings.Connection.Encryption = Encryption.Refuse;
        var governor = new FakeConnectionGovernor();
        var manager = new PeerManager(torrent, new TorrentTestUtility.MockGeoIpService(), new RealPeerFactory(), TimeProvider.System, governor);
        var ctx = new PeerManagerContext(torrent, manager, governor, path);

        torrent.Pieces.AddPiece(0); // 1 of 2 pieces

        var peer = new PeerCommunication(ctx.Torrent, ctx.Manager, TimeProvider.System);
        SetPrivateField(peer, "_connected", 1);
        SetPrivateProperty(peer, "RemoteSupportsFastExtension", true);

        await ctx.Manager.HandshakeFinishedAsync(peer);

        var queue = GetPrivateField<MessageQueue>(peer, "_sendQueue");
        var messages = new List<PeerMessage>();
        while (queue.TryDequeue(out var msg))
        {
            messages.Add(msg!);
        }

        Assert.Contains(messages, m => m.Id == MessageId.Bitfield);
        Assert.DoesNotContain(messages, m => m.Id == MessageId.HaveAll);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task ExtendedMessageReceivedAsync_UnknownExtensionId_DoesNotThrow()
    {
        var ctx = CreateContext();
        var peer = new PeerCommunication(ctx.Torrent, ctx.Manager, TimeProvider.System);
        // LocalMessageId is null (never set) so unknown type 99 won't match

        var ex = await Record.ExceptionAsync(() => ctx.Manager.ExtendedMessageReceivedAsync(peer, 99, []));
        Assert.Null(ex);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task ExtendedMessageReceivedAsync_UtMetadataId_DoesNotThrowOnEmptyPayload()
    {
        var ctx = CreateContext();
        var peer = new PeerCommunication(ctx.Torrent, ctx.Manager, TimeProvider.System);
        peer.UtMetadata.SetLocalMessageId(5);

        // Send an empty/invalid payload — should not throw
        var ex = await Record.ExceptionAsync(() => ctx.Manager.ExtendedMessageReceivedAsync(peer, 5, []));
        Assert.Null(ex);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task AddIncomingPeerAsync_AtConnectionLimit_Rejects()
    {
        var ctx = CreateContext();
        ctx.Torrent.Settings.Connection.MaxPeersPerTorrent = 0; // limit is 0, so any connection is rejected

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var serverClient = await listener.AcceptTcpClientAsync();

        byte[] handshake = BuildHandshake(ctx.Torrent.InfoFile.Info.Hash.Span, ctx.Torrent.Settings.PeerId);
        await ctx.Manager.AddIncomingPeerAsync(serverClient, handshake);

        Assert.Equal(0, ctx.Manager.ConnectedCount);
        Assert.Equal(0, ctx.Governor.AcquiredConnections);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task AddIncomingPeerAsync_GlobalGovernorAtLimit_Rejects()
    {
        var ctx = CreateContext();
        ctx.Torrent.Settings.Connection.MaxPeersPerTorrent = 100;

        // Use a governor that denies slots
        var denyGovernor = new DenyAllGovernor();
        var manager = new PeerManager(ctx.Torrent, new TorrentTestUtility.MockGeoIpService(), new RealPeerFactory(), TimeProvider.System, denyGovernor);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var serverClient = await listener.AcceptTcpClientAsync();

        byte[] handshake = BuildHandshake(ctx.Torrent.InfoFile.Info.Hash.Span, ctx.Torrent.Settings.PeerId);
        await manager.AddIncomingPeerAsync(serverClient, handshake);

        Assert.Equal(0, manager.ConnectedCount);

        await manager.StopAsync();
        await ctx.Torrent.DisposeAsync();
        try { if (Directory.Exists(ctx.Path)) { Directory.Delete(ctx.Path, true); } } catch { }
    }

    [Fact(Timeout = 30000)]
    public async Task MessageReceivedAsync_Interested_FastExtensionPeer_SendsAllowedFast()
    {
        var ctx = CreateContext();
        ctx.Torrent.Pieces.AddPiece(0);

        var peer = new PeerCommunication(ctx.Torrent, ctx.Manager, TimeProvider.System);
        SetPrivateField(peer, "_connected", 1);
        SetPrivateProperty(peer, "RemoteSupportsFastExtension", true);
        SetPrivateField(peer, "_peerInterested", 1);
        peer.RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 6881);

        await ctx.Manager.MessageReceivedAsync(peer, new PeerMessage(MessageId.Interested));
        await Task.Delay(200);

        var queue = GetPrivateField<MessageQueue>(peer, "_sendQueue");
        var messages = new List<PeerMessage>();
        while (queue.TryDequeue(out var msg))
        {
            messages.Add(msg!);
        }

        Assert.Contains(messages, m => m.Id == MessageId.AllowedFast);

        await CleanupAsync(ctx);
    }

    private static void SetPrivateProperty(object target, string propertyName, object value)
    {
        var prop = target.GetType().GetProperty(propertyName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        if (prop?.SetMethod == null)
        {
            throw new InvalidOperationException($"Property '{propertyName}' not settable on {target.GetType().Name}");
        }

        prop.SetValue(target, value);
    }

    private sealed class DenyAllGovernor : IConnectionGovernor
    {
        public int ActiveConnections => 0;
        public int PendingConnections => 0;
        public bool TryAcquireConnectionSlot() => false;
        public bool TryAcquirePendingSlot() => false;
        public void ReleaseConnectionSlot() { }
        public void ReleasePendingSlot() { }
    }

    private static PeerManagerContext CreateContext()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Version = TorrentVersion.V1;
        metadata.Info.Hash = new InfoHash(Enumerable.Range(0, 20).Select(i => (byte)i).ToArray());
        metadata.Info.PieceSize = ProtocolConstants.BlockSize;
        metadata.Info.FullSize = ProtocolConstants.BlockSize;
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "file.bin", Size = ProtocolConstants.BlockSize, Offset = 0 });

        string path = CreateTempPath();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);
        torrent.Settings.Connection.Encryption = Encryption.Refuse;

        var governor = new FakeConnectionGovernor();
        var manager = new PeerManager(torrent, new TorrentTestUtility.MockGeoIpService(), new RealPeerFactory(), TimeProvider.System, governor);

        return new PeerManagerContext(torrent, manager, governor, path);
    }

    private static byte[] BuildHandshake(ReadOnlySpan<byte> infoHash, byte[] peerId)
    {
        byte[] handshake = new byte[68];
        handshake[0] = 19;
        System.Text.Encoding.ASCII.GetBytes("BitTorrent protocol").CopyTo(handshake, 1);
        infoHash.CopyTo(handshake.AsSpan(28, 20));
        peerId.CopyTo(handshake, 48);
        return handshake;
    }

    private static string CreateTempPath()
    {
        return Path.Combine(Path.GetTempPath(), "MtTorrentTests_PeerManager", Guid.NewGuid().ToString("N"));
    }

    private static async Task CleanupAsync(PeerManagerContext ctx)
    {
        await ctx.Manager.StopAsync();
        await ctx.Torrent.DisposeAsync();
        try
        {
            if (Directory.Exists(ctx.Path))
            {
                Directory.Delete(ctx.Path, true);
            }
        }
        catch
        {
            // Best-effort cleanup for temp artifacts.
        }
    }

    private sealed record PeerManagerContext(Torrent Torrent, PeerManager Manager, FakeConnectionGovernor Governor, string Path);

    private static object? InvokePrivate(object target, string methodName, params object[] args)
    {
        var method = target.GetType().GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (method == null)
        {
            throw new InvalidOperationException($"Method '{methodName}' not found on {target.GetType().Name}");
        }
        return method.Invoke(target, args);
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field == null)
        {
            throw new InvalidOperationException($"Field '{fieldName}' not found on {target.GetType().Name}");
        }
        field.SetValue(target, value);
    }

    private static T GetPrivateField<T>(object target, string fieldName) where T : class
    {
        var field = target.GetType().GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field == null)
        {
            throw new InvalidOperationException($"Field '{fieldName}' not found on {target.GetType().Name}");
        }
        return (T)field.GetValue(target)!;
    }

    private sealed class TestPeerListener : IPeerListener
    {
        public Task HandshakeFinishedAsync(IPeerCommunication peer) => Task.CompletedTask;
        public Task ConnectionClosedAsync(IPeerCommunication peer, int code) => Task.CompletedTask;
        public Task MessageReceivedAsync(IPeerCommunication peer, PeerMessage msg) => Task.CompletedTask;
        public Task ExtendedHandshakeFinishedAsync(IPeerCommunication peer, ExtensionHandshake handshake) => Task.CompletedTask;
        public Task ExtendedMessageReceivedAsync(IPeerCommunication peer, int type, byte[] data) => Task.CompletedTask;
        public Task PexReceivedAsync(IPeerCommunication peer, List<IPEndPoint> added, List<byte> addedFlags, List<IPEndPoint> dropped) => Task.CompletedTask;
        public Task HolepunchMessageReceivedAsync(IPeerCommunication peer, UtHolepunch.MsgId id, IPEndPoint endpoint, UtHolepunch.ErrorCode error) => Task.CompletedTask;
        public Task PortReceivedAsync(IPeerCommunication peer, ushort dhtPort) => Task.CompletedTask;
    }

    private sealed class RealPeerFactory : IPeerCommunicationFactory
    {
        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider)
        {
            return new PeerCommunication(torrent, listener, timeProvider);
        }

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, Stream stream, IPEndPoint? remoteEndPoint)
        {
            return new PeerCommunication(torrent, listener, timeProvider);
        }

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, TcpClient client)
        {
            return new PeerCommunication(torrent, listener, timeProvider);
        }
    }

    private sealed class FakeConnectionGovernor : IConnectionGovernor
    {
        public int ActiveConnections => 0;
        public int PendingConnections => 0;
        public int AcquiredConnections { get; private set; }
        public int ReleasedConnections { get; private set; }

        public bool TryAcquireConnectionSlot()
        {
            AcquiredConnections++;
            return true;
        }

        public bool TryAcquirePendingSlot() => true;
        public void ReleaseConnectionSlot() => ReleasedConnections++;
        public void ReleasePendingSlot() { }
    }
}




