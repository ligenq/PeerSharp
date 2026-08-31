namespace PeerSharp.Tests.Api;

/// <summary>
/// Which hashes name which torrents, and which name nothing at all.
/// </summary>
/// <remarks>
/// An absent hash version is stored as all zeros, and all zeros equals all zeros, so asking
/// <c>==</c> whether two torrents share a v2 hash answers yes for every pair that has none. That is
/// true of the bytes and false of the torrents, and it is the reason <see cref="InfoHash.Matches"/>
/// exists alongside the operator rather than replacing it.
/// </remarks>
public class TorrentIdentityTests
{
    private static InfoHash V1(byte fill) => new(Enumerable.Repeat(fill, InfoHash.V1Length).ToArray());

    private static InfoHash V2(byte fill) => new(Enumerable.Repeat(fill, InfoHash.V2Length).ToArray());

    [Fact]
    public void Matches_TwoAbsentHashes_IsNotIdentity()
    {
        Assert.True(InfoHash.Empty == InfoHash.Empty);
        Assert.False(InfoHash.Empty.Matches(InfoHash.Empty));
        Assert.False(InfoHash.EmptyV2.Matches(InfoHash.EmptyV2));
    }

    [Fact]
    public void Matches_OneAbsentHash_IsNotIdentity()
    {
        Assert.False(V1(0x11).Matches(InfoHash.Empty));
        Assert.False(InfoHash.Empty.Matches(V1(0x11)));
    }

    [Fact]
    public void Matches_TheSameHash_IsIdentity()
    {
        Assert.True(V1(0x11).Matches(V1(0x11)));
        Assert.True(V2(0x22).Matches(V2(0x22)));
    }

    [Fact]
    public void Matches_DifferentHashes_IsNot()
    {
        Assert.False(V1(0x11).Matches(V1(0x12)));
        Assert.False(V1(0x11).Matches(V2(0x11)));
    }

    [Fact]
    public void TryFromHex_AllZeros_IsRefused()
    {
        // Absence must not be able to arrive from outside and be used as a lookup key: it would
        // match whichever torrent happens to lack a hash of that version.
        Assert.False(InfoHash.TryFromHex(new string('0', InfoHash.V1Length * 2), out _));
        Assert.False(InfoHash.TryFromHex(new string('0', InfoHash.V2Length * 2), out _));
    }

    [Fact]
    public void HasHash_FindsAV1TorrentByItsHash()
    {
        var torrent = new FakeTorrent { Hash = V1(0x11) };

        Assert.True(TorrentIdentity.HasHash(torrent, V1(0x11)));
        Assert.False(TorrentIdentity.HasHash(torrent, V1(0x12)));
    }

    [Fact]
    public void HasHash_FindsAV2OnlyTorrentByItsV2Hash()
    {
        var torrent = new FakeTorrent { Hash = InfoHash.Empty, HashV2 = V2(0x22) };

        Assert.True(TorrentIdentity.HasHash(torrent, V2(0x22)));
    }

    [Fact]
    public void HasHash_FindsAV2OnlyTorrentByTheTruncationTheWorldUses()
    {
        // BEP 52: everything with a twenty byte field refers to a v2 torrent by its hash cut to
        // twenty bytes, which is not the hash the torrent stores.
        var torrent = new FakeTorrent { Hash = InfoHash.Empty, HashV2 = V2(0x22) };

        Assert.True(TorrentIdentity.HasHash(torrent, V2(0x22).TruncateToV1()));
    }

    [Fact]
    public void HasHash_AnAbsentHash_FindsNothing()
    {
        // The one that mattered. A v2 only torrent stores InfoHash.Empty as its v1 hash, so asking
        // for the empty hash used to answer with the first such torrent - and callers do things to
        // whatever they are answered with.
        var v2Only = new FakeTorrent { Hash = InfoHash.Empty, HashV2 = V2(0x22) };
        var v1Only = new FakeTorrent { Hash = V1(0x11) };

        Assert.False(TorrentIdentity.HasHash(v2Only, InfoHash.Empty));
        Assert.False(TorrentIdentity.HasHash(v1Only, InfoHash.EmptyV2));
    }

    [Fact]
    public void HasSameIdentity_TwoTorrentsThatMerelyLackTheSameVersion_AreNotTheSame()
    {
        var first = new FakeTorrent { Hash = InfoHash.Empty, HashV2 = V2(0x22) };
        var second = new FakeTorrent { Hash = InfoHash.Empty, HashV2 = V2(0x33) };

        Assert.False(first.HasSameIdentity(second));
    }

    [Fact]
    public void HasSameIdentity_ATorrentWithoutAUsableHash_IsStillItself()
    {
        var torrent = new FakeTorrent();

        Assert.True(torrent.HasSameIdentity(torrent));
    }

    [Fact]
    public void HasSameIdentity_AHybridAndTheV1TorrentSharingItsHash_AreTheSame()
    {
        var hybrid = new FakeTorrent { Hash = V1(0x11), HashV2 = V2(0x22) };
        var v1Only = new FakeTorrent { Hash = V1(0x11) };

        Assert.True(hybrid.HasSameIdentity(v1Only));
    }

    private sealed class FakeTorrent : ITorrent
    {
        public bool SuperSeeding { get; set; }

        public int MaxConnections { get; set; }

        public int MaxUploadSlots { get; set; }

        public IWebSeeds WebSeeds => throw new NotSupportedException();

        public Task MoveStorageAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RenameFileAsync(int fileIndex, string newPath, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public IReadOnlyDictionary<int, string> GetRenamedFiles() => new Dictionary<int, string>();

        public Task<byte[]> ReadPieceAsync(int pieceIndex, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());

        public void SetPiecePriority(int pieceIndex, Priority priority) { }

        public Priority GetPiecePriority(int pieceIndex) => Priority.Normal;

        public void ClearPiecePriorities() { }
        public bool HasSameIdentity(ITorrent? other)
        {
            // The same answer the library gives, asked the same way.
            return other != null
                && (ReferenceEquals(this, other) || Hash.Matches(other.Hash) || HashV2.Matches(other.HashV2));
        }

        // Nothing here is about files, so the identity tests should not have to describe any.
        public FakeTorrent()
            : this("identity.bin", [])
        {
        }

        private readonly byte[] _data;
        private readonly IReadOnlyList<TorrentFileInfo> _files;
        private readonly Func<Stream>? _streamFactory;

        public FakeTorrent(string path, byte[] data, Func<Stream>? streamFactory = null)
        {
            _data = data;
            _streamFactory = streamFactory;
            _files = [new TorrentFileInfo(path, data.Length, 0, data.Length)];
        }

        public long DataLeft => 0;
        public long DownloadLimitBytesPerSecond { get; set; }
        public long DiskReadLimitBytesPerSecond { get; set; }
        public long DiskWriteLimitBytesPerSecond { get; set; }
        public DownloadStrategy DownloadStrategy { get; set; }
        public ITorrentEvents? Events => null;
        public int FileCount => _files.Count;
        public IFiles Files => throw new NotImplementedException();
        public IFileTransfer FileTransfer => throw new NotImplementedException();
        public bool Finished => true;
        public ulong FinishedBytes => (ulong)_data.Length;
        public ulong FinishedSelectedBytes => (ulong)_data.Length;
        public InfoHash Hash { get; init; } = InfoHash.Empty;
        public InfoHash HashV2 { get; init; } = InfoHash.EmptyV2;
        public bool HasMetadata => true;
        public bool HasStreamableFiles => true;
        public Exception? LastException => null;
        public IMetadataDownload? MetadataDownload => null;
        public string Name => _files[0].Path;
        public IPeers Peers => throw new NotImplementedException();
        public int PieceCount => 1;
        public uint PieceSize => (uint)_data.Length;
        public int PiecesReceived => 1;
        public float Progress => 1;
        public bool QueueAutoStart { get; set; }
        public int QueuePriority { get; set; }
        public float? RatioLimit { get; set; }
        public TimeSpan? SeedTimeLimit { get; set; }
        public bool SelectionFinished => true;
        public float SelectionProgress => 1;
        public bool Started => true;
        public TorrentState State => TorrentState.Active;
        public DateTimeOffset StateTimestamp => DateTimeOffset.UtcNow;
        public IReadOnlyList<int> StreamableFileIndices => [0];
        public DateTimeOffset TimeAdded => DateTimeOffset.UtcNow;
        public long TotalSize => _data.Length;
        public ITrackers Trackers => throw new NotImplementedException();
        public long UploadLimitBytesPerSecond { get; set; }

        public Task<int> ForceRecheckAsync(IProgress<PieceCheckProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public IReadOnlyList<TorrentFileInfo> GetAllFileInfo() => _files;
        public IReadOnlyList<FileSelection> GetAllFileSelections() => Array.Empty<FileSelection>();
        public TorrentFileInfo GetFileInfo(int fileIndex) => _files[fileIndex];
        public FileSelection GetFileSelection(int fileIndex) => throw new NotImplementedException();
        public byte[] GetPieceBitfield() => [0x80];
        public TorrentResumeData GetResumeData() => throw new NotImplementedException();
        public Task<Stream> OpenStreamAsync(int fileIndex, CancellationToken cancellationToken = default) =>
            Task.FromResult(_streamFactory?.Invoke() ?? new MemoryStream(_data, writable: false));

        public Task SetAllFilesPriorityAsync(Priority priority, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetDownloadPathAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetFilePriorityAsync(int fileIndex, Priority priority, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetFileSelectionAsync(int fileIndex, FileSelection selection, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WaitForMetadataAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public TorrentFile ExportTorrentFile() => throw new NotImplementedException();
        public void RegisterPeerTransport(IPeerTransport transport) => throw new NotImplementedException();
    }
}
