using Microsoft.Extensions.Logging;
using PeerSharp.Internals;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Utilities;
using PeerSharp.Exceptions;
using PeerSharp.Messages;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace PeerSharp.Tests.Core.Peers;

public class PeerManagerTests
{
    [Fact]
    public async Task MetadataRebuild_AdoptsSameLivePeerAndPreservesGovernorSlot()
    {
        var ctx = CreateContext();
        var peer = new PeerCommunication(ctx.Torrent, new TestPeerListener(), TimeProvider.System)
        {
            Connected = 1,
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.80"), 51413)
        };
        peer.Stream = new MemoryStream();
        Stream retainedStream = peer.Stream;
        SetPrivateProperty(peer, "PeerPieces", new PiecesProgress(0));
        await (Task)InvokePrivate(peer, "ProcessMessageAsync", new PeerMessage(MessageId.HaveAll))!;
        Assert.True(ctx.Governor.TryAcquireConnectionSlot());
        ctx.Manager.AddConnectedPeerForTesting(peer);

        IReadOnlyList<PeerCommunication> detached =
            await ctx.Manager.DetachConnectedPeersForMetadataRebuildAsync();
        var replacement = new PeerManager(
            ctx.Torrent,
            new TorrentTestUtility.MockGeoIpService(),
            new RealPeerFactory(),
            TimeProvider.System,
            ctx.Governor);
        int adopted = await replacement.AdoptPeersAfterMetadataRebuildAsync(detached);

        Assert.Equal(1, adopted);
        Assert.Equal(0, ctx.Manager.ConnectedCount);
        Assert.Equal(1, replacement.ConnectedCount);
        Assert.Same(peer, Assert.Single(replacement.GetConnectedPeersInternal()));
        Assert.Same(retainedStream, peer.Stream);
        Assert.Same(replacement, peer.Listener);
        Assert.Equal(ctx.Torrent.Pieces.Count, peer.PeerPieces.Count);
        Assert.True(peer.PeerPieces.IsFull);
        Assert.Equal(0, ctx.Governor.ReleasedConnections);

        await peer.CloseAsync();
        Assert.Equal(1, ctx.Governor.ReleasedConnections);
        await replacement.StopAsync();
        await CleanupAsync(ctx);
    }

    [Fact]
    public async Task MetadataRebuild_InvalidSavedBitfield_ClosesPeerAndReleasesGovernorSlot()
    {
        var ctx = CreateContext();
        var peer = new PeerCommunication(ctx.Torrent, new TestPeerListener(), TimeProvider.System)
        {
            Connected = 1,
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.81"), 51413)
        };
        SetPrivateProperty(peer, "PeerPieces", new PiecesProgress(0));
        await (Task)InvokePrivate(
            peer,
            "ProcessMessageAsync",
            new PeerMessage(MessageId.Bitfield) { Data = [0x80, 0x00] })!;
        Assert.True(ctx.Governor.TryAcquireConnectionSlot());
        ctx.Manager.AddConnectedPeerForTesting(peer);

        IReadOnlyList<PeerCommunication> detached =
            await ctx.Manager.DetachConnectedPeersForMetadataRebuildAsync();
        var replacement = new PeerManager(
            ctx.Torrent,
            new TorrentTestUtility.MockGeoIpService(),
            new RealPeerFactory(),
            TimeProvider.System,
            ctx.Governor);
        int adopted = await replacement.AdoptPeersAfterMetadataRebuildAsync(detached);

        Assert.Equal(0, adopted);
        Assert.Equal(0, replacement.ConnectedCount);
        Assert.Equal(0, peer.Connected);
        Assert.Equal(1, ctx.Governor.ReleasedConnections);

        await replacement.StopAsync();
        await CleanupAsync(ctx);
    }

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
    public async Task AddIncomingPeerAsync_BadDataAddress_ReleasesProvisionalSlot()
    {
        var ctx = CreateContext(new PeerCommunicationFactory());
        var remote = new IPEndPoint(IPAddress.Parse("203.0.113.40"), 50000);
        var offender = new PeerCommunication(ctx.Torrent, new TestPeerListener(), TimeProvider.System)
        {
            RemoteEndPoint = remote
        };

        Assert.False(ctx.Manager.RecordHashFailure(offender));
        Assert.False(ctx.Manager.RecordHashFailure(offender));
        Assert.True(ctx.Manager.RecordHashFailure(offender));

        using var stream = new MemoryStream();
        byte[] handshake = BuildHandshake(ctx.Torrent.InfoFile.Info.Hash.Span, ctx.Torrent.Settings.PeerId);
        await ctx.Manager.AddIncomingPeerAsync(stream, handshake, remote);

        Assert.Equal(0, ctx.Manager.ConnectedCount);
        Assert.Equal(1, ctx.Governor.AcquiredConnections);
        Assert.Equal(1, ctx.Governor.ReleasedConnections);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task AddIncomingTcpPeerAsync_BadDataAddress_IsRejectedBeforeTakingSlot()
    {
        var ctx = CreateContext(new PeerCommunicationFactory());
        var offender = new PeerCommunication(ctx.Torrent, new TestPeerListener(), TimeProvider.System)
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 6881)
        };

        ctx.Manager.RecordHashFailure(offender);
        ctx.Manager.RecordHashFailure(offender);
        ctx.Manager.RecordHashFailure(offender);

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
        ctx.Manager.MarkPeerSlowForTesting(peer, Environment.TickCount64 - 10_000);

        await ctx.Manager.CheckPeerHealthForTestingAsync();

        Assert.Equal(0, ctx.Manager.SlowPeerCountForTesting);

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

        int seconds = ctx.Manager.GetOptimisticUnchokeIntervalSecondsForTesting();
        Assert.Equal(5, seconds);

        ctx.Torrent.Settings.Connection.OptimisticUnchokeIntervalSeconds = 30;
        seconds = ctx.Manager.GetOptimisticUnchokeIntervalSecondsForTesting();
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
        int slots = ctx.Manager.GetUploadSlotsForTesting();
        Assert.Equal(4, slots);

        // With 6 connected peers, min<=slots<=max
        SetPrivateField(ctx.Manager, "_connectedPeersCount", 6);
        slots = ctx.Manager.GetUploadSlotsForTesting();
        Assert.Equal(6, slots);

        // With 12 connected peers, capped by max
        SetPrivateField(ctx.Manager, "_connectedPeersCount", 12);
        slots = ctx.Manager.GetUploadSlotsForTesting();
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

        int slots = ctx.Manager.GetUploadSlotsForTesting();
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

        ctx.Manager.SetGlobalUtpPenaltyForTesting(DateTimeOffset.UtcNow.AddMinutes(5));

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

        // isListenAddress is passed explicitly: reflection does not apply a parameter's default, so an
        // argument per parameter is required. True is the default and what every peer source implies.
        var h1 = (PeerHistory)InvokePrivate(ctx.Manager, "GetOrAddKnownPeerHistory", ep, true)!;
        var h2 = (PeerHistory)InvokePrivate(ctx.Manager, "GetOrAddKnownPeerHistory", ep, true)!;

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
    public async Task HandshakeFinishedAsync_InternalFailure_ClosesIncompletePeerAndTracksFailure()
    {
        var ctx = CreateContext();
        ctx.Torrent.Pieces.SetHaveAll();
        var peer = new FailingHandshakePeer(ctx.Torrent, ctx.Manager);

        await ctx.Manager.HandshakeFinishedAsync(peer);

        Assert.Equal(1, peer.CloseCalls);
        Assert.Equal(1, ctx.Manager.InternalFailureCountForTesting);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task InternalPeerManagerFailures_EscalateAfterThreshold()
    {
        var ctx = CreateContext();

        ctx.Manager.RecordInternalFailureForTesting("test-1", new InvalidOperationException("one"));
        ctx.Manager.RecordInternalFailureForTesting("test-2", new InvalidOperationException("two"));
        Assert.Null(ctx.Torrent.LastException);

        ctx.Manager.RecordInternalFailureForTesting("test-3", new InvalidOperationException("three"));

        Assert.Equal(3, ctx.Manager.InternalFailureCountForTesting);
        var error = Assert.IsType<TorrentException>(ctx.Torrent.LastException);
        Assert.Contains("3 internal failures", error.Message);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task FireAndForget_FaultedTasksAreTrackedAndEscalated()
    {
        var ctx = CreateContext();

        for (int i = 1; i <= 3; i++)
        {
            ctx.Manager.FireAndForgetForTesting(Task.FromException(new InvalidOperationException($"failure {i}")), $"test-{i}");
            int expected = i;
            await TorrentTestUtility.WaitUntilAsync(
                () => ctx.Manager.InternalFailureCountForTesting == expected,
                because: $"faulted fire-and-forget task {i} to be recorded");
        }

        Assert.IsType<TorrentException>(ctx.Torrent.LastException);
        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task InternalPeerManagerFailure_ContainsThrowingErrorSubscriber()
    {
        var ctx = CreateContext();
        ctx.Torrent.Events = new TorrentEventsBuilder()
            .OnError((_, _) => throw new InvalidOperationException("subscriber failed"))
            .Build();

        ctx.Manager.RecordInternalFailureForTesting("test-1", new InvalidOperationException());
        ctx.Manager.RecordInternalFailureForTesting("test-2", new InvalidOperationException());
        ctx.Manager.RecordInternalFailureForTesting("test-3", new InvalidOperationException());

        Assert.Equal(3, ctx.Manager.InternalFailureCountForTesting);
        Assert.IsType<TorrentException>(ctx.Torrent.LastException);
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

    /// <summary>
    /// A connection that connected cleanly and then achieved nothing must count against the peer, or
    /// the backoff never grows. Seeding is where this showed: two seeds have nothing for each other, so
    /// the connection completes, idles and closes, and a six minute run made 6298 outgoing connections
    /// to 1515 peers - several of them thirty-nine times - because every completed handshake reset the
    /// count that the backoff is calculated from.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ConnectionClosed_HavingExchangedNothing_BacksOffFurtherEachTime()
    {
        var ctx = CreateContext();
        var endpoint = new IPEndPoint(IPAddress.Parse("203.0.113.5"), 6881);
        var known = GetPrivateField<ConcurrentDictionary<IPEndPoint, PeerHistory>>(ctx.Manager, "_knownPeersCache");
        var connected = GetPrivateField<ConcurrentDictionary<PeerCommunication, byte>>(ctx.Manager, "_connectedPeers");

        for (int round = 1; round <= 3; round++)
        {
            var peer = new PeerCommunication(ctx.Torrent, new TestPeerListener(), TimeProvider.System)
            {
                RemoteEndPoint = endpoint,
                IsOutgoing = true
            };
            connected.TryAdd(peer, 0);
            // The count is maintained alongside the dictionary by the registration path this bypasses,
            // and closing decrements it - leave it at zero and teardown sizes a list from a negative.
            SetPrivateField(ctx.Manager, "_connectedPeersCount", 1);

            await ctx.Manager.ConnectionClosedAsync(peer, 0);

            Assert.True(known.TryGetValue(endpoint, out var history));
            Assert.Equal(round, history!.FruitlessConnectionCount);
            Assert.True(
                history.NextConnectAttempt > DateTimeOffset.UtcNow,
                $"Round {round} should have pushed the next attempt into the future.");
        }

        await CleanupAsync(ctx);
    }

    /// <summary>
    /// And the other direction: uploading is achieving something. Only a received piece used to mark a
    /// peer as having exchanged data, so a seeder that uploaded for an hour still had every peer on
    /// record as never having given it anything.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ConnectionClosed_AfterUploading_ClearsTheBackoff()
    {
        var ctx = CreateContext();
        var endpoint = new IPEndPoint(IPAddress.Parse("203.0.113.6"), 6881);
        var known = GetPrivateField<ConcurrentDictionary<IPEndPoint, PeerHistory>>(ctx.Manager, "_knownPeersCache");
        var connected = GetPrivateField<ConcurrentDictionary<PeerCommunication, byte>>(ctx.Manager, "_connectedPeers");

        known[endpoint] = new PeerHistory
        {
            EndPoint = endpoint,
            FruitlessConnectionCount = 4,
            NextConnectAttempt = DateTimeOffset.UtcNow.AddMinutes(5)
        };

        var peer = new PeerCommunication(ctx.Torrent, new TestPeerListener(), TimeProvider.System)
        {
            RemoteEndPoint = endpoint,
            IsOutgoing = true
        };
        SetPrivateField(peer, "_uploaded", 16384L);
        connected.TryAdd(peer, 0);
        SetPrivateField(ctx.Manager, "_connectedPeersCount", 1);

        await ctx.Manager.ConnectionClosedAsync(peer, 0);

        var history = known[endpoint];
        Assert.True(history.ExchangedData);
        Assert.Equal(0, history.FruitlessConnectionCount);
        Assert.Equal(DateTimeOffset.MinValue, history.NextConnectAttempt);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task ConnectionClosed_AfterMetadataExchange_ClearsTheBackoff()
    {
        var ctx = CreateContext();
        var endpoint = new IPEndPoint(IPAddress.Parse("203.0.113.42"), 6881);
        var known = GetPrivateField<ConcurrentDictionary<IPEndPoint, PeerHistory>>(ctx.Manager, "_knownPeersCache");
        var connected = GetPrivateField<ConcurrentDictionary<PeerCommunication, byte>>(ctx.Manager, "_connectedPeers");
        known[endpoint] = new PeerHistory
        {
            EndPoint = endpoint,
            FruitlessConnectionCount = 3,
            NextConnectAttempt = DateTimeOffset.UtcNow.AddMinutes(5)
        };

        var peer = new PeerCommunication(ctx.Torrent, new TestPeerListener(), TimeProvider.System)
        {
            RemoteEndPoint = endpoint,
            IsOutgoing = true
        };
        peer.MarkUsefulDataExchanged();
        connected.TryAdd(peer, 0);
        SetPrivateField(ctx.Manager, "_connectedPeersCount", 1);

        await ctx.Manager.ConnectionClosedAsync(peer, 0);

        Assert.Equal(0, known[endpoint].FruitlessConnectionCount);
        Assert.Equal(DateTimeOffset.MinValue, known[endpoint].NextConnectAttempt);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task ConnectionClosed_IncomingSourcePort_RemainsNonDialableWhenHistoryWasPruned()
    {
        var ctx = CreateContext();
        var endpoint = new IPEndPoint(IPAddress.Parse("203.0.113.41"), 50001);
        var known = GetPrivateField<ConcurrentDictionary<IPEndPoint, PeerHistory>>(ctx.Manager, "_knownPeersCache");
        var connected = GetPrivateField<ConcurrentDictionary<PeerCommunication, byte>>(ctx.Manager, "_connectedPeers");
        var peer = new PeerCommunication(ctx.Torrent, new TestPeerListener(), TimeProvider.System)
        {
            RemoteEndPoint = endpoint,
            IsOutgoing = false
        };

        // Model cache pruning while the connection is alive: the close path must recreate the entry
        // with the connection's provenance, not with GetOrAddKnownPeerHistory's dialable default.
        connected.TryAdd(peer, 0);
        SetPrivateField(ctx.Manager, "_connectedPeersCount", 1);

        await ctx.Manager.ConnectionClosedAsync(peer, 0);

        Assert.True(known.TryGetValue(endpoint, out var history));
        Assert.False(history!.IsListenAddress);
        Assert.Equal(0, history.FruitlessConnectionCount);

        await CleanupAsync(ctx);
    }

    /// <summary>
    /// Bad data has to be remembered against the source, not the connection it arrived on. Counting it
    /// on the connection made dropping the peer pointless - it reconnects, the count is gone, and it
    /// goes straight back into the rotation. In a live run one piece failed its hash thirteen times
    /// without a single peer ever being dropped for it.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task HashFailures_AreCountedAgainstTheAddress_NotTheConnection()
    {
        var ctx = CreateContext();
        var address = IPAddress.Parse("203.0.113.9");

        var first = new PeerCommunication(ctx.Torrent, new TestPeerListener(), TimeProvider.System)
        {
            RemoteEndPoint = new IPEndPoint(address, 6881)
        };

        Assert.False(ctx.Manager.RecordHashFailure(first));
        Assert.False(ctx.Manager.RecordHashFailure(first));
        Assert.True(ctx.Manager.RecordHashFailure(first));

        // The same source coming back on a different port, which is what a reconnect looks like and
        // what an incoming connection looks like every time. It must not arrive with a clean slate.
        var reconnected = new PeerCommunication(ctx.Torrent, new TestPeerListener(), TimeProvider.System)
        {
            RemoteEndPoint = new IPEndPoint(address, 51413)
        };

        Assert.True(ctx.Manager.RecordHashFailure(reconnected));

        // And an unrelated peer is unaffected - the count is per source, not a global mood.
        var innocent = new PeerCommunication(ctx.Torrent, new TestPeerListener(), TimeProvider.System)
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.10"), 6881)
        };

        Assert.False(ctx.Manager.RecordHashFailure(innocent));

        await CleanupAsync(ctx);
    }

    /// <summary>
    /// A relay that has hit the holepunch limit goes on asking, so reporting each refusal reported the
    /// same working limit over and over: a nine minute run produced several hundred warnings, twenty of
    /// them inside one second. The limit is worth a warning; each rendezvous it turns away is not.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task HolepunchRateLimit_WarnsOncePerWindow_NotOncePerRefusal()
    {
        var captured = new Interop.CapturingLoggerProvider(LogLevel.Debug);
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(captured);
        });

        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = ProtocolConstants.BlockSize;
        var torrent = TorrentTestUtility.CreateMinimal(metadata, CreateTempPath());

        // Zero, so every rendezvous is refused and none is dialled: this is about what gets reported,
        // and a test of that should not open sockets to find out.
        torrent.Settings.Connection.MaxHolepunchPerMinute = 0;

        var governor = new FakeConnectionGovernor();
        var manager = new PeerManager(
            torrent,
            new TorrentTestUtility.MockGeoIpService(),
            new RealPeerFactory(),
            TimeProvider.System,
            governor,
            loggerFactory.CreateLogger<PeerManager>());

        for (int i = 1; i <= 25; i++)
        {
            manager.ConnectTo($"192.0.2.{i}", 6881, forceUtp: true);
        }

        Assert.Equal(1, captured.CountMatching("Holepunch rate limit"));

        await manager.StopAsync();
        await manager.DisposeAsync();
    }

    /// <summary>
    /// Port 0 is not a port anything listens on, so an address carrying it is malformed rather than
    /// unreachable. Dialling one is not a cheap mistake: uTP has nothing to refuse the connection and
    /// waits out the whole timeout, and a live run spent 1471 attempts on 519 such addresses.
    /// </summary>
    [Theory(Timeout = 30000)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public async Task ConnectTo_APortNoPeerCanListenOn_IsNotDialled(int port)
    {
        var ctx = CreateContext();
        var pending = GetPrivateField<ConcurrentDictionary<IPEndPoint, long>>(ctx.Manager, "_pendingConnections");

        ctx.Manager.ConnectTo("203.0.113.9", port);

        Assert.Empty(pending);

        await CleanupAsync(ctx);
    }

    /// <summary>
    /// The same address arrives through the peer sources too, and one that can never be dialled must
    /// not take a place in a cache bounded at <see cref="Settings.MaxKnownPeersCache"/>.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AddPeers_APortNoPeerCanListenOn_IsNotEvenRemembered()
    {
        var ctx = CreateContext();
        var cache = GetPrivateField<ConcurrentDictionary<IPEndPoint, PeerHistory>>(ctx.Manager, "_knownPeersCache");
        var unusable = new IPEndPoint(IPAddress.Parse("203.0.113.10"), 0);
        var usable = new IPEndPoint(IPAddress.Parse("203.0.113.11"), 6881);

        ctx.Manager.AddPeers([unusable, usable], PeerSourceKind.Tracker);

        Assert.DoesNotContain(unusable, cache.Keys);
        Assert.Contains(usable, cache.Keys);

        await CleanupAsync(ctx);
    }

    /// <summary>
    /// Two seeds have nothing to trade. The peer hangs up within a fraction of a second of the
    /// handshake, so the backoff sees one more failed dial and the candidate returns as soon as the
    /// pool cycles - a live seeding run handshaked seeds 9363 times, one of them 26 times in ten
    /// minutes. Being complete is the whole of the reason, so the moment this torrent wants pieces
    /// again a seed is the best peer available and must be dialled.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ConnectTo_AKnownSeed_IsSkippedOnlyWhileThisTorrentIsAlsoComplete()
    {
        var ctx = CreateContext();
        ctx.Torrent.InfoFile.Info.Pieces.Add(new byte[20]);
        Assert.True(ctx.Torrent.HasMetadata);
        var endpoint = new IPEndPoint(IPAddress.Parse("203.0.113.12"), 6881);
        var pending = GetPrivateField<ConcurrentDictionary<IPEndPoint, long>>(ctx.Manager, "_pendingConnections");

        var history = (PeerHistory)InvokePrivate(ctx.Manager, "GetOrAddKnownPeerHistory", endpoint, true)!;
        history.SeedConfirmed = true;

        Assert.False(ctx.Torrent.Finished);
        ctx.Manager.ConnectTo(endpoint.Address.ToString(), endpoint.Port);
        Assert.Contains(endpoint, pending.Keys);

        // Cleared because a second dial to an already-pending endpoint is refused for that reason
        // alone, which would pass this test whether or not the seed rule exists.
        pending.Clear();

        ctx.Torrent.Pieces.AddPiece(0);
        Assert.True(ctx.Torrent.Finished);
        ctx.Manager.ConnectTo(endpoint.Address.ToString(), endpoint.Port);
        Assert.Empty(pending);

        await CleanupAsync(ctx);
    }

    /// <summary>
    /// The same empty piece collection that makes a magnet look Finished must not make its peers look
    /// useless. Most peers in a magnet's swarm are seeds and announce it with have_all the moment they
    /// handshake, and those are precisely the peers holding the metadata being fetched - refusing to
    /// dial them would leave the fetch with nowhere to go.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ConnectTo_AConfirmedSeed_IsStillDialledBeforeMetadataArrives()
    {
        var ctx = CreateContext(hasPieceCount: false);
        var endpoint = new IPEndPoint(IPAddress.Parse("203.0.113.17"), 6881);
        var pending = GetPrivateField<ConcurrentDictionary<IPEndPoint, long>>(ctx.Manager, "_pendingConnections");

        var history = (PeerHistory)InvokePrivate(ctx.Manager, "GetOrAddKnownPeerHistory", endpoint, true)!;
        history.SeedConfirmed = true;

        Assert.False(ctx.Torrent.HasMetadata);
        Assert.False(ctx.Torrent.Finished);

        ctx.Manager.ConnectTo(endpoint.Address.ToString(), endpoint.Port);
        Assert.Contains(endpoint, pending.Keys);

        await CleanupAsync(ctx);
    }

    /// <summary>
    /// BEP 11 carries a seed flag, so a peer can be marked one on a stranger's word. That is fine for
    /// ranking a candidate and not fine for refusing to dial it: a peer nobody dials never gets to
    /// correct the record, so one mistaken or malicious PEX message would otherwise be enough to flag
    /// a swarm and end this client's seeding.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ConnectTo_ASeedKnownOnlyFromPexFlags_IsStillDialled()
    {
        var ctx = CreateContext();
        ctx.Torrent.InfoFile.Info.Pieces.Add(new byte[20]);
        Assert.True(ctx.Torrent.HasMetadata);
        var endpoint = new IPEndPoint(IPAddress.Parse("203.0.113.13"), 6881);
        var pending = GetPrivateField<ConcurrentDictionary<IPEndPoint, long>>(ctx.Manager, "_pendingConnections");

        var history = (PeerHistory)InvokePrivate(ctx.Manager, "GetOrAddKnownPeerHistory", endpoint, true)!;
        PeerExchangeCoordinator.ApplyFlags(history, (byte)UtPex.Peer.Seed);
        Assert.True(history.IsSeed);
        Assert.False(history.SeedConfirmed);

        ctx.Torrent.Pieces.AddPiece(0);
        Assert.True(ctx.Torrent.Finished);

        ctx.Manager.ConnectTo(endpoint.Address.ToString(), endpoint.Port);
        Assert.Contains(endpoint, pending.Keys);

        await CleanupAsync(ctx);
    }

    /// <summary>
    /// Half-open connections are capped by the deficit once there is nothing left to download, and by
    /// nothing but the setting while there is. Throttling the hunt for peers is what makes a download
    /// ramp slowly, and a finished torrent is not hunting.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task MaxPendingConnections_IsBoundedByTheDeficitOnlyWhenComplete()
    {
        var ctx = CreateContext();
        ctx.Torrent.InfoFile.Info.Pieces.Add(new byte[20]);
        int configured = ctx.Torrent.Settings.Connection.MaxPendingConnections;
        int target = ctx.Torrent.Settings.Connection.MaxPeersPerTorrent;

        Assert.True(ctx.Torrent.HasMetadata);
        Assert.False(ctx.Torrent.Finished);
        Assert.Equal(configured, InvokePrivate(ctx.Manager, "MaxPendingConnectionsNow", 0));
        Assert.Equal(configured, InvokePrivate(ctx.Manager, "MaxPendingConnectionsNow", target - 1));

        ctx.Torrent.Pieces.AddPiece(0);
        Assert.True(ctx.Torrent.Finished);
        Assert.Equal(target, InvokePrivate(ctx.Manager, "MaxPendingConnectionsNow", 0));
        Assert.Equal(1, InvokePrivate(ctx.Manager, "MaxPendingConnectionsNow", target - 1));
        Assert.Equal(0, InvokePrivate(ctx.Manager, "MaxPendingConnectionsNow", target));
        Assert.Equal(0, InvokePrivate(ctx.Manager, "MaxPendingConnectionsNow", target + 10));

        await CleanupAsync(ctx);
    }

    /// <summary>
    /// A magnet has no piece count until metadata arrives, so its empty piece collection happens to
    /// satisfy Finished. It is nevertheless still hunting for peers and must retain download-time
    /// connection depth so it can find one that supplies the metadata.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task MaxPendingConnections_WithoutMetadata_UsesTheConfiguredDownloadDepth()
    {
        var ctx = CreateContext(hasPieceCount: false);
        int configured = ctx.Torrent.Settings.Connection.MaxPendingConnections;

        // Finished is false precisely because there is no metadata - the piece collection is empty,
        // so "received every piece" would otherwise be satisfied by a torrent that does not yet know
        // what it is downloading.
        Assert.False(ctx.Torrent.HasMetadata);
        Assert.False(ctx.Torrent.Finished);
        Assert.Equal(configured, InvokePrivate(ctx.Manager, "MaxPendingConnectionsNow", 0));

        await CleanupAsync(ctx);
    }

    /// <summary>
    /// A batch is queued faster than its entries become half-open, so the completed-torrent deficit
    /// has to be checked by the queue reader as well as by the producer.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ConnectionQueue_WhenComplete_StartsNoMoreThanThePeerDeficit()
    {
        var factory = new BlockingPeerFactory();
        var ctx = CreateContext(factory);
        ctx.Torrent.InfoFile.Info.Pieces.Add(new byte[20]);
        ctx.Torrent.Settings.Connection.ConnectionsPerSecond = 1000;
        ctx.Torrent.Settings.Connection.MaxPeersPerTorrent = 1;
        ctx.Torrent.Pieces.AddPiece(0);

        var pending = GetPrivateField<ConcurrentDictionary<IPEndPoint, long>>(ctx.Manager, "_pendingConnections");
        var first = new IPEndPoint(IPAddress.Parse("203.0.113.14"), 6881);
        var second = new IPEndPoint(IPAddress.Parse("203.0.113.15"), 6881);

        // Queue both before the reader starts. At producer time neither is half-open, so both pass
        // the early check; only the dequeue-time check can see the first attempt occupying the slot.
        ctx.Manager.ConnectTo(first.Address.ToString(), first.Port);
        ctx.Manager.ConnectTo(second.Address.ToString(), second.Port);
        Assert.Equal(2, pending.Count);

        await ctx.Manager.StartAsync();
        await TorrentTestUtility.WaitUntilAsync(
            () => factory.Created.Count == 1 && !pending.ContainsKey(second),
            because: "the second queued request to be declined once the only half-open slot is occupied");

        Assert.Single(factory.Created);

        factory.CompleteAll(false);
        await CleanupAsync(ctx);
    }

    /// <summary>
    /// Completion can happen while a request waits in the rate-limited queue. A confirmed seed that
    /// was useful when queued must be discarded rather than dialled after that transition.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ConnectionQueue_AConfirmedSeedQueuedBeforeCompletion_IsRevalidatedBeforeDial()
    {
        var factory = new BlockingPeerFactory();
        var ctx = CreateContext(factory);
        ctx.Torrent.InfoFile.Info.Pieces.Add(new byte[20]);
        ctx.Torrent.Settings.Connection.ConnectionsPerSecond = 1000;
        var endpoint = new IPEndPoint(IPAddress.Parse("203.0.113.16"), 6881);
        var pending = GetPrivateField<ConcurrentDictionary<IPEndPoint, long>>(ctx.Manager, "_pendingConnections");

        var history = (PeerHistory)InvokePrivate(ctx.Manager, "GetOrAddKnownPeerHistory", endpoint, true)!;
        history.SeedConfirmed = true;

        ctx.Manager.ConnectTo(endpoint.Address.ToString(), endpoint.Port);
        Assert.Contains(endpoint, pending.Keys);

        ctx.Torrent.Pieces.AddPiece(0);
        await ctx.Manager.StartAsync();
        await TorrentTestUtility.WaitUntilAsync(
            () => !pending.ContainsKey(endpoint),
            because: "the now-useless queued seed request to be discarded");

        Assert.Empty(factory.Created);

        await CleanupAsync(ctx);
    }

    /// <summary>
    /// The point of the alert is to keep what the live-peer snapshot loses when a peer goes away, and
    /// the endpoint is the one field that is not lost - it is already on the record. A client name has
    /// to come from the peer ID, the same way <see cref="PeerInfo.ClientName"/> gets it.
    /// </summary>
    [Fact]
    public async Task PeerDisconnected_ReportsTheClientNameAndNotTheEndpointAgain()
    {
        var alerts = new RecordingAlertsManager();
        var ctx = CreateContext(alerts: alerts);
        var endpoint = new IPEndPoint(IPAddress.Parse("203.0.113.90"), 51413);
        var peer = new PeerCommunication(ctx.Torrent, new TestPeerListener(), TimeProvider.System)
        {
            Connected = 1,
            RemoteEndPoint = endpoint
        };
        System.Text.Encoding.ASCII.GetBytes("-qB4550-abcdefghijkl").CopyTo(peer.PeerId, 0);
        Assert.True(ctx.Governor.TryAcquireConnectionSlot());
        ctx.Manager.AddConnectedPeerForTesting(peer);

        await ctx.Manager.ConnectionClosedAsync(peer, 7);

        var alert = Assert.Single(alerts.Alerts.OfType<PeerDisconnectedAlert>());
        Assert.Equal(endpoint, alert.Endpoint);
        Assert.Equal(7, alert.ReasonCode);
        Assert.Equal(ClientIdentification.GetClientName(peer.PeerId), alert.ClientName);
        Assert.NotEqual(endpoint.ToString(), alert.ClientName);

        await CleanupAsync(ctx);
    }

    private sealed class RecordingAlertsManager : IAlertsManager
    {
        public List<Alert> Alerts { get; } = [];
        public void PostAlert(Alert alert) => Alerts.Add(alert);
        public void MetadataAlert(AlertId id, ITorrent torrent) { }
        public void MetadataProgressAlert(ITorrent torrent, float progress, int receivedPieces, int totalPieces) { }
        public void TorrentAlert(AlertId id, ITorrent torrent) { }
        public void ConfigAlert(AlertId id, string configType) { }
        public void PieceCompletedAlert(ITorrent torrent, int pieceIndex, int completedPieces, int totalPieces) { }
        public void ProgressChangedAlert(ITorrent torrent, float progress, float selectionProgress, ulong finishedBytes, ulong totalBytes, int completedPieces, int totalPieces) { }
        public void TransferStatsAlert(ITorrent torrent, long downloaded, long uploaded, long downloadSpeed, long uploadSpeed, int connectedPeers) { }
        public void StateChangedAlert(ITorrent torrent, TorrentState previousState, TorrentState newState) { }
        public void TorrentErrorAlert(ITorrent torrent, Exception exception) { }
        public void RegisterAlerts(uint categories) { }
        public List<Alert> PopAlerts() => [];
        public async IAsyncEnumerable<Alert> GetAlertsAsync(
            TimeSpan? timeout = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private static PeerManagerContext CreateContext(
        IPeerCommunicationFactory? peerFactory = null,
        bool hasPieceCount = true,
        IAlertsManager? alerts = null)
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Version = TorrentVersion.V1;
        metadata.Info.Hash = new InfoHash(Enumerable.Range(0, 20).Select(i => (byte)i).ToArray());
        metadata.Info.PieceSize = ProtocolConstants.BlockSize;
        if (hasPieceCount)
        {
            metadata.Info.FullSize = ProtocolConstants.BlockSize;
            metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "file.bin", Size = ProtocolConstants.BlockSize, Offset = 0 });
        }

        string path = CreateTempPath();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path, alerts: alerts);
        torrent.Settings.Connection.Encryption = Encryption.Refuse;

        var governor = new FakeConnectionGovernor();
        var manager = new PeerManager(
            torrent,
            new TorrentTestUtility.MockGeoIpService(),
            peerFactory ?? new RealPeerFactory(),
            TimeProvider.System,
            governor);

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

    private sealed class FailingHandshakePeer : PeerCommunication
    {
        public FailingHandshakePeer(Torrent torrent, IPeerListener listener)
            : base(torrent, listener, TimeProvider.System)
        {
        }

        public int CloseCalls { get; private set; }

        public override Task SendMessageAsync(PeerMessage msg) => Task.FromException(new InvalidOperationException("send failed"));

        public override Task CloseAsync(string? closedBy = null)
        {
            CloseCalls++;
            return Task.CompletedTask;
        }
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

    private sealed class BlockingPeerFactory : IPeerCommunicationFactory
    {
        public ConcurrentQueue<BlockingPeer> Created { get; } = new();

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider)
        {
            var peer = new BlockingPeer(torrent, listener, timeProvider);
            Created.Enqueue(peer);
            return peer;
        }

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, Stream stream, IPEndPoint? remoteEndPoint)
            => throw new NotSupportedException();

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, TcpClient client)
            => throw new NotSupportedException();

        public void CompleteAll(bool result)
        {
            foreach (var peer in Created)
            {
                peer.ConnectResult.TrySetResult(result);
            }
        }
    }

    private sealed class BlockingPeer : PeerCommunication
    {
        public BlockingPeer(Torrent torrent, IPeerListener listener, TimeProvider timeProvider)
            : base(torrent, listener, timeProvider)
        {
        }

        public TaskCompletionSource<bool> ConnectResult { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<bool> ConnectAsync(
            string ip,
            int port,
            bool useUtp,
            int timeoutMs,
            bool offerEncryption = true,
            CancellationToken ct = default)
            => ConnectResult.Task;
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
