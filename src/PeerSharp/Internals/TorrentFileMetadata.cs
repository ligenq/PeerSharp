namespace PeerSharp.Internals;

/// <summary>
/// BEP 52: Torrent protocol version
/// </summary>
internal enum TorrentVersion
{
    /// <summary>V1 only (SHA-1 piece hashes)</summary>
    V1 = 1,

    /// <summary>V2 only (SHA-256 Merkle tree)</summary>
    V2 = 2,

    /// <summary>Hybrid torrent (both V1 and V2 compatible)</summary>
    Hybrid = 3
}

internal class TorrentFileEntry
{
    /// <summary>
    /// BEP 52: Index of this file's first piece in the torrent.
    /// In V2, files are piece-aligned.
    /// </summary>
    public int FirstPieceIndex { get; set; }

    public long Offset { get; set; }
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// BEP 52: Number of pieces this file spans.
    /// </summary>
    public int PieceCount { get; set; }

    /// <summary>
    /// BEP 52: Piece layers - the Merkle tree layer containing piece-sized hashes.
    /// </summary>
    public List<byte[]>? PieceLayers { get; internal set; }

    /// <summary>
    /// BEP 52: Per-file Merkle tree root hash (32 bytes SHA-256).
    /// Only present in V2/hybrid torrents.
    /// </summary>
    public byte[]? PiecesRoot { get; set; }

    public long Size { get; set; }

    /// <summary>
    /// BEP 47: True if this entry is a padding file.
    /// </summary>
    public bool IsPadding { get; set; }
}

internal class TorrentFileInfo
{
    public List<TorrentFileEntry> Files { get; set; } = new();

    public long FullSize { get; set; }

    /// <summary>V1 info hash (20 bytes SHA-1)</summary>
    public InfoHash Hash { get; set; } = InfoHash.Empty;

    /// <summary>BEP 52: V2 info hash (32 bytes SHA-256)</summary>
    public InfoHash HashV2 { get; set; } = InfoHash.EmptyV2;

    /// <summary>
    /// BEP 30: True if this is a Merkle hash torrent.
    /// </summary>
    public bool IsMerkle => MerkleRootHash?.Length == 20;

    /// <summary>
    /// BEP 17: Private torrent flag. When true, DHT and PEX must not be used.
    /// </summary>
    public bool IsPrivate { get; set; }

    /// <summary>
    /// BEP 52: Returns true if this torrent supports V1 protocol
    /// </summary>
    public bool IsV1 => Version == TorrentVersion.V1 || Version == TorrentVersion.Hybrid;

    /// <summary>
    /// BEP 52: Returns true if this torrent supports V2 protocol
    /// </summary>
    public bool IsV2 => Version == TorrentVersion.V2 || Version == TorrentVersion.Hybrid;

    /// <summary>
    /// BEP 30: Number of pieces in this torrent (derived from file size and piece size).
    /// </summary>
    public int MerklePieceCount => PieceSize > 0 ? (int)((FullSize + PieceSize - 1) / PieceSize) : 0;

    /// <summary>
    /// BEP 30: Merkle root hash (20 bytes SHA-1). Present instead of pieces list.
    /// </summary>
    public byte[]? MerkleRootHash { get; set; }

    /// <summary>
    /// BEP 30: Cached Merkle tree hashes. Populated as hashes are received from peers.
    /// Index 0 = root, then level by level down to piece hashes.
    /// </summary>
    public List<byte[]?> MerkleTree { get; set; } = new();

    public string Name { get; set; } = string.Empty;

    /// <summary>V1: SHA-1 piece hashes (20 bytes each)</summary>
    public List<byte[]> Pieces { get; set; } = new();

    public uint PieceSize { get; set; }

    /// <summary>BEP 52: Protocol version of this torrent</summary>
    public TorrentVersion Version { get; set; } = TorrentVersion.V1;

    /// <summary>
    /// Gets all file indices that a specific piece touches.
    /// </summary>
    public List<int> GetFilesForPiece(int pieceIndex)
    {
        var result = new List<int>();
        int pieceCount = GetPieceCount();
        if (pieceIndex < 0 || pieceIndex >= pieceCount || PieceSize == 0 || pieceCount == 0)
        {
            return result;
        }

        long pieceStart = pieceIndex * PieceSize;
        long pieceEnd = pieceStart + PieceSize - 1;

        // Handle last piece being smaller
        if (pieceEnd >= FullSize)
        {
            pieceEnd = FullSize - 1;
        }

        for (int i = 0; i < Files.Count; i++)
        {
            var file = Files[i];
            if (file.IsPadding)
            {
                continue;
            }
            long fileEnd = file.Offset + file.Size - 1;

            // Check if piece overlaps with file
            if (pieceStart <= fileEnd && pieceEnd >= file.Offset)
            {
                result.Add(i);
            }

            // Optimization: if file starts after piece ends, no more files will overlap
            if (file.Offset > pieceEnd)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// Gets the priority for a piece based on file selection.
    /// Returns the highest priority among all files the piece touches.
    /// </summary>
    public Priority GetPiecePriority(int pieceIndex, IReadOnlyList<FileSelection>? selection)
    {
        if (selection == null || selection.Count == 0)
        {
            return Priority.Normal;
        }

        var fileIndices = GetFilesForPiece(pieceIndex);
        var highestPriority = Priority.DoNotDownload;

        foreach (var fileIdx in fileIndices)
        {
            if (fileIdx < selection.Count)
            {
                var sel = selection[fileIdx];
                if (sel.Selected && sel.Priority > highestPriority)
                {
                    highestPriority = sel.Priority;
                }
            }
            else
            {
                // Default is Normal priority
                if (Priority.Normal > highestPriority)
                {
                    highestPriority = Priority.Normal;
                }
            }
        }

        return highestPriority;
    }

    /// <summary>
    /// Gets the piece range (first and last piece indices) that contain data for a specific file.
    /// </summary>
    public (int firstPiece, int lastPiece) GetPieceRangeForFile(int fileIndex)
    {
        int pieceCount = GetPieceCount();
        if (fileIndex < 0 || fileIndex >= Files.Count || PieceSize == 0 || pieceCount == 0)
        {
            return (-1, -1);
        }

        var file = Files[fileIndex];
        int firstPiece = (int)(file.Offset / PieceSize);
        int lastPiece = (int)((file.Offset + file.Size - 1) / PieceSize);

        // Clamp to valid piece range
        int maxPiece = pieceCount - 1;
        if (lastPiece > maxPiece)
        {
            lastPiece = maxPiece;
        }

        return (firstPiece, lastPiece);
    }

    /// <summary>
    /// Checks if a piece is needed based on file selection.
    /// A piece is needed if any file it touches is selected (not DoNotDownload).
    /// </summary>
    public bool IsPieceNeeded(int pieceIndex, IReadOnlyList<FileSelection>? selection)
    {
        if (selection == null || selection.Count == 0)
        {
            return true; // No selection = download everything
        }

        var fileIndices = GetFilesForPiece(pieceIndex);
        foreach (var fileIdx in fileIndices)
        {
            if (fileIdx < selection.Count)
            {
                var sel = selection[fileIdx];
                if (sel.Selected && sel.Priority != Priority.DoNotDownload)
                {
                    return true;
                }
            }
            else
            {
                // File index beyond selection list = assume selected (default)
                return true;
            }
        }

        return false;
    }

    public int GetVisibleFileCount()
    {
        int count = 0;
        for (int i = 0; i < Files.Count; i++)
        {
            if (!Files[i].IsPadding)
            {
                count++;
            }
        }
        return count;
    }

    public int MapVisibleIndexToInternal(int visibleIndex)
    {
        if (visibleIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(visibleIndex));
        }

        int current = 0;
        for (int i = 0; i < Files.Count; i++)
        {
            if (Files[i].IsPadding)
            {
                continue;
            }

            if (current == visibleIndex)
            {
                return i;
            }
            current++;
        }

        throw new ArgumentOutOfRangeException(nameof(visibleIndex));
    }

    public List<int> GetVisibleFileIndices()
    {
        var indices = new List<int>();
        for (int i = 0; i < Files.Count; i++)
        {
            if (!Files[i].IsPadding)
            {
                indices.Add(i);
            }
        }
        return indices;
    }

    public bool TryMapInternalIndexToVisible(int internalIndex, out int visibleIndex)
    {
        visibleIndex = -1;
        if (internalIndex < 0 || internalIndex >= Files.Count)
        {
            return false;
        }

        int current = 0;
        for (int i = 0; i < Files.Count; i++)
        {
            if (Files[i].IsPadding)
            {
                continue;
            }

            if (i == internalIndex)
            {
                visibleIndex = current;
                return true;
            }
            current++;
        }

        return false;
    }

    private int GetPieceCount()
    {
        if (PieceSize == 0)
        {
            return 0;
        }

        if (Pieces.Count > 0)
        {
            return Pieces.Count;
        }

        return (int)((FullSize + PieceSize - 1) / PieceSize);
    }
}

internal class TorrentFileMetadata
{
    public string Announce { get; set; } = string.Empty;
    public List<string> AnnounceList { get; set; } = new();
    /// <summary>
    /// BEP 12: Tracker tiers (announce-list). Each inner list is a tier.
    /// </summary>
    public List<List<string>> AnnounceTiers { get; set; } = new();
    public TorrentFileInfo Info { get; set; } = new();
    public byte[]? InfoBytes { get; set; }

    /// <summary>
    /// BEP 52: Piece layers dictionary (file root -> layer hashes).
    /// Keys are 32-byte file piece roots, values are concatenated 32-byte hashes.
    /// </summary>
    public Dictionary<byte[], byte[]> PieceLayers { get; set; } = new(new ByteArrayComparer());

    /// <summary>
    /// BEP 19: List of HTTP/FTP web seed URLs for downloading torrent content.
    /// </summary>
    public List<string> WebSeedUrls { get; set; } = new();

    /// <summary>
    /// Byte array comparer for dictionary keys.
    /// </summary>
    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y)
        {
            if (x == null || y == null)
            {
                return x == y;
            }

            if (x.Length != y.Length)
            {
                return false;
            }

            for (int i = 0; i < x.Length; i++)
            {
                if (x[i] != y[i])
                {
                    return false;
                }
            }
            return true;
        }

        public int GetHashCode(byte[] obj)
        {
            if (obj == null || obj.Length == 0)
            {
                return 0;
            }

            int hash = 17;
            for (int i = 0; i < Math.Min(8, obj.Length); i++)
            {
                hash = (hash * 31) + obj[i];
            }
            return hash;
        }
    }
}
