using PeerSharp.Internals;

namespace PeerSharp.Tests.Core;

public class TorrentRegistryTests
{
    private readonly TorrentRegistry _registry;

    public TorrentRegistryTests()
    {
        _registry = new TorrentRegistry();
    }

    [Fact]
    public void Add_AddsTorrent_CanBeRetrieved()
    {
        var torrent = CreateV1Torrent();

        _registry.Add(torrent);

        Assert.True(_registry.Contains(torrent.Hash));
        Assert.True(_registry.TryGet(torrent.Hash, out var retrieved));
        Assert.Same(torrent, retrieved);
        Assert.Single(_registry.GetAll());
    }

    [Fact]
    public void TryGetForRouting_FindsAV2TorrentByItsTruncatedHash()
    {
        // BEP 52 gives a v2 torrent two identities, and the twenty-byte one is what the world uses:
        // the peer handshake, tracker announces and DHT lookups all have room for nothing else.
        // Matching only the stored hashes meant a v2 torrent was unreachable by the only name any of
        // them could ask for - libtorrent's connection_tester got "ERROR READ HANDSHAKE: End of file"
        // because the inbound connection resolved to no torrent and was dropped mid-handshake.
        var torrent = CreateV2Torrent();
        _registry.Add(torrent);

        var handshakeHash = torrent.HashV2.TruncateToV1();

        Assert.True(_registry.TryGetForRouting(handshakeHash, out var routed));
        Assert.Same(torrent, routed);
    }

    [Fact]
    public void TryGetForRouting_FindsAHybridTorrentByEitherOfItsHashes()
    {
        // A hybrid torrent was half-reachable: fine for a peer that used its v1 hash, invisible to
        // one that used the truncated v2.
        var torrent = CreateV2Torrent(withV1Hash: true);
        _registry.Add(torrent);

        Assert.True(_registry.TryGetForRouting(torrent.Hash, out _));
        Assert.True(_registry.TryGetForRouting(torrent.HashV2.TruncateToV1(), out var byV2));
        Assert.Same(torrent, byV2);
    }

    [Fact]
    public void TryGetForRouting_DoesNotMatchAnUnrelatedHash()
    {
        // The truncation must not turn the lookup into something that matches loosely.
        var torrent = CreateV2Torrent();
        _registry.Add(torrent);

        Assert.False(_registry.TryGetForRouting(InfoHash.CreateRandom(), out _));
        Assert.False(_registry.TryGetForRouting(InfoHash.CreateRandomV2(), out _));
    }

    /// <summary>
    /// A torrent with a hash it could actually be looked up by.
    /// </summary>
    /// <remarks>
    /// CreateMinimal leaves the info hash at InfoHash.Empty, which is how absence is stored. A
    /// registry that answers a lookup for the empty hash answers with whichever torrent happens to
    /// lack a hash of that version, so it no longer does, and a torrent registered without one
    /// cannot be found by one.
    /// </remarks>
    private static Torrent CreateV1Torrent()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Hash = InfoHash.CreateRandom();
        metadata.Info.PieceSize = 16384;

        return TorrentTestUtility.CreateMinimal(metadata);
    }

    private static Torrent CreateV2Torrent(bool withV1Hash = false)
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Version = withV1Hash ? TorrentVersion.Hybrid : TorrentVersion.V2;
        metadata.Info.HashV2 = InfoHash.CreateRandomV2();
        if (withV1Hash)
        {
            metadata.Info.Hash = InfoHash.CreateRandom();
        }

        metadata.Info.PieceSize = 16384;
        metadata.Info.FullSize = 16384;
        metadata.Info.Files.Add(new PeerSharp.Internals.TorrentFileEntry { Path = "file.bin", Size = 16384, Offset = 0 });

        return TorrentTestUtility.CreateMinimal(metadata);
    }

    [Fact]
    public void Add_DuplicateHash_ThrowsTorrentAlreadyExistsException()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        _registry.Add(torrent);

        Assert.Throws<TorrentAlreadyExistsException>(() => _registry.Add(torrent));
    }

    [Fact]
    public void Remove_ExistingTorrent_ReturnsTrueAndRemoves()
    {
        var torrent = CreateV1Torrent();
        _registry.Add(torrent);

        bool removed = _registry.Remove(torrent.Hash, out var removedTorrent);

        Assert.True(removed);
        Assert.Same(torrent, removedTorrent);
        Assert.False(_registry.Contains(torrent.Hash));
        Assert.Empty(_registry.GetAll());
    }

    [Fact]
    public void Remove_NonExistentTorrent_ReturnsFalse()
    {
        var hash = new InfoHash(new byte[20]);
        bool removed = _registry.Remove(hash, out var removedTorrent);

        Assert.False(removed);
        Assert.Null(removedTorrent);
    }

    [Fact]
    public void TryGet_NonExistentTorrent_ReturnsFalse()
    {
        var hash = new InfoHash(new byte[20]);
        bool found = _registry.TryGet(hash, out var torrent);

        Assert.False(found);
        Assert.Null(torrent);
    }

    [Fact]
    public void Concurrent_AddAndRemove_MaintainsConsistency()
    {
        // This test attempts to find race conditions by hammering the registry
        const int threadCount = 10;
        const int operationsPerThread = 1000;

        // We will add and remove torrents with random hashes concurrently
        Parallel.For(0, threadCount, t =>
        {
            for (int i = 0; i < operationsPerThread; i++)
            {
                // Create a unique torrent for this operation to avoid collision on add
                var metadata = new TorrentFileMetadata { Info = { Hash = new InfoHash(Guid.NewGuid().ToByteArray().Concat(new byte[4]).ToArray()) } };
                var torrent = TorrentTestUtility.CreateMinimal(metadata);

                _registry.Add(torrent);
                Assert.True(_registry.Contains(torrent.Hash));

                bool removed = _registry.Remove(torrent.Hash, out _);
                Assert.True(removed);
            }
        });

        Assert.Empty(_registry.GetAll());
        Assert.Equal(0, _registry.Count);
    }
}




