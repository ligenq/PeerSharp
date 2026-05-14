using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using PeerSharp.Messages;

namespace PeerSharp.Tests.Core.Peers;

public class PeerCommunicationTests
{
    [Fact]
    public async Task SetHandshakeReceivedAsync_V2TruncatedHash_SetsFlagsAndPeerId()
    {
        var metadata = CreateMetadataV2();
        string path = CreateTempPath();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);
        var listener = new TestPeerListener();
        var peer = new PeerCommunication(torrent, listener, TimeProvider.System);

        byte[] peerId = Enumerable.Range(0, 20).Select(i => (byte)(200 + i)).ToArray();
        byte[] handshake = BuildHandshake(metadata.Info.HashV2.Span[..20], peerId, supportsExtensions: true, supportsFast: true, supportsV2: true);

        bool ok = await peer.SetHandshakeReceivedAsync(handshake);

        Assert.True(ok);
        Assert.True(peer.RemoteSupportsExtensions);
        Assert.True(peer.RemoteSupportsFastExtension);
        Assert.True(peer.RemoteSupportsV2);
        Assert.Equal(peerId, peer.PeerId);

        await torrent.DisposeAsync();
        CleanupPath(path);
    }

    [Fact]
    public async Task SetHandshakeReceivedAsync_InvalidHash_ReturnsFalse()
    {
        var metadata = CreateMetadataV1();
        string path = CreateTempPath();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);
        var listener = new TestPeerListener();
        var peer = new PeerCommunication(torrent, listener, TimeProvider.System);

        byte[] peerId = new byte[20];
        byte[] wrongHash = Enumerable.Repeat((byte)0xAA, 20).ToArray();
        byte[] handshake = BuildHandshake(wrongHash, peerId, supportsExtensions: false, supportsFast: false, supportsV2: false);

        bool ok = await peer.SetHandshakeReceivedAsync(handshake);
        Assert.False(ok);

        await torrent.DisposeAsync();
        CleanupPath(path);
    }

    [Fact]
    public async Task SetHandshakeReceivedAsync_InvalidProtocol_ReturnsFalse()
    {
        var metadata = CreateMetadataV1();
        string path = CreateTempPath();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);
        var listener = new TestPeerListener();
        var peer = new PeerCommunication(torrent, listener, TimeProvider.System);

        byte[] handshake = BuildHandshake(metadata.Info.Hash.Span, new byte[20], supportsExtensions: false, supportsFast: false, supportsV2: false);
        handshake[1] = (byte)'X';

        bool ok = await peer.SetHandshakeReceivedAsync(handshake);

        Assert.False(ok);

        await torrent.DisposeAsync();
        CleanupPath(path);
    }

    [Fact]
    public async Task GetOptimalPipelineDepth_UsesEstimatedBandwidthWhenNoSpeed()
    {
        var metadata = CreateMetadataV1();
        string path = CreateTempPath();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);
        torrent.Settings.Transfer.EstimatedBandwidthBytesPerSec = 10 * 1024 * 1024;
        torrent.Settings.Transfer.EstimatedRttMs = 50;

        var peer = new PeerCommunication(torrent, new TestPeerListener(), TimeProvider.System);

        int depth = peer.GetOptimalPipelineDepth();

        long estimatedBytesInFlight = (long)torrent.Settings.Transfer.EstimatedBandwidthBytesPerSec * torrent.Settings.Transfer.EstimatedRttMs / 1000;
        long expected = (estimatedBytesInFlight * 3 / 2) / (16 * 1024);
        int clamped = (int)Math.Clamp(expected, 16, 250);

        Assert.Equal(clamped, depth);

        await torrent.DisposeAsync();
        CleanupPath(path);
    }

    [Fact]
    public async Task GetAdaptivePipelineDepth_ReducesForStrikesAndHighRtt()
    {
        var metadata = CreateMetadataV1();
        string path = CreateTempPath();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);

        var peer = new PeerCommunication(torrent, new TestPeerListener(), TimeProvider.System);

        SetPrivateProperty(peer, "DownloadSpeed", 4 * 1024 * 1024);
        SetPrivateField(peer, "_smoothedRttMs", 100);

        int baseDepth = peer.GetOptimalPipelineDepth();

        peer.Strikes = 2;
        int reduced = peer.GetAdaptivePipelineDepth();
        Assert.Equal(Math.Max(ProtocolConstants.MinPipelineDepth, baseDepth - 20), reduced);

        SetPrivateField(peer, "_smoothedRttMs", 900);
        int highRttOptimal = peer.GetOptimalPipelineDepth();
        int expectedHighRtt = Math.Max(ProtocolConstants.MinPipelineDepth, Math.Max(ProtocolConstants.MinPipelineDepth, highRttOptimal - 20) / 2);
        int highRtt = peer.GetAdaptivePipelineDepth();
        Assert.Equal(expectedHighRtt, highRtt);

        await torrent.DisposeAsync();
        CleanupPath(path);
    }

    [Fact]
    public async Task AllowedFastPiece_AddedAndQueriedThreadSafely()
    {
        var torrent = TorrentTestUtility.CreateMinimal(CreateMetadataV1(), CreateTempPath());
        var peer = new PeerCommunication(torrent, new TestPeerListener(), TimeProvider.System);

        Assert.False(peer.IsAllowedFast(7));
        Assert.Equal(0, peer.AllowedFastCount);
        Assert.Empty(peer.GetAllowedFastPieces());

        InvokePrivate<object?>(peer, "AddAllowedFastPiece", 7);
        InvokePrivate<object?>(peer, "AddAllowedFastPiece", 9);
        InvokePrivate<object?>(peer, "AddAllowedFastPiece", 7); // duplicate ignored

        Assert.True(peer.IsAllowedFast(7));
        Assert.True(peer.IsAllowedFast(9));
        Assert.False(peer.IsAllowedFast(8));
        Assert.Equal(2, peer.AllowedFastCount);

        var snapshot = peer.GetAllowedFastPieces();
        Assert.Contains(7, snapshot);
        Assert.Contains(9, snapshot);

        // Snapshot is reused until invalidated
        var snapshot2 = peer.GetAllowedFastPieces();
        Assert.Same(snapshot, snapshot2);

        // New addition invalidates the cache
        InvokePrivate<object?>(peer, "AddAllowedFastPiece", 11);
        var snapshot3 = peer.GetAllowedFastPieces();
        Assert.NotSame(snapshot, snapshot3);
        Assert.Contains(11, snapshot3);

        await torrent.DisposeAsync();
    }

    [Fact]
    public async Task SuggestedPiece_StoredAndReturnedFromSnapshot()
    {
        var torrent = TorrentTestUtility.CreateMinimal(CreateMetadataV1(), CreateTempPath());
        var peer = new PeerCommunication(torrent, new TestPeerListener(), TimeProvider.System);

        Assert.Empty(peer.GetSuggestedPieces());

        InvokePrivate<object?>(peer, "AddSuggestedPiece", 4);
        InvokePrivate<object?>(peer, "AddSuggestedPiece", 5);

        var snapshot = peer.GetSuggestedPieces();
        Assert.Equal(new[] { 4, 5 }, snapshot);

        // Snapshot reuse until cache invalidates
        Assert.Same(snapshot, peer.GetSuggestedPieces());

        InvokePrivate<object?>(peer, "AddSuggestedPiece", 6);
        Assert.NotSame(snapshot, peer.GetSuggestedPieces());

        await torrent.DisposeAsync();
    }

    [Fact]
    public async Task IncrementStrikes_IsAtomicAndCumulative()
    {
        var torrent = TorrentTestUtility.CreateMinimal(CreateMetadataV1(), CreateTempPath());
        var peer = new PeerCommunication(torrent, new TestPeerListener(), TimeProvider.System);

        peer.IncrementStrikes();
        peer.IncrementStrikes();
        peer.IncrementStrikes();

        Assert.Equal(3, peer.Strikes);

        peer.Strikes = 0;
        Assert.Equal(0, peer.Strikes);

        await torrent.DisposeAsync();
    }

    [Fact]
    public async Task RecordRtt_AppliesExponentialMovingAverageAndClamps()
    {
        var torrent = TorrentTestUtility.CreateMinimal(CreateMetadataV1(), CreateTempPath());
        var peer = new PeerCommunication(torrent, new TestPeerListener(), TimeProvider.System);

        // Initial smoothed RTT is 100ms, EMA: new = (old*7 + sample) / 8
        Assert.Equal(100, peer.SmoothedRttMs);

        peer.RecordRtt(200);
        Assert.Equal(((100 * 7) + 200) / 8, peer.SmoothedRttMs); // 112

        peer.RecordRtt(50);
        Assert.Equal(((112 * 7) + 50) / 8, peer.SmoothedRttMs); // 104

        // Lower clamp to 10ms (impossible to fall below even when sample = 0 from start of 10)
        SetPrivateField(peer, "_smoothedRttMs", 10);
        peer.RecordRtt(0);
        Assert.True(peer.SmoothedRttMs >= 10);

        // Upper clamp to 5000ms even with extreme sample
        SetPrivateField(peer, "_smoothedRttMs", 5000);
        peer.RecordRtt(int.MaxValue);
        Assert.True(peer.SmoothedRttMs <= 5000);

        await torrent.DisposeAsync();
    }

    [Fact]
    public async Task Choke_RespectsCooldownAndIsIdempotent()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2030-01-01T00:00:00Z"));
        var torrent = TorrentTestUtility.CreateMinimal(CreateMetadataV1(), CreateTempPath());
        var peer = new PeerCommunication(torrent, new TestPeerListener(), time);

        // Initially choking; calling Choke() again is a no-op (no state change).
        peer.Choke();
        Assert.True(peer.AmChoking);

        // Unchoke transitions state - but cooldown isn't enforced on the first transition
        peer.Unchoke();
        Assert.False(peer.AmChoking);

        // Choke immediately should be blocked by the 10s cooldown
        peer.Choke();
        Assert.False(peer.AmChoking);

        // Advance past cooldown - choke should now succeed
        time.Advance(TimeSpan.FromSeconds(11));
        peer.Choke();
        Assert.True(peer.AmChoking);

        await torrent.DisposeAsync();
    }

    [Fact]
    public async Task Unchoke_ReturnsEarlyWhenAlreadyUnchoked()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2030-01-01T00:00:00Z"));
        var torrent = TorrentTestUtility.CreateMinimal(CreateMetadataV1(), CreateTempPath());
        var peer = new PeerCommunication(torrent, new TestPeerListener(), time);

        peer.Unchoke();
        time.Advance(TimeSpan.FromSeconds(60)); // Bypass any cooldown

        // Already unchoked, second call should be a no-op
        peer.Unchoke();
        Assert.False(peer.AmChoking);

        await torrent.DisposeAsync();
    }

    [Fact]
    public async Task AddDownloadedAndUploaded_AreCumulative()
    {
        var torrent = TorrentTestUtility.CreateMinimal(CreateMetadataV1(), CreateTempPath());
        var peer = new PeerCommunication(torrent, new TestPeerListener(), TimeProvider.System);

        peer.AddDownloaded(123);
        peer.AddDownloaded(456);
        peer.AddUploaded(7);
        peer.AddUploaded(11);

        Assert.Equal(579, peer.Downloaded);
        Assert.Equal(18, peer.Uploaded);

        await torrent.DisposeAsync();
    }

    [Fact]
    public async Task AssignBandwidth_NoOpDoesNotThrow()
    {
        var torrent = TorrentTestUtility.CreateMinimal(CreateMetadataV1(), CreateTempPath());
        var peer = new PeerCommunication(torrent, new TestPeerListener(), TimeProvider.System);

        peer.AssignBandwidth(0);
        peer.AssignBandwidth(int.MaxValue);

        await torrent.DisposeAsync();
    }

    [Fact]
    public async Task GetConnectionElapsedMs_BeforeConnect_ReturnsZero()
    {
        var torrent = TorrentTestUtility.CreateMinimal(CreateMetadataV1(), CreateTempPath());
        var peer = new PeerCommunication(torrent, new TestPeerListener(), TimeProvider.System);

        Assert.Equal(0, peer.GetConnectionElapsedMs());

        await torrent.DisposeAsync();
    }

    [Fact]
    public async Task UpdateSpeed_DerivesDownloadAndUploadRates()
    {
        var torrent = TorrentTestUtility.CreateMinimal(CreateMetadataV1(), CreateTempPath());
        var peer = new PeerCommunication(torrent, new TestPeerListener(), TimeProvider.System);

        peer.AddDownloaded(1_000_000);
        peer.AddUploaded(500_000);
        peer.UpdateSpeed();

        Assert.Equal(1_000_000, peer.DownloadSpeed);
        Assert.Equal(500_000, peer.UploadSpeed);

        // Smoothing on first sample with old=0 should be quick adoption: (0*7 + 1_000_000*3) / 10
        Assert.Equal(300_000, peer.SmoothedDownloadSpeed);

        // Slow decay path: when sample <= smoothed, decay slowly
        peer.UpdateSpeed(); // delta is 0
        // newSmoothed = (300_000 * 19 + 0) / 20 = 285_000
        Assert.Equal(285_000, peer.SmoothedDownloadSpeed);

        await torrent.DisposeAsync();
    }

    [Fact]
    public async Task CanUseUtpWithProxy_ReturnsTrueWhenNoProxy()
    {
        var settings = new Settings();
        Assert.True(PeerCommunication.CanUseUtpWithProxy(settings));
    }

    [Theory]
    [InlineData(ProxyType.Socks5, true)]
    [InlineData(ProxyType.Http, false)]
    public async Task CanUseUtpWithProxy_DependsOnProxyType(ProxyType type, bool expected)
    {
        var settings = new Settings();
        settings.Proxy.Type = type;
        settings.Proxy.Host = "proxy.local";
        settings.Proxy.ProxyPeers = true;
        Assert.Equal(expected, PeerCommunication.CanUseUtpWithProxy(settings));
    }

    [Fact]
    public async Task CanUseUtpWithProxy_TrueWhenProxyDoesNotProxyPeers()
    {
        var settings = new Settings();
        settings.Proxy.Type = ProxyType.Http;
        settings.Proxy.Host = "proxy.local";
        settings.Proxy.ProxyPeers = false;
        Assert.True(PeerCommunication.CanUseUtpWithProxy(settings));
    }

    [Fact]
    public async Task ShouldDropNonCriticalMessage_RetainsCriticalMessages()
    {
        var torrent = TorrentTestUtility.CreateMinimal(CreateMetadataV1(), CreateTempPath());
        var peer = new PeerCommunication(torrent, new TestPeerListener(), TimeProvider.System);

        SetPrivateField(peer, "_connected", 1);
        SetPrivateField(peer, "_smoothedDownloadSpeed", 0);

        var queue = GetPrivateField<object>(peer, "_sendQueue");
        int limit = InvokePrivate<int>(peer, "GetAdaptiveSendQueueLimit");
        for (int i = 0; i < limit; i++)
        {
            InvokePublic(queue, "TryEnqueue", new PeerMessage(MessageId.Have));
        }

        // Critical messages should still be considered for enqueue (return false from drop check)
        bool dropPiece = InvokePrivate<bool>(peer, "ShouldDropNonCriticalMessage", new PeerMessage(MessageId.Piece));
        bool dropChoke = InvokePrivate<bool>(peer, "ShouldDropNonCriticalMessage", new PeerMessage(MessageId.Choke));
        bool dropRequest = InvokePrivate<bool>(peer, "ShouldDropNonCriticalMessage", new PeerMessage(MessageId.Request));

        Assert.False(dropPiece);
        Assert.False(dropChoke);
        Assert.False(dropRequest);

        // Non-critical drops
        Assert.True(InvokePrivate<bool>(peer, "ShouldDropNonCriticalMessage", new PeerMessage(MessageId.Have)));
        Assert.True(InvokePrivate<bool>(peer, "ShouldDropNonCriticalMessage", new PeerMessage(MessageId.AllowedFast)));
        Assert.True(InvokePrivate<bool>(peer, "ShouldDropNonCriticalMessage", new PeerMessage(MessageId.Suggest)));
        Assert.True(InvokePrivate<bool>(peer, "ShouldDropNonCriticalMessage", new PeerMessage(MessageId.Port)));

        await torrent.DisposeAsync();
    }

    [Fact]
    public async Task ConfigureTcpClient_AppliesSocketOptionsWithoutThrowing()
    {
        using var server = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        server.Start();
        int port = ((System.Net.IPEndPoint)server.LocalEndpoint).Port;

        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, port);
        using var serverClient = await server.AcceptTcpClientAsync();

        var settings = new Settings();
        settings.Connection.TcpNoDelay = true;
        settings.Connection.TcpReceiveBufferBytes = 32 * 1024;
        settings.Connection.TcpSendBufferBytes = 32 * 1024;

        var configure = typeof(PeerCommunication)
            .GetMethod("ConfigureTcpClient", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(configure);
        configure!.Invoke(null, new object?[] { client, settings, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance });

        Assert.True(client.NoDelay);
    }

    [Fact]
    public async Task HandleHashRequestAsync_V2_RepliesWithHashes()
    {
        var metadata = CreateMetadataV2();
        string path = CreateTempPath();

        byte[] v2Root = metadata.Info.HashV2.Span.ToArray();
        metadata.Info.Files[0].PiecesRoot = v2Root;
        // Directly set PieceLayers to bypass TryAddV2Hashes logic for testing HandleHashRequestAsync
        byte[] hash1 = new byte[32], hash2 = new byte[32];
        metadata.Info.Files[0].PieceLayers = [hash1, hash2];

        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);
        int baseLayer = PeerSharp.Internals.Utilities.MerkleTree.GetPieceLayerDepth(metadata.Info.PieceSize);

        var peer = new PeerCommunication(torrent, new TestPeerListener(), TimeProvider.System);
        SetPrivateProperty(peer, "RemoteSupportsV2", true);
        SetPrivateField(peer, "_connected", 1);

        var request = new PeerMessage(MessageId.HashRequest)
        {
            HashPiecesRoot = v2Root,
            HashBaseLayer = baseLayer,
            HashIndex = 0,
            HashLength = 2,
            HashProofLayers = 0
        };

        await InvokePrivate<Task>(peer, "HandleHashRequestAsync", request);

        var queue = GetPrivateField<MessageQueue>(peer, "_sendQueue");
        bool dequeued = queue.TryDequeue(out PeerMessage? reply);
        Assert.True(dequeued);
        Assert.NotNull(reply);

        Assert.Equal(MessageId.Hashes, reply.Id);
        Assert.Equal(v2Root, reply.HashPiecesRoot);
        // Should contain the 2 hashes we set in PieceLayers
        byte[] expectedHashes = new byte[64];
        Assert.Equal(expectedHashes, reply.Data);

        await torrent.DisposeAsync();
        CleanupPath(path);
    }

    [Fact]
    public async Task HandleHashRequestAsync_InvalidRoot_RepliesWithHashReject()
    {
        var metadata = CreateMetadataV2();
        string path = CreateTempPath();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);

        var peer = new PeerCommunication(torrent, new TestPeerListener(), TimeProvider.System);
        SetPrivateProperty(peer, "RemoteSupportsV2", true);
        SetPrivateField(peer, "_connected", 1);

        var request = new PeerMessage(MessageId.HashRequest)
        {
            HashPiecesRoot = new byte[32], // invalid
            HashBaseLayer = 0,
            HashIndex = 0,
            HashLength = 2,
            HashProofLayers = 0
        };

        await InvokePrivate<Task>(peer, "HandleHashRequestAsync", request);

        var queue = GetPrivateField<MessageQueue>(peer, "_sendQueue");
        bool dequeued = queue.TryDequeue(out PeerMessage? reply);
        Assert.True(dequeued);
        Assert.NotNull(reply);

        Assert.Equal(MessageId.HashReject, reply.Id);
        Assert.Equal(request.HashPiecesRoot, reply.HashPiecesRoot);

        await torrent.DisposeAsync();
        CleanupPath(path);
    }

    [Fact]
    public async Task SendMessageMethods_EnqueueCorrectMessages()
    {
        var metadata = CreateMetadataV1();
        string path = CreateTempPath();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);
        var peer = new PeerCommunication(torrent, new TestPeerListener(), TimeProvider.System);
        SetPrivateProperty(peer, "RemoteSupportsFastExtension", true);
        SetPrivateProperty(peer, "RemoteSupportsV2", true);
        SetPrivateField(peer, "_connected", 1); // Set connected to 1 to allow messages

        await peer.SendAllowedFastAsync(42);
        await peer.SendSuggestAsync(99);
        await peer.SendRejectAsync(new BlockRequest { PieceIndex = 1, Offset = 16384, Length = 8192 });
        await peer.SendHashRequestAsync(new byte[32], 4, 8, 16, 2);

        var queue = GetPrivateField<MessageQueue>(peer, "_sendQueue");
        var messages = new List<PeerMessage>();
        while (queue.TryDequeue(out PeerMessage? reply))
        {
            messages.Add(reply!);
        }

        Assert.Contains(messages, m => m.Id == MessageId.AllowedFast && m.PieceIndex == 42);
        Assert.Contains(messages, m => m.Id == MessageId.Suggest && m.PieceIndex == 99);
        Assert.Contains(messages, m => m.Id == MessageId.Reject && m.PieceIndex == 1 && m.BlockOffset == 16384 && m.BlockLength == 8192);
        Assert.Contains(messages, m => m.Id == MessageId.HashRequest && m.HashBaseLayer == 4 && m.HashIndex == 8);

        await torrent.DisposeAsync();
        CleanupPath(path);
    }

    [Fact]
    public async Task SendMessageAsync_DropsNonCriticalWhenQueueIsFull()
    {
        var metadata = CreateMetadataV1();
        string path = CreateTempPath();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);

        var peer = new PeerCommunication(torrent, new TestPeerListener(), TimeProvider.System);
        SetPrivateField(peer, "_connected", 1);
        SetPrivateField(peer, "_smoothedDownloadSpeed", 0);

        var queue = GetPrivateField<object>(peer, "_sendQueue");
        int limit = InvokePrivate<int>(peer, "GetAdaptiveSendQueueLimit");
        for (int i = 0; i < limit; i++)
        {
            InvokePublic(queue, "TryEnqueue", new PeerMessage(MessageId.Have));
        }

        int before = (int)GetPublicProperty(queue, "Count")!;
        await peer.SendMessageAsync(new PeerMessage(MessageId.Have));
        int after = (int)GetPublicProperty(queue, "Count")!;

        Assert.Equal(before, after);

        await torrent.DisposeAsync();
        CleanupPath(path);
    }

    private static TorrentFileMetadata CreateMetadataV1()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Version = TorrentVersion.V1;
        metadata.Info.Hash = new InfoHash(Enumerable.Range(0, 20).Select(i => (byte)i).ToArray());
        metadata.Info.PieceSize = ProtocolConstants.BlockSize;
        metadata.Info.FullSize = ProtocolConstants.BlockSize;
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "file.bin", Size = ProtocolConstants.BlockSize, Offset = 0 });
        return metadata;
    }

    private static TorrentFileMetadata CreateMetadataV2()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Version = TorrentVersion.V2;
        metadata.Info.HashV2 = InfoHash.CreateRandomV2();
        metadata.Info.PieceSize = ProtocolConstants.BlockSize;
        metadata.Info.FullSize = ProtocolConstants.BlockSize * 2;
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "file.bin", Size = ProtocolConstants.BlockSize * 2, Offset = 0 });
        return metadata;
    }

    private static byte[] BuildHandshake(ReadOnlySpan<byte> infoHash, byte[] peerId, bool supportsExtensions, bool supportsFast, bool supportsV2)
    {
        byte[] handshake = new byte[68];
        handshake[0] = 19;
        System.Text.Encoding.ASCII.GetBytes("BitTorrent protocol").CopyTo(handshake, 1);
        if (supportsExtensions)
        {
            handshake[25] |= 0x10;
        }
        if (supportsFast)
        {
            handshake[27] |= 0x04;
        }
        if (supportsV2)
        {
            handshake[27] |= 0x10;
        }

        infoHash.CopyTo(handshake.AsSpan(28, 20));
        peerId.CopyTo(handshake, 48);
        return handshake;
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

    private static void SetPrivateProperty(object target, string propertyName, object value)
    {
        var prop = target.GetType().GetProperty(propertyName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        if (prop?.SetMethod == null)
        {
            throw new InvalidOperationException($"Property '{propertyName}' not found or not settable on {target.GetType().Name}");
        }
        prop.SetValue(target, value);
    }

    private static T InvokePrivate<T>(object target, string methodName, params object[] args)
    {
        var method = target.GetType().GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (method == null)
        {
            throw new InvalidOperationException($"Method '{methodName}' not found on {target.GetType().Name}");
        }
        return (T)method.Invoke(target, args)!;
    }

    private static object? InvokePublic(object target, string methodName, params object[] args)
    {
        var method = target.GetType().GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        if (method == null)
        {
            throw new InvalidOperationException($"Method '{methodName}' not found on {target.GetType().Name}");
        }
        return method.Invoke(target, args);
    }

    private static object? GetPublicProperty(object target, string propertyName)
    {
        var prop = target.GetType().GetProperty(propertyName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        if (prop == null)
        {
            throw new InvalidOperationException($"Property '{propertyName}' not found on {target.GetType().Name}");
        }
        return prop.GetValue(target);
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field == null)
        {
            throw new InvalidOperationException($"Field '{fieldName}' not found on {target.GetType().Name}");
        }
        return (T)field.GetValue(target)!;
    }

    private static string CreateTempPath()
    {
        return Path.Combine(Path.GetTempPath(), "MtTorrentTests_PeerComm", Guid.NewGuid().ToString("N"));
    }

    private static void CleanupPath(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // Best-effort cleanup for temp artifacts.
        }
    }

    [Fact]
    public async Task SendHaveNoneAsync_FastExtensionPeer_ReturnsTrueAndEnqueuesMessage()
    {
        var metadata = CreateMetadataV1();
        string path = CreateTempPath();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);
        var peer = new PeerCommunication(torrent, new TestPeerListener(), TimeProvider.System);
        SetPrivateProperty(peer, "RemoteSupportsFastExtension", true);
        SetPrivateField(peer, "_connected", 1);

        bool result = await peer.SendHaveNoneAsync();

        Assert.True(result);
        var queue = GetPrivateField<MessageQueue>(peer, "_sendQueue");
        Assert.True(queue.TryDequeue(out var msg));
        Assert.Equal(MessageId.HaveNone, msg!.Id);

        await torrent.DisposeAsync();
        CleanupPath(path);
    }

    [Fact]
    public async Task SendHaveNoneAsync_NonFastPeer_ReturnsFalseNoMessage()
    {
        var metadata = CreateMetadataV1();
        string path = CreateTempPath();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);
        var peer = new PeerCommunication(torrent, new TestPeerListener(), TimeProvider.System);
        SetPrivateField(peer, "_connected", 1);
        // RemoteSupportsFastExtension is false by default

        bool result = await peer.SendHaveNoneAsync();

        Assert.False(result);
        var queue = GetPrivateField<MessageQueue>(peer, "_sendQueue");
        Assert.False(queue.TryDequeue(out _));

        await torrent.DisposeAsync();
        CleanupPath(path);
    }

    [Fact]
    public async Task SendPortAsync_WhenConnected_EnqueuesPortMessage()
    {
        var metadata = CreateMetadataV1();
        string path = CreateTempPath();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);
        var peer = new PeerCommunication(torrent, new TestPeerListener(), TimeProvider.System);
        SetPrivateField(peer, "_connected", 1);

        await peer.SendPortAsync(6881);

        var queue = GetPrivateField<MessageQueue>(peer, "_sendQueue");
        Assert.True(queue.TryDequeue(out var msg));
        Assert.Equal(MessageId.Port, msg!.Id);
        Assert.Equal((ushort)6881, msg.Port);

        await torrent.DisposeAsync();
        CleanupPath(path);
    }

    [Fact]
    public async Task DisposeAsync_WhenNotConnected_CompletesCleanly()
    {
        var metadata = CreateMetadataV1();
        string path = CreateTempPath();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);
        var peer = new PeerCommunication(torrent, new TestPeerListener(), TimeProvider.System);

        var ex = await Record.ExceptionAsync(() => peer.DisposeAsync().AsTask());

        Assert.Null(ex);

        await torrent.DisposeAsync();
        CleanupPath(path);
    }

    // ── ProcessMessageAsync — message switch cases ─────────────────────────

    [Fact]
    public async Task ProcessMessageAsync_CancelMessage_NotifiesListener()
    {
        var metadata = CreateMetadataV1();
        string path = CreateTempPath();
        var listener = new RecordingPeerListener();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);
        var peer = new PeerCommunication(torrent, listener, TimeProvider.System);

        var msg = new PeerMessage(MessageId.Cancel) { PieceIndex = 3, BlockOffset = 0, BlockLength = 16384 };
        await InvokePrivate<Task>(peer, "ProcessMessageAsync", msg);

        Assert.Contains(listener.Received, m => m.Id == MessageId.Cancel);

        await torrent.DisposeAsync();
        CleanupPath(path);
    }

    [Fact]
    public async Task ProcessMessageAsync_SuggestMessage_AddsSuggestedPiece()
    {
        var metadata = CreateMetadataV1();
        string path = CreateTempPath();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);
        var peer = new PeerCommunication(torrent, new TestPeerListener(), TimeProvider.System);

        var msg = new PeerMessage(MessageId.Suggest) { PieceIndex = 7 };
        await InvokePrivate<Task>(peer, "ProcessMessageAsync", msg);

        var suggested = peer.GetSuggestedPieces();
        Assert.Contains(7, suggested);

        await torrent.DisposeAsync();
        CleanupPath(path);
    }

    [Fact]
    public async Task ProcessMessageAsync_AllowedFastMessage_AddsAllowedFastPiece()
    {
        var metadata = CreateMetadataV1();
        string path = CreateTempPath();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);
        var peer = new PeerCommunication(torrent, new TestPeerListener(), TimeProvider.System);

        var msg = new PeerMessage(MessageId.AllowedFast) { PieceIndex = 11 };
        await InvokePrivate<Task>(peer, "ProcessMessageAsync", msg);

        Assert.True(peer.IsAllowedFast(11));

        await torrent.DisposeAsync();
        CleanupPath(path);
    }

    [Fact]
    public async Task ProcessMessageAsync_RejectMessage_NotifiesListener()
    {
        var metadata = CreateMetadataV1();
        string path = CreateTempPath();
        var listener = new RecordingPeerListener();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);
        var peer = new PeerCommunication(torrent, listener, TimeProvider.System);

        var msg = new PeerMessage(MessageId.Reject) { PieceIndex = 2, BlockOffset = 0, BlockLength = 16384 };
        await InvokePrivate<Task>(peer, "ProcessMessageAsync", msg);

        Assert.Contains(listener.Received, m => m.Id == MessageId.Reject);

        await torrent.DisposeAsync();
        CleanupPath(path);
    }

    [Fact]
    public async Task ProcessMessageAsync_PortMessage_NotifiesPortReceived()
    {
        var metadata = CreateMetadataV1();
        string path = CreateTempPath();
        var listener = new RecordingPeerListener();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);
        var peer = new PeerCommunication(torrent, listener, TimeProvider.System);

        var msg = new PeerMessage(MessageId.Port) { Port = 6881 };
        await InvokePrivate<Task>(peer, "ProcessMessageAsync", msg);

        Assert.Contains(listener.ReceivedPorts, p => p == 6881);

        await torrent.DisposeAsync();
        CleanupPath(path);
    }

    [Fact]
    public async Task ProcessMessageAsync_BitfieldAfterFirstMessage_ThrowsInvalidDataException()
    {
        var metadata = CreateMetadataV1WithPieces();
        string path = CreateTempPath();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);
        var peer = new PeerCommunication(torrent, new TestPeerListener(), TimeProvider.System);

        // First send a Have to set _firstMessageProcessed = true
        await InvokePrivate<Task>(peer, "ProcessMessageAsync", new PeerMessage(MessageId.Have) { HavePieceIndex = 0 });

        // Now send Bitfield - should throw because firstMessageProcessed is true and torrent has metadata
        var bitfieldMsg = new PeerMessage(MessageId.Bitfield) { Data = new byte[1] };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            InvokePrivate<Task>(peer, "ProcessMessageAsync", bitfieldMsg));

        await torrent.DisposeAsync();
        CleanupPath(path);
    }

    [Fact]
    public async Task ProcessMessageAsync_RequestWhenAmChoking_MessageDroppedSilently()
    {
        var metadata = CreateMetadataV1();
        string path = CreateTempPath();
        var listener = new RecordingPeerListener();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);
        var peer = new PeerCommunication(torrent, listener, TimeProvider.System);

        // AmChoking is true by default
        Assert.True(peer.AmChoking);

        var msg = new PeerMessage(MessageId.Request) { PieceIndex = 1, BlockOffset = 0, BlockLength = 16384 };
        await InvokePrivate<Task>(peer, "ProcessMessageAsync", msg);

        // Listener should NOT receive a Request when we are choking
        Assert.DoesNotContain(listener.Received, m => m.Id == MessageId.Request);

        await torrent.DisposeAsync();
        CleanupPath(path);
    }

    [Fact]
    public async Task ProcessMessageAsync_HashRejectMessage_LoggedWithoutException()
    {
        var metadata = CreateMetadataV2();
        string path = CreateTempPath();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);
        var peer = new PeerCommunication(torrent, new TestPeerListener(), TimeProvider.System);

        var msg = new PeerMessage(MessageId.HashReject) { HashPiecesRoot = new byte[32] };
        var ex = await Record.ExceptionAsync(() => InvokePrivate<Task>(peer, "ProcessMessageAsync", msg));

        Assert.Null(ex);

        await torrent.DisposeAsync();
        CleanupPath(path);
    }

    private static TorrentFileMetadata CreateMetadataV1WithPieces()
    {
        var metadata = CreateMetadataV1();
        // Add a fake piece hash so HasMetadata returns true
        metadata.Info.Pieces.Add(new byte[20]);
        return metadata;
    }

    private sealed class RecordingPeerListener : IPeerListener
    {
        public List<PeerMessage> Received { get; } = [];
        public List<ushort> ReceivedPorts { get; } = [];

        public Task HandshakeFinishedAsync(IPeerCommunication peer) => Task.CompletedTask;
        public Task ConnectionClosedAsync(IPeerCommunication peer, int code) => Task.CompletedTask;
        public Task MessageReceivedAsync(IPeerCommunication peer, PeerMessage msg) { Received.Add(msg); return Task.CompletedTask; }
        public Task ExtendedHandshakeFinishedAsync(IPeerCommunication peer, ExtensionHandshake handshake) => Task.CompletedTask;
        public Task ExtendedMessageReceivedAsync(IPeerCommunication peer, int type, byte[] data) => Task.CompletedTask;
        public Task PexReceivedAsync(IPeerCommunication peer, List<System.Net.IPEndPoint> added, List<byte> addedFlags, List<System.Net.IPEndPoint> dropped) => Task.CompletedTask;
        public Task HolepunchMessageReceivedAsync(IPeerCommunication peer, UtHolepunch.MsgId id, System.Net.IPEndPoint endpoint, UtHolepunch.ErrorCode error) => Task.CompletedTask;
        public Task PortReceivedAsync(IPeerCommunication peer, ushort dhtPort) { ReceivedPorts.Add(dhtPort); return Task.CompletedTask; }
    }

    private sealed class TestPeerListener : IPeerListener
    {
        public Task HandshakeFinishedAsync(IPeerCommunication peer) => Task.CompletedTask;
        public Task ConnectionClosedAsync(IPeerCommunication peer, int code) => Task.CompletedTask;
        public Task MessageReceivedAsync(IPeerCommunication peer, PeerMessage msg) => Task.CompletedTask;
        public Task ExtendedHandshakeFinishedAsync(IPeerCommunication peer, ExtensionHandshake handshake) => Task.CompletedTask;
        public Task ExtendedMessageReceivedAsync(IPeerCommunication peer, int type, byte[] data) => Task.CompletedTask;
        public Task PexReceivedAsync(IPeerCommunication peer, List<System.Net.IPEndPoint> added, List<byte> addedFlags, List<System.Net.IPEndPoint> dropped) => Task.CompletedTask;
        public Task HolepunchMessageReceivedAsync(IPeerCommunication peer, UtHolepunch.MsgId id, System.Net.IPEndPoint endpoint, UtHolepunch.ErrorCode error) => Task.CompletedTask;
        public Task PortReceivedAsync(IPeerCommunication peer, ushort dhtPort) => Task.CompletedTask;
    }
}




