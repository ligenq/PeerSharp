using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using PeerSharp.BEncoding;
using System.Net;
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
        public List<PeerMessage> SentMessages { get; } = new();
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
        public List<int> RequestedPieces { get; } = new();
        public void Init(ExtensionHandshake handshake) { }
        public void SetLocalMessageId(int id) => LocalMessageId = id;
        public void SendRequest(int piece)
        {
            RequestedPieces.Add(piece);
        }

        public void SendData(int piece, byte[] data, int totalSize) { }
        public void SendReject(int piece) { }
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
        var requestsField = typeof(MetadataDownload).GetField("_pendingRequests", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var requests = requestsField?.GetValue(download) as System.Collections.IDictionary;
        Assert.NotNull(requests);
        Assert.Equal(1, requests.Count);

        download.PeerDisconnected(mockPeer);

        // Ensure requests are cleared
        Assert.Equal(0, requests.Count);
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

        var requestsField = typeof(MetadataDownload).GetField("_pendingRequests", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var requests = requestsField?.GetValue(download) as System.Collections.IDictionary;
        Assert.NotNull(requests);
        Assert.Equal(0, requests.Count);

        var activeProp = typeof(MetadataDownload).GetProperty("Active");
        bool isActive = (bool)activeProp?.GetValue(download)!;
        Assert.False(isActive);
    }
}





