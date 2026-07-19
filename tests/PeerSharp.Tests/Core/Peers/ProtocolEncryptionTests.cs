using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Peers;
using System.Security.Cryptography;

namespace PeerSharp.Tests.Core.Peers;

public class ProtocolEncryptionTests
{
    #region Mock Classes

    private class MockTorrent : ITorrent
    {
        public MockTorrent(InfoHash hash) { Hash = hash; }
        public InfoHash Hash { get; }
        public InfoHash HashV2 => InfoHash.EmptyV2;
        public Task StartAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task StopAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task WaitForMetadataAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public TorrentFile ExportTorrentFile() => throw new NotImplementedException();
        public bool Started => false;
        public TorrentState State => TorrentState.Stopped;
        public DateTimeOffset StateTimestamp => DateTimeOffset.MinValue;
        public Exception? LastException => null;
        public string Name => "MockTorrent";
        public float Progress => 0;
        public float SelectionProgress => 0;
        public ulong FinishedBytes => 0;
        public ulong FinishedSelectedBytes => 0;
        public bool Finished => false;
        public bool SelectionFinished => false;
        public DateTimeOffset TimeAdded => DateTimeOffset.MinValue;
        public bool HasMetadata => true;
        public long TotalSize => 0;
        public long DataLeft => 0;
        public uint PieceSize => 0;
        public int PieceCount => 0;
        public int PiecesReceived => 0;
        public byte[] GetPieceBitfield() => [];
        public TorrentResumeData GetResumeData() => throw new NotImplementedException();
        public IFiles Files => throw new NotImplementedException();
        public ITrackers Trackers => throw new NotImplementedException();
        public IPeers Peers => throw new NotImplementedException();
        public IFileTransfer FileTransfer => throw new NotImplementedException();
        public IMetadataDownload? MetadataDownload => null;
        public int FileCount => 0;
        public TorrentFileInfo GetFileInfo(int fileIndex) => throw new NotImplementedException();
        public IReadOnlyList<TorrentFileInfo> GetAllFileInfo() => Array.Empty<TorrentFileInfo>();
        public FileSelection GetFileSelection(int fileIndex) => throw new NotImplementedException();
        public Task SetFileSelectionAsync(int fileIndex, FileSelection selection, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task SetFilePriorityAsync(int fileIndex, Priority priority, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task SetAllFilesPriorityAsync(Priority priority, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public IReadOnlyList<FileSelection> GetAllFileSelections() => Array.Empty<FileSelection>();
        public Task<int> ForceRecheckAsync(IProgress<PieceCheckProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task SetDownloadPathAsync(string path) => throw new NotImplementedException();
        public Task<Stream> OpenStreamAsync(int fileIndex, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public bool HasStreamableFiles => false;
        public IReadOnlyList<int> StreamableFileIndices => Array.Empty<int>();
        public ITorrentEvents? Events => null;
        public DownloadStrategy DownloadStrategy { get => DownloadStrategy.RarestFirst; set { } }
        public int DownloadLimitBytesPerSecond { get => 0; set { } }
        public int DiskReadLimitBytesPerSecond { get => 0; set { } }
        public int DiskWriteLimitBytesPerSecond { get => 0; set { } }
        public int UploadLimitBytesPerSecond { get => 0; set { } }
        public int QueuePriority { get => 0; set { } }
        public bool QueueAutoStart { get => false; set { } }
        public float? RatioLimit { get => null; set { } }
        public TimeSpan? SeedTimeLimit { get => null; set { } }
        public void RegisterPeerTransport(IPeerTransport transport) => throw new NotImplementedException();
    }

    private class MockTorrentResolver : ITorrentResolver
    {
        private readonly List<ITorrent> _torrents = [];

        public void AddTorrent(byte[] infoHash)
        {
            _torrents.Add(new MockTorrent(infoHash));
        }

        public ITorrent? GetTorrent(InfoHash hash)
        {
            return _torrents.FirstOrDefault(t => t.Hash == hash);
        }

        public IReadOnlyList<ITorrent> GetTorrents()
        {
            return _torrents;
        }
    }

    #endregion

    [Fact]
    public void FullHandshake_Succeeds()
    {
        // Arrange
        byte[] infoHash = new byte[20];
        RandomNumberGenerator.Fill(infoHash);

        var initiator = new ProtocolEncryptionHandshake(infoHash, true);
        var responder = new ProtocolEncryptionHandshake(infoHash, false);

        byte[] initiatorHandshake = [1, 2, 3, 4, 5];
        initiator.InitialPayload = initiatorHandshake;

        // Act

        // 1. Initiator sends KeyA + PadA
        byte[] msg1 = initiator.Initiate();
        Assert.True(msg1.Length >= 96);

        // 2. Responder receives msg1, sends KeyB + PadB
        byte[] msg2 = responder.HandleIncoming(msg1);
        Assert.True(msg2.Length >= 96);

        // 3. Initiator receives msg2, sends req1Hash + verification + encrypted(VC, provide, len(PadC), PadC, len(IA), IA)
        byte[] msg3 = initiator.HandleIncoming(msg2);
        Assert.True(msg3.Length >= 40 + 8 + 4 + 2 + 2 + initiatorHandshake.Length);

        // 4. Responder receives msg3, sends encrypted(VC, select, len(PadD), PadD)
        byte[] msg4 = responder.HandleIncoming(msg3);
        Assert.True(msg4.Length >= 8 + 4 + 2);

        // 5. Initiator receives msg4
        initiator.HandleIncoming(msg4);

        // Assert
        Assert.True(initiator.IsComplete);
        Assert.True(responder.IsComplete);
        Assert.NotNull(initiator.Encryption);
        Assert.NotNull(responder.Encryption);
        Assert.Equal(initiatorHandshake, responder.ReceivedPayload);

        // Verify communication works
        byte[] secretMessage = "Hello World"u8.ToArray();
        byte[] encrypted = secretMessage.ToArray();
        initiator.Encryption.Encrypt(encrypted, 0, encrypted.Length);

        responder.Encryption.Decrypt(encrypted, 0, encrypted.Length);
        Assert.Equal(secretMessage, encrypted);
    }

    [Fact]
    public void Handshake_WrongInfoHash_Fails()
    {
        // Arrange
        byte[] infoHash1 = new byte[20];
        byte[] infoHash2 = new byte[20];
        infoHash1[0] = 1;
        infoHash2[0] = 2;

        var initiator = new ProtocolEncryptionHandshake(infoHash1, true);
        var responder = new ProtocolEncryptionHandshake(infoHash2, false);

        // Act
        byte[] msg1 = initiator.Initiate();
        byte[] msg2 = responder.HandleIncoming(msg1);
        byte[] msg3 = initiator.HandleIncoming(msg2);
        responder.HandleIncoming(msg3);

        // Assert
        Assert.True(responder.IsError);
    }

    #region ITorrentResolver Constructor Tests

    [Fact]
    public void FullHandshake_WithResolver_Succeeds()
    {
        // Arrange
        byte[] infoHash = new byte[20];
        RandomNumberGenerator.Fill(infoHash);

        var resolver = new MockTorrentResolver();
        resolver.AddTorrent(infoHash);

        var initiator = new ProtocolEncryptionHandshake(infoHash, true);
        var responder = new ProtocolEncryptionHandshake(resolver);

        byte[] initiatorHandshake = [1, 2, 3, 4, 5];
        initiator.InitialPayload = initiatorHandshake;

        // Act
        // 1. Initiator sends KeyA + PadA
        byte[] msg1 = initiator.Initiate();
        Assert.True(msg1.Length >= 96);

        // 2. Responder receives msg1, sends KeyB + PadB
        byte[] msg2 = responder.HandleIncoming(msg1);
        Assert.True(msg2.Length >= 96);

        // 3. Initiator receives msg2, sends req1Hash + verification + encrypted(VC, provide, len(PadC), PadC, len(IA), IA)
        byte[] msg3 = initiator.HandleIncoming(msg2);
        Assert.True(msg3.Length >= 40 + 8 + 4 + 2 + 2 + initiatorHandshake.Length);

        // 4. Responder receives msg3, sends encrypted(VC, select, len(PadD), PadD)
        byte[] msg4 = responder.HandleIncoming(msg3);
        Assert.True(msg4.Length >= 8 + 4 + 2);

        // 5. Initiator receives msg4
        initiator.HandleIncoming(msg4);

        // Assert
        Assert.True(initiator.IsComplete);
        Assert.True(responder.IsComplete);
        Assert.NotNull(initiator.Encryption);
        Assert.NotNull(responder.Encryption);
        Assert.Equal(initiatorHandshake, responder.ReceivedPayload);
        Assert.NotNull(responder.MatchedInfoHash);
        Assert.Equal(infoHash, responder.MatchedInfoHash);
    }

    [Fact]
    public void Handshake_WithResolver_NoMatchingTorrent_Fails()
    {
        // Arrange
        byte[] initiatorInfoHash = new byte[20];
        byte[] differentInfoHash = new byte[20];
        RandomNumberGenerator.Fill(initiatorInfoHash);
        RandomNumberGenerator.Fill(differentInfoHash);

        var resolver = new MockTorrentResolver();
        resolver.AddTorrent(differentInfoHash); // Add a different torrent

        var initiator = new ProtocolEncryptionHandshake(initiatorInfoHash, true);
        var responder = new ProtocolEncryptionHandshake(resolver);

        // Act
        byte[] msg1 = initiator.Initiate();
        byte[] msg2 = responder.HandleIncoming(msg1);
        byte[] msg3 = initiator.HandleIncoming(msg2);
        responder.HandleIncoming(msg3);

        // Assert
        Assert.True(responder.IsError);
        Assert.Null(responder.MatchedInfoHash);
    }

    [Fact]
    public void Handshake_WithResolver_EmptyResolver_Fails()
    {
        // Arrange
        byte[] infoHash = new byte[20];
        RandomNumberGenerator.Fill(infoHash);

        var resolver = new MockTorrentResolver(); // Empty resolver

        var initiator = new ProtocolEncryptionHandshake(infoHash, true);
        var responder = new ProtocolEncryptionHandshake(resolver);

        // Act
        byte[] msg1 = initiator.Initiate();
        byte[] msg2 = responder.HandleIncoming(msg1);
        byte[] msg3 = initiator.HandleIncoming(msg2);
        responder.HandleIncoming(msg3);

        // Assert
        Assert.True(responder.IsError);
    }

    [Fact]
    public void Handshake_WithResolver_MultipleTorrents_MatchesCorrectOne()
    {
        // Arrange
        byte[] targetInfoHash = new byte[20];
        byte[] otherInfoHash1 = new byte[20];
        byte[] otherInfoHash2 = new byte[20];
        RandomNumberGenerator.Fill(targetInfoHash);
        RandomNumberGenerator.Fill(otherInfoHash1);
        RandomNumberGenerator.Fill(otherInfoHash2);

        var resolver = new MockTorrentResolver();
        resolver.AddTorrent(otherInfoHash1);
        resolver.AddTorrent(targetInfoHash);
        resolver.AddTorrent(otherInfoHash2);

        var initiator = new ProtocolEncryptionHandshake(targetInfoHash, true);
        var responder = new ProtocolEncryptionHandshake(resolver);

        byte[] initialPayload = "BitTorrent protocol handshake"u8.ToArray();
        initiator.InitialPayload = initialPayload;

        // Act
        byte[] msg1 = initiator.Initiate();
        byte[] msg2 = responder.HandleIncoming(msg1);
        byte[] msg3 = initiator.HandleIncoming(msg2);
        byte[] msg4 = responder.HandleIncoming(msg3);
        initiator.HandleIncoming(msg4);

        // Assert
        Assert.True(initiator.IsComplete);
        Assert.True(responder.IsComplete);
        Assert.NotNull(responder.MatchedInfoHash);
        Assert.Equal(targetInfoHash, responder.MatchedInfoHash);
        Assert.Equal(initialPayload, responder.ReceivedPayload);
    }

    [Fact]
    public void Handshake_WithResolver_CommunicationWorks()
    {
        // Arrange
        byte[] infoHash = new byte[20];
        RandomNumberGenerator.Fill(infoHash);

        var resolver = new MockTorrentResolver();
        resolver.AddTorrent(infoHash);

        var initiator = new ProtocolEncryptionHandshake(infoHash, true);
        var responder = new ProtocolEncryptionHandshake(resolver);

        // Complete handshake
        byte[] msg1 = initiator.Initiate();
        byte[] msg2 = responder.HandleIncoming(msg1);
        byte[] msg3 = initiator.HandleIncoming(msg2);
        byte[] msg4 = responder.HandleIncoming(msg3);
        initiator.HandleIncoming(msg4);

        Assert.True(initiator.IsComplete);
        Assert.True(responder.IsComplete);

        // Act - Test bidirectional communication
        // Initiator -> Responder
        byte[] messageToResponder = "Hello from initiator!"u8.ToArray();
        byte[] encryptedToResponder = messageToResponder.ToArray();
        initiator.Encryption!.Encrypt(encryptedToResponder, 0, encryptedToResponder.Length);
        responder.Encryption!.Decrypt(encryptedToResponder, 0, encryptedToResponder.Length);

        // Responder -> Initiator
        byte[] messageToInitiator = "Hello from responder!"u8.ToArray();
        byte[] encryptedToInitiator = messageToInitiator.ToArray();
        responder.Encryption.Encrypt(encryptedToInitiator, 0, encryptedToInitiator.Length);
        initiator.Encryption.Decrypt(encryptedToInitiator, 0, encryptedToInitiator.Length);

        // Assert
        Assert.Equal(messageToResponder, encryptedToResponder);
        Assert.Equal(messageToInitiator, encryptedToInitiator);
    }

    [Fact]
    public void Handshake_WithResolver_PartialData_WaitsForComplete()
    {
        // Arrange
        byte[] infoHash = new byte[20];
        RandomNumberGenerator.Fill(infoHash);

        var resolver = new MockTorrentResolver();
        resolver.AddTorrent(infoHash);

        var initiator = new ProtocolEncryptionHandshake(infoHash, true);
        var responder = new ProtocolEncryptionHandshake(resolver);

        // Act - Send partial data (less than 96 bytes for key)
        byte[] msg1 = initiator.Initiate();
        byte[] partialMsg1 = msg1[..50]; // Only 50 bytes

        byte[] response = responder.HandleIncoming(partialMsg1);

        // Assert - Should return empty (waiting for more data)
        Assert.Empty(response);
        Assert.False(responder.IsComplete);
        Assert.False(responder.IsError);

        // Act - Send rest of the data
        byte[] remainingMsg1 = msg1[50..];
        byte[] msg2 = responder.HandleIncoming(remainingMsg1);

        // Assert - Now should respond
        Assert.True(msg2.Length >= 96);
    }

    [Fact]
    public void Handshake_WithResolver_LargeInitialPayload_Succeeds()
    {
        // Arrange
        byte[] infoHash = new byte[20];
        RandomNumberGenerator.Fill(infoHash);

        var resolver = new MockTorrentResolver();
        resolver.AddTorrent(infoHash);

        var initiator = new ProtocolEncryptionHandshake(infoHash, true);
        var responder = new ProtocolEncryptionHandshake(resolver);

        // Standard BT handshake is 68 bytes, let's use something close to that
        byte[] initialPayload = new byte[68];
        RandomNumberGenerator.Fill(initialPayload);
        initiator.InitialPayload = initialPayload;

        // Act
        byte[] msg1 = initiator.Initiate();
        byte[] msg2 = responder.HandleIncoming(msg1);
        byte[] msg3 = initiator.HandleIncoming(msg2);
        byte[] msg4 = responder.HandleIncoming(msg3);
        initiator.HandleIncoming(msg4);

        // Assert
        Assert.True(initiator.IsComplete);
        Assert.True(responder.IsComplete);
        Assert.Equal(initialPayload, responder.ReceivedPayload);
    }

    [Fact]
    public void Constructor_WithResolver_InitializesCorrectly()
    {
        // Arrange
        var resolver = new MockTorrentResolver();

        // Act
        var handshake = new ProtocolEncryptionHandshake(resolver);

        // Assert
        Assert.False(handshake.IsComplete);
        Assert.False(handshake.IsError);
        Assert.Null(handshake.Encryption);
        Assert.Null(handshake.MatchedInfoHash);
        Assert.Null(handshake.ReceivedPayload);
    }

    [Fact]
    public void Dispose_WithResolver_CleansUpResources()
    {
        // Arrange
        var resolver = new MockTorrentResolver();
        var handshake = new ProtocolEncryptionHandshake(resolver);

        // Act
        handshake.Dispose();

        // Assert - Should throw ObjectDisposedException
        Assert.Throws<ObjectDisposedException>(() => handshake.HandleIncoming(new byte[100]));
    }

    [Fact]
    public void Handshake_WithResolver_DisposeDuringHandshake_ThrowsOnNextCall()
    {
        // Arrange
        byte[] infoHash = new byte[20];
        RandomNumberGenerator.Fill(infoHash);

        var resolver = new MockTorrentResolver();
        resolver.AddTorrent(infoHash);

        var initiator = new ProtocolEncryptionHandshake(infoHash, true);
        var responder = new ProtocolEncryptionHandshake(resolver);

        // Start handshake
        byte[] msg1 = initiator.Initiate();
        responder.HandleIncoming(msg1);

        // Act - Dispose during handshake
        responder.Dispose();

        // Assert
        Assert.Throws<ObjectDisposedException>(() => responder.HandleIncoming(new byte[10]));
    }

    #endregion
}





