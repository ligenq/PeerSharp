using System.Collections.Concurrent;
using PeerSharp.Internals.Utilities;

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

internal sealed record V2HashRequest(byte[] PiecesRoot, int BaseLayer, int Index, int Length, int ProofLayers);

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

    public ConcurrentDictionary<int, byte[]> PieceLayerHashes { get; } = new();

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

    public long GetPieceSize(int pieceIndex)
    {
        int pieceCount = GetPieceCount();
        if (pieceIndex < 0 || pieceIndex >= pieceCount || PieceSize == 0)
        {
            return 0;
        }

        if (IsV2)
        {
            var file = GetV2FileForPiece(pieceIndex);
            if (file == null)
            {
                return 0;
            }

            long filePieceStart = (long)(pieceIndex - file.FirstPieceIndex) * PieceSize;
            long remaining = file.Size - filePieceStart;
            return Math.Min(PieceSize, Math.Max(0, remaining));
        }

        if (pieceIndex == pieceCount - 1)
        {
            long lastPieceSize = FullSize % PieceSize;
            return lastPieceSize == 0 ? PieceSize : lastPieceSize;
        }

        return PieceSize;
    }

    public TorrentFileEntry? GetV2FileForPiece(int pieceIndex)
    {
        if (!IsV2)
        {
            return null;
        }

        foreach (var file in Files)
        {
            if (file.Size <= 0)
            {
                continue;
            }

            if (pieceIndex >= file.FirstPieceIndex && pieceIndex < file.FirstPieceIndex + file.PieceCount)
            {
                return file;
            }
        }

        return null;
    }

    public byte[]? GetV2ExpectedPieceHash(int pieceIndex)
    {
        var file = GetV2FileForPiece(pieceIndex);
        if (file?.PiecesRoot == null)
        {
            return null;
        }

        int filePieceIndex = pieceIndex - file.FirstPieceIndex;
        if (file.PieceCount == 1)
        {
            return file.PiecesRoot;
        }

        var pieceLayers = file.PieceLayers;
        if (pieceLayers != null && filePieceIndex >= 0 && filePieceIndex < pieceLayers.Count)
        {
            return pieceLayers[filePieceIndex];
        }

        if (file.PieceLayerHashes.TryGetValue(filePieceIndex, out var hash))
        {
            return hash;
        }

        return null;
    }

    public bool ShouldPadV2PieceToPieceSize(int pieceIndex)
    {
        var file = GetV2FileForPiece(pieceIndex);
        return file?.PieceCount > 1;
    }

    public InfoHash GetTrackerInfoHash()
    {
        if (IsV1 && !Hash.IsEmpty)
        {
            return Hash;
        }

        if (IsV2 && !HashV2.IsEmpty)
        {
            return HashV2.TruncateToV1();
        }

        return InfoHash.Empty;
    }

    public byte[]? GetV2Hashes(byte[] piecesRoot, int baseLayer, int index, int length, int proofLayers)
    {
        if (!IsV2 || piecesRoot.Length != Utilities.MerkleTree.HashSize || index < 0 || length <= 0)
        {
            return null;
        }

        int pieceLayerDepth = Utilities.MerkleTree.GetPieceLayerDepth(PieceSize);
        if (baseLayer != pieceLayerDepth)
        {
            return null;
        }

        foreach (var file in Files)
        {
            if (file.PiecesRoot == null || !file.PiecesRoot.AsSpan().SequenceEqual(piecesRoot))
            {
                continue;
            }

            List<byte[]> layer;
            if (file.PieceCount == 1)
            {
                layer = new List<byte[]> { file.PiecesRoot };
            }
            else if (file.PieceLayers != null)
            {
                layer = file.PieceLayers;
            }
            else
            {
                return null;
            }

            // GetPieceLayerHashesWithProof virtually pads entries past the actual piece count
            // with the layer pad hash (BEP 52), so libtorrent-style padded chunk requests
            // (e.g. count = NextPow2(remaining) for the final chunk) can be served.
            return Utilities.MerkleTree.GetPieceLayerHashesWithProof(layer, PieceSize, file.Size, index, length, proofLayers);
        }

        return null;
    }

    public bool TryAddV2Hashes(byte[] piecesRoot, int baseLayer, int index, int length, int proofLayers, byte[] hashes)
    {
        if (!IsV2 ||
            piecesRoot.Length != Utilities.MerkleTree.HashSize ||
            index < 0 ||
            length <= 0 ||
            baseLayer != Utilities.MerkleTree.GetPieceLayerDepth(PieceSize))
        {
            return false;
        }

        foreach (var file in Files)
        {
            if (file.PiecesRoot == null || !file.PiecesRoot.AsSpan().SequenceEqual(piecesRoot))
            {
                continue;
            }

            if (file.PieceCount <= 1)
            {
                return false;
            }

            int paddedLayerSize = Utilities.MerkleTree.CeilingPowerOf2(file.PieceCount);
            if (index >= file.PieceCount || index + length > paddedLayerSize)
            {
                return false;
            }

            int baseTreeLayers = Utilities.MerkleTree.FloorLog2(Utilities.MerkleTree.CeilingPowerOf2(length));
            int proofHashCount = Math.Max(0, proofLayers - baseTreeLayers + 1);
            if (hashes.Length != (length + proofHashCount) * Utilities.MerkleTree.HashSize)
            {
                return false;
            }

            var received = Utilities.MerkleTree.ParsePieceLayer(hashes.AsSpan(0, length * Utilities.MerkleTree.HashSize).ToArray());
            var proof = proofHashCount == 0
                ? new List<byte[]>()
                : Utilities.MerkleTree.ParsePieceLayer(hashes.AsSpan(length * Utilities.MerkleTree.HashSize).ToArray());

            if (!Utilities.MerkleTree.VerifyPieceLayerSubsetAgainstRoot(
                    received, file.PiecesRoot, PieceSize, file.Size, index, proofLayers, proof))
            {
                return false;
            }

            // Store only real piece hashes; entries past file.PieceCount are pad and discarded.
            int realCount = Math.Min(length, file.PieceCount - index);
            for (int i = 0; i < realCount; i++)
            {
                file.PieceLayerHashes[index + i] = received[i];
            }

            // Promote the dictionary into PieceLayers once every real piece is known.
            if (file.PieceLayers == null && file.PieceLayerHashes.Count >= file.PieceCount)
            {
                var complete = new List<byte[]>(file.PieceCount);
                bool full = true;
                for (int i = 0; i < file.PieceCount; i++)
                {
                    if (!file.PieceLayerHashes.TryGetValue(i, out var h))
                    {
                        full = false;
                        break;
                    }
                    complete.Add(h);
                }
                if (full)
                {
                    file.PieceLayers = complete;
                }
            }

            return true;
        }

        return false;
    }

    public V2HashRequest? GetV2HashRequestForPiece(int pieceIndex)
    {
        var file = GetV2FileForPiece(pieceIndex);
        if (file?.PiecesRoot == null || file.PieceCount <= 1)
        {
            return null;
        }

        int filePieceIndex = pieceIndex - file.FirstPieceIndex;

        // Skip if we already have this specific piece's hash, either fully resolved into
        // PieceLayers or cached in PieceLayerHashes from a partial chunk.
        if (file.PieceLayers != null && filePieceIndex < file.PieceLayers.Count)
        {
            return null;
        }
        if (file.PieceLayerHashes.ContainsKey(filePieceIndex))
        {
            return null;
        }

        int chunkStart = filePieceIndex / 512 * 512;
        int remaining = file.PieceCount - chunkStart;
        // BEP 52 / libtorrent: pad chunk length to the next power of two (capped at 512).
        // The sender fills entries past the file's piece count with pad hashes so the
        // chunk forms a balanced sub-tree we can hash up to a known root.
        int chunkLength = Math.Min(512, Utilities.MerkleTree.CeilingPowerOf2(remaining));
        int baseLayer = Utilities.MerkleTree.GetPieceLayerDepth(PieceSize);
        int proofLayers = Utilities.MerkleTree.GetTotalLevels(file.Size) - baseLayer - 1;
        if (proofLayers < 0)
        {
            proofLayers = 0;
        }
        return new V2HashRequest(file.PiecesRoot, baseLayer, chunkStart, chunkLength, proofLayers);
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

    public int GetPieceCount()
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
