using System.Security.Cryptography;

namespace PeerSharp.Internals.Utilities;

/// <summary>
/// <para>BEP 52: Merkle tree implementation for BitTorrent v2.</para>
/// <para>
/// In v2, each file has its own Merkle tree:
/// - Leaves are 16KB block hashes (SHA-256)
/// - Pieces are at a specific layer (determined by piece size)
/// - The root is stored in the torrent file as "pieces root"
/// </para>
/// </summary>
internal static class MerkleTree
{
    /// <summary>
    /// BEP 52: Block size is always 16KB for Merkle tree leaves.
    /// </summary>
    public const int BlockSize = 16384; // 16KB

    /// <summary>
    /// Hash size for SHA-256.
    /// </summary>
    public const int HashSize = 32;

    /// <summary>
    /// Compute leaf hashes for a file's data.
    /// Each leaf is a 16KB block hash.
    /// </summary>
    public static List<byte[]> ComputeLeaves(ReadOnlySpan<byte> data)
    {
        var leaves = new List<byte[]>();
        int offset = 0;

        while (offset < data.Length)
        {
            int blockLen = Math.Min(BlockSize, data.Length - offset);
            var block = data.Slice(offset, blockLen);

            // Pad last block to 16KB if needed
            if (blockLen < BlockSize)
            {
                // Use array instead of stackalloc to avoid potential stack overflow in loop
                var padded = new byte[BlockSize];
                block.CopyTo(padded);
                leaves.Add(SHA256.HashData(padded));
            }
            else
            {
                leaves.Add(SHA256.HashData(block));
            }

            offset += BlockSize;
        }

        return leaves;
    }

    /// <summary>
    /// Compute the Merkle root from a list of leaf hashes.
    /// If the number of leaves is not a power of 2, it's padded with zero hashes.
    /// </summary>
    public static byte[] ComputeRoot(List<byte[]> leaves)
    {
        if (leaves.Count == 0)
        {
            return new byte[HashSize];
        }

        if (leaves.Count == 1)
        {
            return leaves[0];
        }

        // Pad to power of 2
        int paddedCount = NextPowerOf2(leaves.Count);
        var layer = new List<byte[]>(leaves);

        // Pad with zero hashes
        byte[] zeroHash = new byte[HashSize];
        while (layer.Count < paddedCount)
        {
            layer.Add(zeroHash);
        }

        // Build tree bottom-up
        while (layer.Count > 1)
        {
            var nextLayer = new List<byte[]>(layer.Count / 2);
            for (int i = 0; i < layer.Count; i += 2)
            {
                nextLayer.Add(HashPair(layer[i], layer[i + 1]));
            }
            layer = nextLayer;
        }

        return layer[0];
    }

    /// <summary>
    /// Get the layer at a specific depth from leaves.
    /// Depth 0 = leaves, depth 1 = parent of leaves, etc.
    /// </summary>
    public static List<byte[]> GetLayer(List<byte[]> leaves, int depth)
    {
        if (depth == 0)
        {
            return leaves;
        }

        // Pad to power of 2
        int paddedCount = NextPowerOf2(leaves.Count);
        var layer = new List<byte[]>(leaves);

        byte[] zeroHash = new byte[HashSize];
        while (layer.Count < paddedCount)
        {
            layer.Add(zeroHash);
        }

        // Build up to requested depth
        for (int d = 0; d < depth && layer.Count > 1; d++)
        {
            var nextLayer = new List<byte[]>(layer.Count / 2);
            for (int i = 0; i < layer.Count; i += 2)
            {
                nextLayer.Add(HashPair(layer[i], layer[i + 1]));
            }
            layer = nextLayer;
        }

        return layer;
    }

    /// <summary>
    /// Get the piece layer (the Merkle tree layer at piece-sized granularity).
    /// This is what's stored in the "piece layers" dictionary.
    /// </summary>
    public static List<byte[]> GetPieceLayer(List<byte[]> leaves, uint pieceSize)
    {
        int depth = GetPieceLayerDepth(pieceSize);
        return GetLayer(leaves, depth);
    }

    /// <summary>
    /// Calculate the depth of the piece layer in the Merkle tree.
    /// The piece layer is at depth = log2(pieceSize / blockSize).
    /// </summary>
    public static int GetPieceLayerDepth(uint pieceSize)
    {
        int blocksPerPiece = (int)(pieceSize / BlockSize);
        return Log2(blocksPerPiece);
    }

    /// <summary>
    /// Compute SHA-256 hash of data.
    /// </summary>
    public static byte[] HashBlock(ReadOnlySpan<byte> data)
    {
        return SHA256.HashData(data);
    }

    /// <summary>
    /// Compute SHA-256 hash of two concatenated hashes (for internal nodes).
    /// </summary>
    public static byte[] HashPair(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        Span<byte> combined = stackalloc byte[HashSize * 2];
        left.CopyTo(combined);
        right.CopyTo(combined.Slice(HashSize));
        return SHA256.HashData(combined);
    }

    /// <summary>
    /// Parse piece layer data from the torrent file.
    /// Piece layer is a concatenation of 32-byte hashes.
    /// </summary>
    public static List<byte[]> ParsePieceLayer(byte[] layerData)
    {
        var hashes = new List<byte[]>();
        for (int i = 0; i + HashSize <= layerData.Length; i += HashSize)
        {
            var hash = new byte[HashSize];
            Array.Copy(layerData, i, hash, 0, HashSize);
            hashes.Add(hash);
        }
        return hashes;
    }

    /// <summary>
    /// Verify a piece against the Merkle tree.
    /// </summary>
    /// <param name="pieceData">The piece data to verify</param>
    /// <param name="pieceIndex">Index of the piece within the file</param>
    /// <param name="pieceLayerHash">Expected hash from piece layer</param>
    /// <param name="pieceSize">Size of a piece</param>
    /// <returns>True if the piece is valid</returns>
    public static bool VerifyPiece(ReadOnlySpan<byte> pieceData, int pieceIndex, byte[] pieceLayerHash, uint pieceSize)
    {
        // Compute leaves for this piece
        var leaves = ComputeLeaves(pieceData);

        // Get the piece layer (should be a single hash for this piece)
        int depth = GetPieceLayerDepth(pieceSize);
        var layer = GetLayer(leaves, depth);

        if (layer.Count == 0)
        {
            return false;
        }

        // The computed hash should match the expected piece layer hash
        return layer[0].AsSpan().SequenceEqual(pieceLayerHash);
    }

    /// <summary>
    /// Verify a piece layer against a pieces root.
    /// </summary>
    public static bool VerifyPieceLayerAgainstRoot(List<byte[]> pieceLayerHashes, byte[] piecesRoot, uint pieceSize, long fileSize)
    {
        if (pieceLayerHashes.Count == 0)
        {
            return false;
        }

        // Compute how many levels from piece layer to root
        int pieceDepth = GetPieceLayerDepth(pieceSize);
        int totalBlocks = (int)((fileSize + BlockSize - 1) / BlockSize);
        int totalLevels = Log2(NextPowerOf2(totalBlocks));
        int levelsToRoot = totalLevels - pieceDepth;

        // Build from piece layer up to root
        var layer = new List<byte[]>(pieceLayerHashes);

        // Pad to power of 2
        int paddedCount = NextPowerOf2(layer.Count);
        byte[] zeroHash = new byte[HashSize];
        while (layer.Count < paddedCount)
        {
            layer.Add(zeroHash);
        }

        // Build up to root
        for (int i = 0; i < levelsToRoot && layer.Count > 1; i++)
        {
            var nextLayer = new List<byte[]>(layer.Count / 2);
            for (int j = 0; j < layer.Count; j += 2)
            {
                nextLayer.Add(HashPair(layer[j], layer[j + 1]));
            }
            layer = nextLayer;
        }

        if (layer.Count != 1)
        {
            return false;
        }

        return layer[0].AsSpan().SequenceEqual(piecesRoot);
    }

    /// <summary>
    /// Calculate log base 2 of n (floor).
    /// </summary>
    private static int Log2(int n)
    {
        if (n <= 0)
        {
            return 0;
        }

        int log = 0;
        while (n > 1)
        {
            n >>= 1;
            log++;
        }
        return log;
    }

    /// <summary>
    /// Calculate next power of 2 >= n.
    /// </summary>
    private static int NextPowerOf2(int n)
    {
        if (n <= 1)
        {
            return 1;
        }

        n--;
        n |= n >> 1;
        n |= n >> 2;
        n |= n >> 4;
        n |= n >> 8;
        n |= n >> 16;
        return n + 1;
    }
}
