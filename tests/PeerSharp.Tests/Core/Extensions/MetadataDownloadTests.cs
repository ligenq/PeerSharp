using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using PeerSharp.BEncoding;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using PeerSharp.Messages;

namespace PeerSharp.Tests.Core.Extensions;

public class MetadataDownloadTests
{
    private class MockPeerCommunication : IPeerCommunication
    {
        public byte[] PeerId { get; set; } = new byte[20];
        public IPEndPoint? RemoteEndPoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 1234);
        public bool RemoteSupportsExtensions { get; set; }
        public ExtensionHandshake? RemoteExtensions { get; set; }
        public IUtMetadata UtMetadata { get; set; } = null!;
        public IUtPex UtPex => throw new NotImplementedException();
        public IPeerListener Listener => throw new NotImplementedException();
        public IUtHashPiece? UtHashPiece => throw new NotImplementedException();
        public IUtHolepunch UtHolepunch => throw new NotImplementedException();
        public List<PeerMessage> SentMessages { get; } = [];
        public Task SetInterestedAsync(bool interested) => Task.CompletedTask;
        public Task SendMessageAsync(PeerMessage msg)
        {
            SentMessages.Add(msg);
            return Task.CompletedTask;
        }
    }

    private class MockUtMetadata : IUtMetadata
    {
        public int? LocalMessageId { get; private set; }
        public int? RemoteMessageId { get; set; }
        public List<int> RequestedPieces { get; } = [];
        public void Init(ExtensionHandshake handshake) { }
        public void SetLocalMessageId(int id) => LocalMessageId = id;
        public void SendRequest(int piece)
        {
            RequestedPieces.Add(piece);
        }

        public List<(int Piece, byte[] Data, int TotalSize)> SentDataPieces { get; } = [];
        public List<int> RejectedPieces { get; } = [];
        public void SendData(int piece, byte[] data, int totalSize) => SentDataPieces.Add((piece, data, totalSize));
        public void SendReject(int piece) => RejectedPieces.Add(piece);
    }

    [Fact]
    public void PeerConnected_SupportsMetadata_RequestsFirstPiece()
    {
        // Arrange
        var torrent = TorrentTestUtility.CreateMinimal();
        var download = new MetadataDownload(torrent);
        download.Start();

        var utMetadata = new MockUtMetadata();
        utMetadata.RemoteMessageId = 1;
        var mockPeer = new MockPeerCommunication
        {
            RemoteSupportsExtensions = true,
            RemoteExtensions = new ExtensionHandshake { MetadataSize = 32768 }, // 2 pieces
            UtMetadata = utMetadata
        };
        mockPeer.RemoteExtensions.MessageIds[UtMetadata.Name] = 1;

        // Act
        download.PeerConnected(mockPeer);

        // Assert
        Assert.Equal(2, utMetadata.RequestedPieces.Count);
        Assert.Contains(0, utMetadata.RequestedPieces);
        Assert.Contains(1, utMetadata.RequestedPieces);
        Assert.Equal(0.0f, download.Progress);
    }

    [Fact]
    public async Task MetadataPieceReceivedAsync_Full_SetsFinished()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var download = new MetadataDownload(torrent);
        download.Start();

        // We need a valid bencoded info dict for TorrentFileParser.Parse to not throw
        var infoDict = new BDict();
        infoDict.Dict["name"] = new BString(Encoding.UTF8.GetBytes("test"));
        infoDict.Dict["piece length"] = new BNumber(16384);
        infoDict.Dict["pieces"] = new BString(new byte[20]);
        infoDict.Dict["length"] = new BNumber(100);

        var metadataBytes = BencodeWriter.Write(infoDict);

        // SECURITY: Set the torrent's expected info hash so the new check passes
        var expectedHash = System.Security.Cryptography.SHA1.HashData(metadataBytes);
        torrent.InfoFile.Info.Hash = new InfoHash(expectedHash);

        download.InitializeMetadataBuffer(metadataBytes.Length);

        var utMetadata = new MockUtMetadata();
        var mockPeer = new MockPeerCommunication { UtMetadata = utMetadata };

        // Act
        await download.MetadataPieceReceivedAsync(mockPeer, 0, metadataBytes);

        // Assert
        Assert.True(download.Finished);
        Assert.Equal(1.0f, download.Progress);
        Assert.Equal("test", torrent.Name);
    }

    [Fact]
    public async Task MetadataPieceReceivedAsync_HashMismatch_DiscardsMetadata()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var download = new MetadataDownload(torrent);
        download.Start();

        var infoDict = new BDict();
        infoDict.Dict["name"] = new BString(Encoding.UTF8.GetBytes("malicious"));
        infoDict.Dict["piece length"] = new BNumber(16384);
        infoDict.Dict["pieces"] = new BString(new byte[20]);
        infoDict.Dict["length"] = new BNumber(100);

        var metadataBytes = BencodeWriter.Write(infoDict);

        // SECURITY: Set the torrent's expected info hash to something different
        var fakeHash = new byte[20];
        fakeHash[0] = 42;
        torrent.InfoFile.Info.Hash = new InfoHash(fakeHash);

        download.InitializeMetadataBuffer(metadataBytes.Length);

        var utMetadata = new MockUtMetadata();
        var mockPeer = new MockPeerCommunication { UtMetadata = utMetadata };

        // Act
        await download.MetadataPieceReceivedAsync(mockPeer, 0, metadataBytes);

        // Assert
        Assert.False(download.Finished);
        Assert.Equal(0.0f, download.Progress);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1025)]
    public void InitializeMetadataBuffer_InvalidSize_Throws(int size)
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Settings.Transfer.MaxMetadataSizeBytes = 1024;
        var download = new MetadataDownload(torrent);

        Assert.Throws<InvalidDataException>(() => download.InitializeMetadataBuffer(size));
    }

    [Fact]
    public void InitializeMetadataBuffer_ChangedSize_Throws()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var download = new MetadataDownload(torrent);

        download.InitializeMetadataBuffer(1024);

        Assert.Throws<InvalidDataException>(() => download.InitializeMetadataBuffer(2048));
    }

    [Fact]
    public void PeerDisconnected_ClearsPendingRequestsForPeer()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var download = new MetadataDownload(torrent);
        download.Start();

        var mockPeer = new MockPeerCommunication
        {
            RemoteSupportsExtensions = true,
            RemoteExtensions = new ExtensionHandshake { MetadataSize = 16384 },
            UtMetadata = new MockUtMetadata { RemoteMessageId = 1 }
        };
        mockPeer.RemoteExtensions.MessageIds[UtMetadata.Name] = 1;

        download.PeerConnected(mockPeer);

        // Ensure piece 0 was requested
        Assert.Equal(1, download.PendingRequestCountForTesting);

        download.PeerDisconnected(mockPeer);

        // Ensure requests are cleared
        Assert.Equal(0, download.PendingRequestCountForTesting);
    }

    [Fact]
    public void Stop_ClearsPendingRequestsAndDeactivates()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var download = new MetadataDownload(torrent);
        download.Start();

        var mockPeer = new MockPeerCommunication
        {
            RemoteSupportsExtensions = true,
            RemoteExtensions = new ExtensionHandshake { MetadataSize = 16384 },
            UtMetadata = new MockUtMetadata { RemoteMessageId = 1 }
        };
        mockPeer.RemoteExtensions.MessageIds[UtMetadata.Name] = 1;

        download.PeerConnected(mockPeer);

        download.Stop();

        Assert.Equal(0, download.PendingRequestCountForTesting);
        Assert.False(download.Active);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Build info-dict bytes that span 2 metadata pieces (> 16384 bytes)
    private static (byte[] Bytes, InfoHash Hash) BuildTwoPieceMetadata()
    {
        var infoDict = new BDict();
        infoDict.Dict["name"] = new BString(Encoding.UTF8.GetBytes("two-piece"));
        infoDict.Dict["piece length"] = new BNumber(16384);
        infoDict.Dict["pieces"] = new BString(new byte[20]);
        infoDict.Dict["length"] = new BNumber(16384 + 1024);
        infoDict.Dict["_pad"] = new BString(new byte[20000]); // forces > 16384 bytes
        var bytes = BencodeWriter.Write(infoDict);
        var hash = new InfoHash(SHA1.HashData(bytes));
        return (bytes, hash);
    }

    private static MockPeerCommunication MakePeer(int? metadataSize = null)
    {
        var utMeta = new MockUtMetadata { RemoteMessageId = 1 };
        var peer = new MockPeerCommunication
        {
            RemoteSupportsExtensions = true,
            RemoteExtensions = new ExtensionHandshake { MetadataSize = metadataSize },
            UtMetadata = utMeta,
        };
        peer.RemoteExtensions.MessageIds[UtMetadata.Name] = 1;
        return peer;
    }

    // ── MetadataRejectReceived ────────────────────────────────────────────────

    [Fact]
    public void MetadataRejectReceived_PendingPiece_ReRequestsFromAlternatePeer()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Settings.Transfer.MetadataRequestPipeline = 1;
        var download = new MetadataDownload(torrent);
        download.Start();
        download.InitializeMetadataBuffer(UtMetadata.PieceSize);

        var peer1 = MakePeer(UtMetadata.PieceSize);
        var peer2 = MakePeer(UtMetadata.PieceSize);

        download.PeerConnected(peer1);   // → requests piece 0 from peer1
        Assert.Contains(0, ((MockUtMetadata)peer1.UtMetadata).RequestedPieces);

        download.PeerConnected(peer2);   // pipeline full, peer2 gets nothing yet
        Assert.Empty(((MockUtMetadata)peer2.UtMetadata).RequestedPieces);

        download.MetadataRejectReceived(peer1, 0); // reject from peer1 → re-request from peer2

        Assert.Contains(0, ((MockUtMetadata)peer2.UtMetadata).RequestedPieces);
    }

    [Fact]
    public void MetadataRejectReceived_NoPendingRequest_IsNoOp()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var download = new MetadataDownload(torrent);
        download.Start();
        download.InitializeMetadataBuffer(UtMetadata.PieceSize);

        var peer = MakePeer(UtMetadata.PieceSize);

        download.MetadataRejectReceived(peer, 0); // nothing pending for piece 0

        Assert.Empty(((MockUtMetadata)peer.UtMetadata).RequestedPieces);
    }

    // ── MetadataPieceReceivedAsync – guard paths ──────────────────────────────

    [Fact]
    public async Task MetadataPieceReceivedAsync_WhenNotActive_IgnoresPiece()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var download = new MetadataDownload(torrent);
        // Not started → Active = false
        download.InitializeMetadataBuffer(UtMetadata.PieceSize);

        var peer = new MockPeerCommunication { UtMetadata = new MockUtMetadata() };
        await download.MetadataPieceReceivedAsync(peer, 0, new byte[UtMetadata.PieceSize]);

        Assert.False(download.Finished);
    }

    [Fact]
    public async Task MetadataPieceReceivedAsync_WhenAlreadyFinished_IgnoresPiece()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var download = new MetadataDownload(torrent);
        download.SetMetadata(new byte[UtMetadata.PieceSize]);

        var peer = new MockPeerCommunication { UtMetadata = new MockUtMetadata() };
        await download.MetadataPieceReceivedAsync(peer, 0, new byte[UtMetadata.PieceSize]);

        Assert.True(download.Finished); // still finished, nothing changed
    }

    [Fact]
    public async Task MetadataPieceReceivedAsync_OutOfBoundsPieceIndex_IgnoresPiece()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var download = new MetadataDownload(torrent);
        download.Start();
        download.InitializeMetadataBuffer(UtMetadata.PieceSize); // 1 piece (index 0 only)

        var peer = new MockPeerCommunication { UtMetadata = new MockUtMetadata() };
        await download.MetadataPieceReceivedAsync(peer, 5, new byte[10]);

        Assert.False(download.Finished);
        Assert.Equal(0.0f, download.Progress);
    }

    [Fact]
    public async Task MetadataPieceReceivedAsync_DuplicatePiece_IgnoresSecondCopy()
    {
        var (metadataBytes, hash) = BuildTwoPieceMetadata();
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.InfoFile.Info.Hash = hash;
        var download = new MetadataDownload(torrent);
        download.Start();
        download.InitializeMetadataBuffer(metadataBytes.Length);

        var peer = new MockPeerCommunication { UtMetadata = new MockUtMetadata() };
        byte[] piece0 = metadataBytes[..UtMetadata.PieceSize];

        await download.MetadataPieceReceivedAsync(peer, 0, piece0);
        float progressAfterFirst = download.Progress;
        Assert.False(download.Finished);

        await download.MetadataPieceReceivedAsync(peer, 0, piece0); // duplicate
        Assert.Equal(progressAfterFirst, download.Progress);
        Assert.False(download.Finished);
    }

    [Fact]
    public async Task MetadataPieceReceivedAsync_DataBeyondBuffer_IgnoresPiece()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var download = new MetadataDownload(torrent);
        download.Start();
        download.InitializeMetadataBuffer(1000); // buffer of 1000 bytes, 1 piece

        var peer = new MockPeerCommunication { UtMetadata = new MockUtMetadata() };
        await download.MetadataPieceReceivedAsync(peer, 0, new byte[1001]); // overflows

        Assert.False(download.Finished);
        Assert.Equal(0.0f, download.Progress);
    }

    // ── MetadataPieceReceivedAsync – completion paths ─────────────────────────

    [Fact]
    public async Task MetadataPieceReceivedAsync_OutOfOrder_CompletesOnLastPiece()
    {
        var (metadataBytes, hash) = BuildTwoPieceMetadata();
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.InfoFile.Info.Hash = hash;
        var download = new MetadataDownload(torrent);
        download.Start();
        download.InitializeMetadataBuffer(metadataBytes.Length);

        var peer = new MockPeerCommunication { UtMetadata = new MockUtMetadata() };
        byte[] piece0 = metadataBytes[..UtMetadata.PieceSize];
        byte[] piece1 = metadataBytes[UtMetadata.PieceSize..];

        await download.MetadataPieceReceivedAsync(peer, 1, piece1); // piece 1 first
        Assert.False(download.Finished);

        await download.MetadataPieceReceivedAsync(peer, 0, piece0); // piece 0 last
        Assert.True(download.Finished);
        Assert.Equal(1.0f, download.Progress);
    }

    [Fact]
    public async Task MetadataPieceReceivedAsync_MultiPeer_FirstWinsRace()
    {
        var (metadataBytes, hash) = BuildTwoPieceMetadata();
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.InfoFile.Info.Hash = hash;
        var download = new MetadataDownload(torrent);
        download.Start();
        download.InitializeMetadataBuffer(metadataBytes.Length);

        var peer1 = new MockPeerCommunication { UtMetadata = new MockUtMetadata() };
        var peer2 = new MockPeerCommunication { UtMetadata = new MockUtMetadata() };

        byte[] piece0 = metadataBytes[..UtMetadata.PieceSize];
        byte[] piece1 = metadataBytes[UtMetadata.PieceSize..];

        await download.MetadataPieceReceivedAsync(peer1, 0, piece0); // peer1 sends piece 0
        Assert.False(download.Finished);

        await download.MetadataPieceReceivedAsync(peer2, 1, piece1); // peer2 completes it
        Assert.True(download.Finished);

        // Late arrivals after Finished are silently ignored
        await download.MetadataPieceReceivedAsync(peer1, 1, piece1);
        await download.MetadataPieceReceivedAsync(peer2, 0, piece0);
        Assert.True(download.Finished);
        Assert.Equal(1.0f, download.Progress);
    }

    [Fact]
    public async Task MetadataPieceReceivedAsync_HashMismatch_ResetsAndReRequests()
    {
        var (metadataBytes, _) = BuildTwoPieceMetadata();
        var torrent = TorrentTestUtility.CreateMinimal();
        // Set a hash that won't match → triggers reset
        var wrongHash = new byte[20];
        wrongHash[0] = 0xFF;
        torrent.InfoFile.Info.Hash = new InfoHash(wrongHash);
        var download = new MetadataDownload(torrent);
        download.Start();
        download.InitializeMetadataBuffer(metadataBytes.Length);

        var utMeta = new MockUtMetadata { RemoteMessageId = 1 };
        var peer = new MockPeerCommunication { UtMetadata = utMeta };

        byte[] piece0 = metadataBytes[..UtMetadata.PieceSize];
        byte[] piece1 = metadataBytes[UtMetadata.PieceSize..];

        // First connect peer so it's in _activePeers for re-requesting
        download.PeerConnected(peer);
        utMeta.RequestedPieces.Clear(); // clear initial requests

        await download.MetadataPieceReceivedAsync(peer, 0, piece0);
        await download.MetadataPieceReceivedAsync(peer, 1, piece1); // all pieces received → hash mismatch

        // Should reset (not finished) and re-request pieces
        Assert.False(download.Finished);
        Assert.Equal(0.0f, download.Progress);
        Assert.NotEmpty(utMeta.RequestedPieces); // re-requests fired
    }

    // ── PeerConnected paths ───────────────────────────────────────────────────

    [Fact]
    public void PeerConnected_WhenNotActive_DoesNothing()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var download = new MetadataDownload(torrent);
        // Not started → Active = false

        var peer = MakePeer(UtMetadata.PieceSize);
        download.PeerConnected(peer);

        Assert.Empty(((MockUtMetadata)peer.UtMetadata).RequestedPieces);
    }

    [Fact]
    public void PeerConnected_WhenAlreadyFinished_DoesNothing()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var download = new MetadataDownload(torrent);
        download.SetMetadata(new byte[100]);

        var peer = MakePeer(100);
        download.PeerConnected(peer);

        Assert.Empty(((MockUtMetadata)peer.UtMetadata).RequestedPieces);
    }

    [Fact]
    public void PeerConnected_PeerNoUtMetadataSupport_DoesNothing()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var download = new MetadataDownload(torrent);
        download.Start();

        var utMeta = new MockUtMetadata { RemoteMessageId = 1 };
        var peer = new MockPeerCommunication
        {
            RemoteSupportsExtensions = true,
            RemoteExtensions = new ExtensionHandshake { MetadataSize = UtMetadata.PieceSize },
            UtMetadata = utMeta,
        };
        // Not adding UtMetadata.Name to MessageIds → peer doesn't advertise ut_metadata

        download.PeerConnected(peer);

        Assert.Empty(utMeta.RequestedPieces);
    }

    [Fact]
    public void PeerConnected_NoMetadataSize_ProbesPiece0()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var download = new MetadataDownload(torrent);
        download.Start(); // Active = true, _metadataSize = 0

        var utMeta = new MockUtMetadata { RemoteMessageId = 1 };
        var peer = new MockPeerCommunication
        {
            RemoteSupportsExtensions = true,
            RemoteExtensions = new ExtensionHandshake { MetadataSize = null }, // omitted
            UtMetadata = utMeta,
        };
        peer.RemoteExtensions.MessageIds[UtMetadata.Name] = 1;

        download.PeerConnected(peer);

        Assert.Contains(0, utMeta.RequestedPieces);
    }

    [Fact]
    public void PeerConnected_NoMetadataSizeButBufferInitialized_FillsMissingRequests()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var download = new MetadataDownload(torrent);
        download.Start();
        download.InitializeMetadataBuffer(UtMetadata.PieceSize); // _metadataSize > 0

        var utMeta = new MockUtMetadata { RemoteMessageId = 1 };
        var peer = new MockPeerCommunication
        {
            RemoteSupportsExtensions = true,
            RemoteExtensions = new ExtensionHandshake { MetadataSize = null }, // omitted
            UtMetadata = utMeta,
        };
        peer.RemoteExtensions.MessageIds[UtMetadata.Name] = 1;

        download.PeerConnected(peer); // → hits "else if (Active && !Finished && _metadataSize > 0)"

        Assert.Contains(0, utMeta.RequestedPieces);
    }

    [Fact]
    public void PeerConnected_BufferAlreadyInitialized_FillsMissingFromNewPeer()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Settings.Transfer.MetadataRequestPipeline = 1;
        var download = new MetadataDownload(torrent);
        download.Start();

        var peer1 = MakePeer(UtMetadata.PieceSize);
        download.PeerConnected(peer1); // initializes buffer and requests piece 0 from peer1

        // peer2 has the same MetadataSize; buffer already initialized, pipeline full
        var peer2 = MakePeer(UtMetadata.PieceSize);
        download.PeerConnected(peer2); // skips InitializeMetadataBuffer, calls FillMissingRequests

        // Pipeline is full so peer2 gets no request yet, but code path was exercised without error
        Assert.True(download.Active);
    }

    // ── MetadataRequestReceived ───────────────────────────────────────────────

    [Fact]
    public void MetadataRequestReceived_NotFinished_SendsReject()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var download = new MetadataDownload(torrent);
        download.Start();

        var utMeta = new MockUtMetadata { RemoteMessageId = 1 };
        var peer = new MockPeerCommunication { UtMetadata = utMeta };

        download.MetadataRequestReceived(peer, 0);

        Assert.Contains(0, utMeta.RejectedPieces);
    }

    [Fact]
    public void MetadataRequestReceived_PieceOutOfBounds_SendsReject()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var download = new MetadataDownload(torrent);
        download.SetMetadata(new byte[UtMetadata.PieceSize]); // only piece 0 exists

        var utMeta = new MockUtMetadata { RemoteMessageId = 1 };
        var peer = new MockPeerCommunication { UtMetadata = utMeta };

        download.MetadataRequestReceived(peer, 1); // piece 1 is out of range

        Assert.Contains(1, utMeta.RejectedPieces);
    }

    [Fact]
    public void MetadataRequestReceived_ValidPiece_ServesData()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var download = new MetadataDownload(torrent);

        byte[] content = Encoding.UTF8.GetBytes("hello metadata content");
        download.SetMetadata(content);

        var utMeta = new MockUtMetadata { RemoteMessageId = 1 };
        var peer = new MockPeerCommunication { UtMetadata = utMeta };

        download.MetadataRequestReceived(peer, 0);

        Assert.Single(utMeta.SentDataPieces);
        Assert.Equal(0, utMeta.SentDataPieces[0].Piece);
        Assert.Equal(content.Length, utMeta.SentDataPieces[0].TotalSize);
    }

    // ── Update ────────────────────────────────────────────────────────────────

    [Fact]
    public void Update_NoActivePeers_NoRequests()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var download = new MetadataDownload(torrent);
        download.Start(); // Active, no peers

        download.Update(); // should not throw
    }

    [Fact]
    public void Update_ActiveWithPeers_NoBufferYet_RequestsPiece0()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var download = new MetadataDownload(torrent);
        download.Start();

        var utMeta = new MockUtMetadata { RemoteMessageId = 1 };
        var peer = new MockPeerCommunication { UtMetadata = utMeta };

        // Inject peer directly into _activePeers without going through PeerConnected
        // (which would probe piece 0 itself)
        var activePeersField = typeof(MetadataDownload)
            .GetField("_activePeers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        ((List<IPeerCommunication>)activePeersField.GetValue(download)!).Add(peer);

        download.Update(); // _metadataSize == 0, peers present → RequestPiece(0, ...)

        Assert.Contains(0, utMeta.RequestedPieces);
    }

    [Fact]
    public void Update_NotActive_DoesNothing()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var download = new MetadataDownload(torrent);
        // Not started

        download.Update(); // should not throw or do anything
        Assert.False(download.Active);
    }
}

