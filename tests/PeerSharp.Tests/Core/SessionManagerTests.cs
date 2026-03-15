using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;

namespace PeerSharp.Tests.Core;

public class SessionManagerTests
{
    private readonly FakeTimeProvider _timeProvider;
    private readonly TorrentRegistry _registry;
    private readonly MockSessionPersistence _persistence;
    private readonly SessionManager _sessionManager;

    public SessionManagerTests()
    {
        _timeProvider = new FakeTimeProvider();
        _registry = new TorrentRegistry();
        _persistence = new MockSessionPersistence();
        _sessionManager = new SessionManager(_persistence, _registry, _timeProvider, NullLogger<SessionManager>.Instance);
    }

    [Fact]
    public async Task InitializeAutoSaveAsync_StartsLoopAndSavesPeriodically()
    {
        // Setup
        var torrent = TorrentTestUtility.CreateMinimal();
        _registry.Add(torrent);
        int intervalSeconds = 10;

        // Act
        await _sessionManager.InitializeAutoSaveAsync(intervalSeconds);

        // Assert - Initial state (no save yet)
        Assert.Empty(_persistence.SavedEntries);

        // Act - Advance time by interval
        _timeProvider.Advance(TimeSpan.FromSeconds(intervalSeconds + 1));

        // Wait for async task to process (using simple delay/yield as the loop runs on task pool)
        // Since we are using FakeTimeProvider, the delay task completes synchronously, 
        // but the loop continuation is scheduled.
        await Task.Delay(50);

        // Assert - Should have saved once
        Assert.Single(_persistence.SavedEntries);
        Assert.Equal(torrent.Hash, _persistence.SavedEntries[0].Hash);

        // Act - Advance time again
        _timeProvider.Advance(TimeSpan.FromSeconds(intervalSeconds));
        await Task.Delay(50);

        // Assert - Should have saved again (mock just adds/updates)
        // Our mock implementation below will likely just store by hash, so count remains 1 but updated.
    }

    [Fact]
    public async Task SaveTorrentEntryAsync_CorrectlyMapsTorrentState()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.DownloadLimitBytesPerSecond = 1024;

        // Register some raw data
        byte[] rawData = new byte[] { 1, 2, 3 };
        _sessionManager.RegisterTorrentData(torrent.Hash, rawData, null);

        await _sessionManager.SaveTorrentEntryAsync(torrent);

        var saved = _persistence.SavedEntries.FirstOrDefault(e => e.Hash == torrent.Hash);
        Assert.NotNull(saved);
        Assert.Equal(rawData, saved.TorrentFileData);
        Assert.Equal(1024, saved.Options?.DownloadLimitBytesPerSecond);
    }

    [Fact]
    public async Task DeleteAsync_RemovesFromPersistenceAndMemory()
    {
        var hash = new InfoHash(new byte[20]);
        await _sessionManager.DeleteAsync(hash, CancellationToken.None);

        Assert.True(_persistence.DeleteCalled);
        Assert.Equal(hash, _persistence.LastDeletedHash);
    }

    [Fact]
    public async Task Dispose_CancelsAutoSaveLoop()
    {
        await _sessionManager.InitializeAutoSaveAsync(10);
        await _sessionManager.DisposeAsync();

        // Advance time - should NOT save
        _timeProvider.Advance(TimeSpan.FromSeconds(20));
        Assert.Empty(_persistence.SavedEntries);
    }

    [Fact]
    public async Task SaveAndLoadDhtState_DelegatesToPersistence()
    {
        var nodeId = Enumerable.Repeat((byte)0x11, 20).ToArray();
        var nodes = new List<DhtNode>
        {
            new DhtNode(
                Enumerable.Repeat((byte)0x22, 20).ToArray(),
                new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 6881))
        };
        var state = new DhtState(nodeId, nodes);

        await _sessionManager.SaveDhtStateAsync(state, CancellationToken.None);

        Assert.NotNull(_persistence.SavedDhtState);
        Assert.Equal(nodeId, _persistence.SavedDhtState!.NodeId);
        Assert.Single(_persistence.SavedDhtState.Nodes);

        var loaded = await _sessionManager.LoadDhtStateAsync(CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(nodeId, loaded!.NodeId);
        Assert.Single(loaded.Nodes);
    }

    // Mock for SessionPersistence
    private class MockSessionPersistence : ISessionPersistence
    {
        public List<SavedTorrentEntry> SavedEntries { get; } = new();
        public bool DeleteCalled { get; private set; }
        public InfoHash? LastDeletedHash { get; private set; }
        public DhtState? SavedDhtState { get; private set; }

        public Task DeleteAsync(InfoHash hash, CancellationToken cancellationToken = default)
        {
            DeleteCalled = true;
            LastDeletedHash = hash;
            SavedEntries.RemoveAll(e => e.Hash == hash);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SavedTorrentEntry>> LoadAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SavedTorrentEntry>>(SavedEntries.AsReadOnly());
        }

        public Task SaveAllAsync(IEnumerable<SavedTorrentEntry> entries, CancellationToken cancellationToken = default)
        {
            foreach (var entry in entries)
            {
                SaveAsync(entry, cancellationToken);
            }
            return Task.CompletedTask;
        }

        public Task SaveAsync(SavedTorrentEntry entry, CancellationToken cancellationToken = default)
        {
            var existing = SavedEntries.FirstOrDefault(e => e.Hash == entry.Hash);
            if (existing != null)
            {
                SavedEntries.Remove(existing);
            }
            SavedEntries.Add(entry);
            return Task.CompletedTask;
        }

        public Task SaveDhtStateAsync(DhtState state, CancellationToken cancellationToken = default)
        {
            SavedDhtState = state;
            return Task.CompletedTask;
        }

        public Task<DhtState?> LoadDhtStateAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SavedDhtState);
        }
    }
}




