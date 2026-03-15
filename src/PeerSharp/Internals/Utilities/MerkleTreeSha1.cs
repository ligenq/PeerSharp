using System.Security.Cryptography;

namespace PeerSharp.Internals.Utilities;

/// <summary>
/// <para>BEP 30: Merkle Hash Torrents - SHA-1 Merkle tree implementation.</para>
/// <para>
/// In BEP 30, instead of storing all piece hashes in the torrent file,
/// only the Merkle root is stored. Peers exchange Merkle tree nodes
/// (uncle hashes) to verify pieces.
/// </para>
/// <para>
/// Tree structure:
/// - Leaves are piece hashes (SHA-1 of piece data)
/// - Internal nodes are SHA-1(left child || right child)
/// - Tree is padded to a power of 2 with zero hashes
/// </para>
/// </summary>
internal class MerkleTreeSha1
{
    /// <summary>
    /// Hash size for SHA-1.
    /// </summary>
    public const int HashSize = 20;

    private readonly int _leafStart;
    private readonly byte[]?[] _nodes;

    /// <summary>
    /// Create a new Merkle tree for the given number of pieces.
    /// </summary>
    public MerkleTreeSha1(int pieceCount)
    {
        PieceCount = pieceCount;
        int paddedCount = NextPowerOf2(pieceCount);
        _leafStart = paddedCount - 1;
        TreeSize = (2 * paddedCount) - 1;
        _nodes = new byte[]?[TreeSize];
    }

    /// <summary>
    /// Create a Merkle tree initialized with a known root hash.
    /// </summary>
    public MerkleTreeSha1(int pieceCount, byte[] rootHash) : this(pieceCount)
    {
        if (rootHash.Length == HashSize)
        {
            _nodes[0] = rootHash;
        }
    }

    /// <summary>
    /// Number of pieces (leaves) in the tree.
    /// </summary>
    public int PieceCount { get; }

    /// <summary>
    /// Root hash of the Merkle tree.
    /// </summary>
    public byte[]? Root => _nodes.Length > 0 ? _nodes[0] : null;

    /// <summary>
    /// Total number of nodes in the tree.
    /// </summary>
    public int TreeSize { get; }

    /// <summary>
    /// Compute the piece hash for given piece data.
    /// </summary>
    public static byte[] ComputePieceHash(ReadOnlySpan<byte> pieceData)
    {
        return SHA1.HashData(pieceData);
    }

    /// <summary>
    /// Compute hash of two concatenated hashes.
    /// </summary>
    public static byte[] HashPair(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        Span<byte> combined = stackalloc byte[HashSize * 2];
        left.CopyTo(combined);
        right.CopyTo(combined.Slice(HashSize));
        return SHA1.HashData(combined);
    }

    /// <summary>
    /// Build the tree from all piece hashes (when we have all pieces).
    /// </summary>
    public void BuildFromPieceHashes(List<byte[]> pieceHashes)
    {
        // Set leaf nodes
        for (int i = 0; i < pieceHashes.Count && i < PieceCount; i++)
        {
            _nodes[_leafStart + i] = pieceHashes[i];
        }

        // Fill remaining leaves with zero hashes
        byte[] zeroHash = new byte[HashSize];
        for (int i = pieceHashes.Count; i < NextPowerOf2(PieceCount); i++)
        {
            _nodes[_leafStart + i] = zeroHash;
        }

        // Build internal nodes bottom-up
        for (int i = _leafStart - 1; i >= 0; i--)
        {
            int leftChild = GetLeftChild(i);
            int rightChild = GetRightChild(i);

            byte[]? left = _nodes[leftChild];
            byte[]? right = _nodes[rightChild];

            if (left != null && right != null)
            {
                _nodes[i] = HashPair(left, right);
            }
        }
    }

    /// <summary>
    /// Check if we have all hashes needed to verify a piece.
    /// </summary>
    public bool CanVerifyPiece(int pieceIndex)
    {
        if (pieceIndex < 0 || pieceIndex >= PieceCount)
        {
            return false;
        }

        // Need the piece hash itself
        int nodeIndex = _leafStart + pieceIndex;
        if (_nodes[nodeIndex] == null)
        {
            return false;
        }

        // Need all uncle hashes up to the root
        while (nodeIndex > 0)
        {
            int siblingIndex = GetSibling(nodeIndex);
            if (_nodes[siblingIndex] == null)
            {
                return false;
            }
            nodeIndex = GetParent(nodeIndex);
        }

        return true;
    }

    /// <summary>
    /// Get the depth of the tree (number of levels).
    /// </summary>
    public int GetDepth()
    {
        int paddedCount = NextPowerOf2(PieceCount);
        return Log2(paddedCount) + 1;
    }

    /// <summary>
    /// Get a node hash at the given index.
    /// </summary>
    public byte[]? GetNode(int nodeIndex)
    {
        if (nodeIndex >= 0 && nodeIndex < TreeSize)
        {
            return _nodes[nodeIndex];
        }
        return null;
    }

    /// <summary>
    /// Get a piece hash (leaf node).
    /// </summary>
    public byte[]? GetPieceHash(int pieceIndex)
    {
        if (pieceIndex < 0 || pieceIndex >= PieceCount)
        {
            return null;
        }

        int nodeIndex = _leafStart + pieceIndex;
        return _nodes[nodeIndex];
    }

    /// <summary>
    /// Get the uncle hashes needed to verify a piece.
    /// Returns the hashes from the piece's sibling up to the root's children.
    /// </summary>
    public List<byte[]> GetUncleHashes(int pieceIndex)
    {
        var uncles = new List<byte[]>();

        if (pieceIndex < 0 || pieceIndex >= PieceCount)
        {
            return uncles;
        }

        int nodeIndex = _leafStart + pieceIndex;

        while (nodeIndex > 0)
        {
            int siblingIndex = GetSibling(nodeIndex);
            byte[]? siblingHash = _nodes[siblingIndex];

            if (siblingHash != null)
            {
                uncles.Add(siblingHash);
            }
            else
            {
                // Use zero hash for missing siblings (padding)
                uncles.Add(new byte[HashSize]);
            }

            nodeIndex = GetParent(nodeIndex);
        }

        return uncles;
    }

    /// <summary>
    /// Convert node index to piece index (returns -1 if not a leaf).
    /// </summary>
    public int NodeToPieceIndex(int nodeIndex)
    {
        if (nodeIndex >= _leafStart && nodeIndex < _leafStart + PieceCount)
        {
            return nodeIndex - _leafStart;
        }
        return -1;
    }

    /// <summary>
    /// Convert piece index to node index in the tree.
    /// </summary>
    public int PieceToNodeIndex(int pieceIndex)
    {
        return _leafStart + pieceIndex;
    }

    /// <summary>
    /// Set an internal node hash at the given index.
    /// </summary>
    public void SetNode(int nodeIndex, byte[] hash)
    {
        if (nodeIndex >= 0 && nodeIndex < TreeSize)
        {
            _nodes[nodeIndex] = hash;
        }
    }

    /// <summary>
    /// Set a piece hash (leaf node).
    /// </summary>
    public void SetPieceHash(int pieceIndex, byte[] hash)
    {
        if (pieceIndex < 0 || pieceIndex >= PieceCount)
        {
            return;
        }

        int nodeIndex = _leafStart + pieceIndex;
        _nodes[nodeIndex] = hash;
    }

    /// <summary>
    /// Set uncle hashes for a piece (received from peer).
    /// </summary>
    public void SetUncleHashes(int pieceIndex, List<byte[]> uncles)
    {
        if (pieceIndex < 0 || pieceIndex >= PieceCount)
        {
            return;
        }

        int nodeIndex = _leafStart + pieceIndex;
        int uncleIdx = 0;

        while (nodeIndex > 0 && uncleIdx < uncles.Count)
        {
            int siblingIndex = GetSibling(nodeIndex);
            _nodes[siblingIndex] = uncles[uncleIdx];
            uncleIdx++;
            nodeIndex = GetParent(nodeIndex);
        }
    }

    /// <summary>
    /// Verify a piece against the Merkle tree.
    /// Returns true if the piece hash matches and the path to the root is valid.
    /// </summary>
    public bool VerifyPiece(int pieceIndex, byte[] pieceData)
    {
        return VerifyPiece(pieceIndex, pieceData.AsSpan());
    }

    public bool VerifyPiece(int pieceIndex, ReadOnlySpan<byte> pieceData)
    {
        if (pieceIndex < 0 || pieceIndex >= PieceCount)
        {
            return false;
        }

        if (_nodes[0] == null)
        {
            return false; // No root hash to verify against
        }

        // Compute piece hash
        byte[] pieceHash = SHA1.HashData(pieceData);

        // Store and verify path to root
        int nodeIndex = _leafStart + pieceIndex;
        byte[] currentHash = pieceHash;

        while (nodeIndex > 0)
        {
            int siblingIndex = GetSibling(nodeIndex);
            byte[]? siblingHash = _nodes[siblingIndex];

            if (siblingHash == null)
            {
                // Missing sibling - use zero hash for padding
                siblingHash = new byte[HashSize];
            }

            // Combine with sibling to get parent hash
            if (IsLeftChild(nodeIndex))
            {
                currentHash = HashPair(currentHash, siblingHash);
            }
            else
            {
                currentHash = HashPair(siblingHash, currentHash);
            }

            nodeIndex = GetParent(nodeIndex);
        }

        // Compare computed root with stored root
        return currentHash.AsSpan().SequenceEqual(_nodes[0]);
    }

    private static int GetLeftChild(int index)
    {
        return (2 * index) + 1;
    }

    private static int GetParent(int index)
    {
        return (index - 1) / 2;
    }

    private static int GetRightChild(int index)
    {
        return (2 * index) + 2;
    }

    private static int GetSibling(int index)
    {
        if (index == 0)
        {
            return 0;
        }

        return IsLeftChild(index) ? index + 1 : index - 1;
    }

    private static bool IsLeftChild(int index)
    {
        return index % 2 == 1;
    }

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
