using Microsoft.Extensions.Logging;
using PeerSharp.Internals.Utilities;
using PeerSharp.BEncoding;
using PeerSharp.Messages;

namespace PeerSharp.Internals.Extensions;

/// <summary>
/// <para>BEP 30: ut_hash_piece extension for Merkle hash torrents.</para>
/// <para>
/// This extension allows peers to request and exchange Merkle tree hashes
/// for piece verification without having all piece hashes in the torrent file.
/// </para>
/// <para>
/// Message types:
/// - hash_request: Request hashes for a piece (sends base layer + uncle hashes)
/// - hash_piece: Response with piece hash and uncle hashes (Merkle proof)
/// </para>
/// </summary>
internal class UtHashPiece : IUtHashPiece
{
    public const string Name = "ut_hash_piece";
    private readonly ILogger<UtHashPiece> _logger = TorrentLoggerFactory.CreateLogger<UtHashPiece>();

    /// <summary>
    /// Merkle tree for this torrent.
    /// </summary>
    private readonly MerkleTreeSha1? _merkleTree;

    private readonly IPeerCommunication _peer;
    private readonly Torrent _torrent;

    public UtHashPiece(IPeerCommunication peer, Torrent torrent)
    {
        _peer = peer;
        _torrent = torrent;

        // Initialize Merkle tree if this is a BEP 30 torrent
        if (torrent.InfoFile.Info.IsMerkle)
        {
            int pieceCount = torrent.InfoFile.Info.MerklePieceCount;
            if (torrent.InfoFile.Info.MerkleRootHash != null)
            {
                _merkleTree = new MerkleTreeSha1(pieceCount, torrent.InfoFile.Info.MerkleRootHash);
            }
        }
    }

    /// <summary>
    /// Extension message ID assigned by peer in extended handshake.
    /// </summary>
    public int? LocalMessageId { get; private set; }
    public int? RemoteMessageId { get; set; }

    /// <summary>
    /// Check if we can verify a specific piece.
    /// </summary>
    public bool CanVerifyPiece(int pieceIndex)
    {
        return _merkleTree?.CanVerifyPiece(pieceIndex) ?? false;
    }

    /// <summary>
    /// Get the piece hash for a specific piece (if known).
    /// </summary>
    public byte[]? GetPieceHash(int pieceIndex)
    {
        return _merkleTree?.GetPieceHash(pieceIndex);
    }

    /// <summary>
    /// Handle incoming ut_hash_piece message.
    /// </summary>
    public void HandleMessage(byte[] data)
    {
        if (_merkleTree == null)
        {
            return;
        }

        try
        {
            var result = BencodeParser.ParseWithConsumed(data);
            if (result.Node is not BDict dict)
            {
                return;
            }

            int msgType = (int)(dict.GetLong("msg_type") ?? -1);
            int pieceIndex = (int)(dict.GetLong("piece") ?? -1);

            if (pieceIndex < 0 || pieceIndex >= _merkleTree.PieceCount)
            {
                return;
            }

            switch (msgType)
            {
                case 0: // hash_request
                    HandleHashRequest(pieceIndex);
                    break;

                case 1: // hash_piece (response)
                    HandleHashPiece(dict, pieceIndex);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BEP 30: Error parsing hash message from {RemoteEndPoint}", _peer.RemoteEndPoint);
        }
    }

    /// <summary>
    /// Request hashes for a specific piece from the peer.
    /// </summary>
    public void RequestHashes(int pieceIndex)
    {
        if (!RemoteMessageId.HasValue || _merkleTree == null)
        {
            return;
        }

        // BEP 30: hash_request message format:
        // { "msg_type": 0, "piece": <piece_index> }
        var dict = new BDict();
        dict.Dict["msg_type"] = new BNumber(0); // 0 = hash_request
        dict.Dict["piece"] = new BNumber(pieceIndex);

        using var result = BencodeWriter.WriteToResult(dict);

        var msg = new PeerMessage(MessageId.Extended)
        {
            Data = new byte[1 + result.Memory.Length]
        };
        msg.Data[0] = (byte)RemoteMessageId.Value;
        result.Memory.Span.CopyTo(msg.Data.AsSpan(1));

        _ = _peer.SendMessageAsync(msg);
        _logger.LogDebug("BEP 30: Requesting hashes for piece {PieceIndex} from {RemoteEndPoint}", pieceIndex, _peer.RemoteEndPoint);
    }

    /// <summary>
    /// Set a verified piece hash (when we compute it ourselves).
    /// </summary>
    public void SetPieceHash(int pieceIndex, byte[] hash)
    {
        _merkleTree?.SetPieceHash(pieceIndex, hash);
    }

    /// <summary>
    /// Verify a piece against the Merkle tree.
    /// </summary>
    public bool VerifyPiece(int pieceIndex, byte[] pieceData)
    {
        return _merkleTree?.VerifyPiece(pieceIndex, pieceData) ?? false;
    }

    /// <summary>
    /// Handle a hash_piece response from peer.
    /// </summary>
    private void HandleHashPiece(BDict dict, int pieceIndex)
    {
        if (_merkleTree == null)
        {
            return;
        }

        var hashesData = dict.GetBytes("hashes");
        if (hashesData == null)
        {
            return;
        }

        var hashes = hashesData.Value;
        int hashCount = hashes.Length / MerkleTreeSha1.HashSize;

        if (hashCount < 1)
        {
            return;
        }

        // First hash is the piece hash
        byte[] pieceHash = hashes[..MerkleTreeSha1.HashSize].ToArray();
        _merkleTree.SetPieceHash(pieceIndex, pieceHash);

        // Remaining hashes are uncle hashes
        var uncles = new List<byte[]>();
        for (int i = 1; i < hashCount; i++)
        {
            byte[] uncle = hashes.Slice(i * MerkleTreeSha1.HashSize, MerkleTreeSha1.HashSize).ToArray();
            uncles.Add(uncle);
        }

        _merkleTree.SetUncleHashes(pieceIndex, uncles);

        // Also update the shared Merkle tree in Torrent for verification
        var sharedTree = _torrent.MerkleTree;
        if (sharedTree != null)
        {
            sharedTree.SetPieceHash(pieceIndex, pieceHash);
            sharedTree.SetUncleHashes(pieceIndex, uncles);
        }

        _logger.LogDebug("BEP 30: Received {HashCount} hashes for piece {PieceIndex} from {RemoteEndPoint}", hashCount, pieceIndex, _peer.RemoteEndPoint);
    }

    /// <summary>
    /// Handle a hash request from peer.
    /// </summary>
    private void HandleHashRequest(int pieceIndex)
    {
        if (!RemoteMessageId.HasValue || _merkleTree == null)
        {
            return;
        }

        // Only respond if we have this piece verified
        if (!_torrent.Pieces.HasPiece(pieceIndex))
        {
            return;
        }

        byte[]? pieceHash = _merkleTree.GetPieceHash(pieceIndex);
        if (pieceHash == null)
        {
            return;
        }

        // Get uncle hashes for the Merkle proof
        var uncles = _merkleTree.GetUncleHashes(pieceIndex);
        int hashCount = 1 + uncles.Count;
        byte[] hashesData = new byte[hashCount * MerkleTreeSha1.HashSize];
        Array.Copy(pieceHash, 0, hashesData, 0, MerkleTreeSha1.HashSize);
        for (int i = 0; i < uncles.Count; i++)
        {
            Array.Copy(uncles[i], 0, hashesData, (i + 1) * MerkleTreeSha1.HashSize, MerkleTreeSha1.HashSize);
        }

        var dict = new BDict();
        dict.Dict["msg_type"] = new BNumber(1); // 1 = hash_piece
        dict.Dict["piece"] = new BNumber(pieceIndex);
        dict.Dict["hashes"] = new BString(hashesData);

        using var result = BencodeWriter.WriteToResult(dict);

        var msg = new PeerMessage(MessageId.Extended)
        {
            Data = new byte[1 + result.Memory.Length]
        };
        msg.Data[0] = (byte)RemoteMessageId.Value;
        result.Memory.Span.CopyTo(msg.Data.AsSpan(1));

        _ = _peer.SendMessageAsync(msg);
        _logger.LogDebug("BEP 30: Sent hashes for piece {PieceIndex} to {RemoteEndPoint}", pieceIndex, _peer.RemoteEndPoint);
    }

    public void SetLocalMessageId(int id)
    {
        LocalMessageId = id;
    }
}
