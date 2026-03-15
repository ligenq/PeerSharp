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
        var torrent = TorrentTestUtility.CreateMinimal();

        _registry.Add(torrent);

        Assert.True(_registry.Contains(torrent.Hash));
        Assert.True(_registry.TryGet(torrent.Hash, out var retrieved));
        Assert.Same(torrent, retrieved);
        Assert.Single(_registry.GetAll());
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
        var torrent = TorrentTestUtility.CreateMinimal();
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
        int threadCount = 10;
        int operationsPerThread = 1000;

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




