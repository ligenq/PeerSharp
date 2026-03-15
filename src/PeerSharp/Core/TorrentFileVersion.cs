namespace PeerSharp.Core;

/// <summary>
/// Specifies which BitTorrent metadata version to emit.
/// </summary>
public enum TorrentFileVersion
{
    /// <summary>
    /// V1 metadata only (SHA-1 piece hashes).
    /// </summary>
    V1 = 1,

    /// <summary>
    /// V2 metadata only (SHA-256 Merkle trees).
    /// </summary>
    V2 = 2,

    /// <summary>
    /// Hybrid metadata (both V1 and V2).
    /// </summary>
    Hybrid = 3
}

