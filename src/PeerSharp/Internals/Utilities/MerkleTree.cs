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

            if (blockLen < BlockSize)
            {
                // BEP 52: "If the file size is not a multiple of 16KiB, the last leaf is the SHA-256 hash of the remaining data, zero-padded to 16KiB."
                byte[] padded = new byte[BlockSize];
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
    /// Per BEP 52, this contains exactly file_num_pieces hashes — padding hashes
    /// from the balanced tree are NOT included in the .torrent's "piece layers" entry.
    /// </summary>
    public static List<byte[]> GetPieceLayer(List<byte[]> leaves, uint pieceSize)
    {
        int depth = GetPieceLayerDepth(pieceSize);
        var layer = GetLayer(leaves, depth);
        int blocksPerPiece = (int)(pieceSize / BlockSize);
        if (blocksPerPiece <= 0 || leaves.Count == 0)
        {
            return new List<byte[]>();
        }

        int actualPieceCount = (leaves.Count + blocksPerPiece - 1) / blocksPerPiece;
        if (layer.Count <= actualPieceCount)
        {
            return layer;
        }

        return layer.GetRange(0, actualPieceCount);
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
    /// BEP 52: Returns the merkle padding hash at a given layer depth above the leaf layer.
    /// The leaf-layer pad is 32 zero bytes; each layer up, the pad becomes SHA256(pad || pad).
    /// </summary>
    public static byte[] PadHashAtLayer(int layerDepth)
    {
        byte[] pad = new byte[HashSize];
        for (int i = 0; i < layerDepth; i++)
        {
            pad = HashPair(pad, pad);
        }
        return pad;
    }

    /// <summary>
    /// Smallest power of two greater than or equal to <paramref name="n"/>. Returns 1 for n &lt;= 1.
    /// </summary>
    public static int CeilingPowerOf2(int n) => NextPowerOf2(n);

    /// <summary>
    /// Floor of log2(n). Returns 0 for n &lt;= 0.
    /// </summary>
    public static int FloorLog2(int n) => Log2(n);

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
    public static bool VerifyPiece(ReadOnlySpan<byte> pieceData, int pieceIndex, byte[] pieceLayerHash, uint pieceSize, bool padToPieceSize = false)
    {
        var leaves = ComputeLeaves(pieceData);

        if (padToPieceSize)
        {
            int blocksPerPiece = (int)(pieceSize / BlockSize);
            if (blocksPerPiece <= 0 || leaves.Count > blocksPerPiece)
            {
                return false;
            }

            while (leaves.Count < blocksPerPiece)
            {
                leaves.Add(new byte[HashSize]);
            }
        }

        if (leaves.Count == 0)
        {
            return false;
        }

        var pieceRoot = padToPieceSize
            ? ComputeRoot(leaves)
            : GetLayer(leaves, GetPieceLayerDepth(pieceSize))[0];

        return pieceRoot.AsSpan().SequenceEqual(pieceLayerHash);
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

        int pieceDepth = GetPieceLayerDepth(pieceSize);
        int totalBlocks = (int)((fileSize + BlockSize - 1) / BlockSize);
        int totalLevels = Log2(NextPowerOf2(totalBlocks));
        int levelsToRoot = totalLevels - pieceDepth;

        var layer = new List<byte[]>(pieceLayerHashes);

        // BEP 52: pad with merkle_pad(blocks_per_piece, 1), NOT zero. The piece layer sits
        // above the leaf layer, so the pad value at this depth is the result of hashing
        // a zero leaf with itself pieceDepth times.
        byte[] pad = PadHashAtLayer(pieceDepth);
        int paddedCount = NextPowerOf2(layer.Count);
        while (layer.Count < paddedCount)
        {
            layer.Add(pad);
        }

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
    /// BEP 52: Returns <paramref name="count"/> piece-layer hashes starting at <paramref name="index"/>,
    /// followed by enough sibling (uncle) hashes to anchor the chunk in the file's merkle tree
    /// at <paramref name="proofLayers"/> ancestor layers. Entries past the file's actual piece
    /// count are filled in with the proper layer pad hash so callers don't have to pre-pad.
    /// </summary>
    public static byte[]? GetPieceLayerHashesWithProof(
        List<byte[]> pieceLayerHashes,
        uint pieceSize,
        long fileSize,
        int index,
        int count,
        int proofLayers)
    {
        int pieceDepth = GetPieceLayerDepth(pieceSize);
        if (!ValidateLayerRequest(pieceSize, fileSize, pieceDepth, index, count, proofLayers) ||
            index >= pieceLayerHashes.Count)
        {
            return null;
        }

        byte[] pieceLayerPad = PadHashAtLayer(pieceDepth);
        int baseTreeLayers = Log2(NextPowerOf2(count));
        int proofHashCount = Math.Max(0, proofLayers - baseTreeLayers + 1);
        var result = new byte[(count + proofHashCount) * HashSize];

        for (int i = 0; i < count; i++)
        {
            int absolute = index + i;
            byte[] hash = absolute < pieceLayerHashes.Count ? pieceLayerHashes[absolute] : pieceLayerPad;
            Buffer.BlockCopy(hash, 0, result, i * HashSize, HashSize);
        }

        if (proofHashCount == 0)
        {
            return result;
        }

        var layers = BuildUpperLayers(pieceLayerHashes, pieceDepth);
        int currentIndex = index >> baseTreeLayers;
        int writtenProofs = 0;

        for (int i = baseTreeLayers; i <= proofLayers; i++)
        {
            int currentDepth = pieceDepth + i;

            if (currentDepth >= layers.Count)
            {
                return null;
            }

            int siblingIndex = currentIndex ^ 1;
            byte[] sibling = siblingIndex < layers[currentDepth].Count
                ? layers[currentDepth][siblingIndex]
                : PadHashAtLayer(currentDepth);
            Buffer.BlockCopy(sibling, 0, result, (count + writtenProofs) * HashSize, HashSize);
            writtenProofs++;
            currentIndex /= 2;
        }

        return result;
    }

    /// <summary>
    /// BEP 52: Verify a chunk of piece-layer hashes (plus uncle hashes) against the file's pieces_root.
    /// The received chunk is treated as a sub-tree of <paramref name="pieceLayerHashes"/>.Count leaves
    /// (which the caller may already have padded to a power of two via the pad hash); we then walk
    /// up via <paramref name="proofHashes"/> until we either reach the root or run out of proof.
    /// </summary>
    public static bool VerifyPieceLayerSubsetAgainstRoot(
        IReadOnlyList<byte[]> pieceLayerHashes,
        byte[] piecesRoot,
        uint pieceSize,
        long fileSize,
        int index,
        int proofLayers,
        IReadOnlyList<byte[]> proofHashes)
    {
        int baseLayer = GetPieceLayerDepth(pieceSize);
        if (!ValidateLayerRequest(pieceSize, fileSize, baseLayer, index, pieceLayerHashes.Count, proofLayers))
        {
            return false;
        }

        int totalLevels = GetTotalLevels(fileSize);
        int baseTreeLayers = Log2(NextPowerOf2(pieceLayerHashes.Count));
        int expectedProofHashes = Math.Max(0, proofLayers - baseTreeLayers + 1);
        if (proofHashes.Count != expectedProofHashes)
        {
            return false;
        }

        // To verify against the FILE root, climbing must reach the top: baseTreeLayers via
        // sub-tree reduction + expectedProofHashes via uncle hashes == totalLevels - baseLayer.
        if (baseTreeLayers + expectedProofHashes != totalLevels - baseLayer)
        {
            return false;
        }

        var layer = new List<byte[]>(pieceLayerHashes);
        byte[] pad = PadHashAtLayer(baseLayer);
        int paddedCount = NextPowerOf2(layer.Count);
        while (layer.Count < paddedCount)
        {
            layer.Add(pad);
        }

        while (layer.Count > 1)
        {
            var nextLayer = new List<byte[]>(layer.Count / 2);
            for (int i = 0; i < layer.Count; i += 2)
            {
                nextLayer.Add(HashPair(layer[i], layer[i + 1]));
            }
            layer = nextLayer;
        }

        byte[] current = layer[0];
        int currentIndex = index >> baseTreeLayers;
        int proofIndex = 0;

        for (int i = baseTreeLayers; i <= proofLayers; i++)
        {
            byte[] sibling = proofHashes[proofIndex++];
            current = (currentIndex & 1) == 0 ? HashPair(current, sibling) : HashPair(sibling, current);
            currentIndex /= 2;
        }

        return current.AsSpan().SequenceEqual(piecesRoot);
    }

    public static bool ValidateLayerRequest(uint pieceSize, long fileSize, int baseLayer, int index, int count, int proofLayers)
    {
        if (pieceSize < BlockSize ||
            pieceSize % BlockSize != 0 ||
            baseLayer < 0 ||
            index < 0 ||
            count <= 0 ||
            count > 8192 ||
            proofLayers < 0)
        {
            return false;
        }

        int totalBlocks = (int)((fileSize + BlockSize - 1) / BlockSize);
        int leafCount = NextPowerOf2(totalBlocks);
        int totalLayers = Log2(leafCount);
        if (baseLayer >= totalLayers)
        {
            return false;
        }

        int levelSize = leafCount >> baseLayer;
        return index < levelSize &&
               index + count <= levelSize &&
               proofLayers < totalLayers - baseLayer;
    }

    public static int GetTotalLevels(long fileSize)
    {
        int totalBlocks = (int)((fileSize + BlockSize - 1) / BlockSize);
        return Log2(NextPowerOf2(totalBlocks));
    }

    private static List<List<byte[]>> BuildUpperLayers(List<byte[]> pieceLayerHashes, int baseDepth)
    {
        var layers = new List<List<byte[]>>();
        while (layers.Count <= baseDepth)
        {
            layers.Add(new List<byte[]>());
        }

        var layer = new List<byte[]>(pieceLayerHashes);
        // BEP 52: pad with the pad hash for THIS layer (not zero), so unmerged subtrees produce
        // the same hashes the file's full merkle tree would.
        byte[] pad = PadHashAtLayer(baseDepth);
        int paddedCount = NextPowerOf2(layer.Count);
        while (layer.Count < paddedCount)
        {
            layer.Add(pad);
        }

        layers[baseDepth] = layer;
        while (layer.Count > 1)
        {
            var nextLayer = new List<byte[]>(layer.Count / 2);
            for (int i = 0; i < layer.Count; i += 2)
            {
                nextLayer.Add(HashPair(layer[i], layer[i + 1]));
            }
            layer = nextLayer;
            layers.Add(layer);
        }

        return layers;
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
