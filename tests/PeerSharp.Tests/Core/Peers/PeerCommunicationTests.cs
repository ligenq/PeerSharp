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
        metadata.Info.FullSize = ProtocolConstants.BlockSize;
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "file.bin", Size = ProtocolConstants.BlockSize, Offset = 0 });
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




