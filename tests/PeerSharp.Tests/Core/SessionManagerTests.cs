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

    /// <summary>
    /// The auto-save loop wakes on its interval and writes resume data.
    ///
    /// <para>
    /// Two races make this awkward to observe, and waiting a fixed moment of real time loses to both.
    /// The loop is started as a task, so advancing the fake clock before it reaches its
    /// <c>Task.Delay</c> registers nothing and that tick is simply missed - no amount of subsequent
    /// waiting would produce a save. And once the timer does fire, the continuation still has to be
    /// picked up off the thread pool, which on a loaded CI machine takes longer than the fifty
    /// milliseconds this used to allow. It failed in CI for the second reason.
    /// </para>
    ///
    /// <para>
    /// Advancing repeatedly until the save appears covers both: a missed tick is caught by the next
    /// advance, and a late continuation by the next poll. Repeats are harmless here because the double
    /// keys entries by hash, so saving the same torrent twice leaves one entry.
    /// </para>
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task InitializeAutoSaveAsync_StartsLoopAndSavesPeriodically()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        _registry.Add(torrent);
        const int intervalSeconds = 10;

        await _sessionManager.InitializeAutoSaveAsync(intervalSeconds);

        // Nothing is written before the first interval elapses.
        Assert.Empty(_persistence.SavedEntries);

        await TorrentTestUtility.AdvanceUntilAsync(
            _timeProvider,
            () => _persistence.SavedEntries.Count > 0,
            TimeSpan.FromSeconds(intervalSeconds + 1),
            "the auto-save loop to write resume data");

        Assert.Single(_persistence.SavedEntries);
        Assert.Equal(torrent.Hash, _persistence.SavedEntries[0].Hash);
    }



    [Fact]
    public async Task SaveTorrentEntryAsync_CorrectlyMapsTorrentState()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.DownloadLimitBytesPerSecond = 1024;

        // Register some raw data
        byte[] rawData = [1, 2, 3];
        _sessionManager.RegisterTorrentData(torrent.Hash, rawData, null);

        await _sessionManager.SaveTorrentEntryAsync(torrent);

        var saved = _persistence.SavedEntries.FirstOrDefault(e => e.Hash == torrent.Hash);
        Assert.NotNull(saved);
        Assert.Equal(rawData, saved.TorrentFileData);
        Assert.Equal(1024, saved.Options?.DownloadLimitBytesPerSecond);
    }

    [Fact]
    public async Task SaveTorrentEntryAsync_PersistsOnlyLearnedPeerPreferences()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.PeersInternal.ImportConnectionPreferences(
        [
            new SavedPeerPreference("192.0.2.10", 6881, UtpSupported: false),
            new SavedPeerPreference("192.0.2.11", 6882),
            new SavedPeerPreference("2001:db8::10", 6883, OfferEncryptionNext: false)
        ]);

        await _sessionManager.SaveTorrentEntryAsync(torrent);

        var saved = Assert.Single(_persistence.SavedEntries);
        Assert.Collection(
            saved.Options!.PeerPreferences!.OrderBy(preference => preference.Port),
            preference => Assert.False(preference.UtpSupported),
            preference => Assert.False(preference.OfferEncryptionNext));
    }

    [Fact]
    public void ImportConnectionPreferences_ValidatesAndBoundsPersistedData()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Settings.MaxKnownPeersCache = 1;

        torrent.PeersInternal.ImportConnectionPreferences(
        [
            new SavedPeerPreference("not-an-address", 6880, UtpSupported: false),
            new SavedPeerPreference("0.0.0.0", 6880, UtpSupported: false),
            new SavedPeerPreference("192.0.2.10", 6881),
            new SavedPeerPreference("192.0.2.11", 6882, UtpSupported: false),
            new SavedPeerPreference("192.0.2.12", 6883, OfferEncryptionNext: false)
        ]);

        var preference = Assert.Single(torrent.PeersInternal.ExportConnectionPreferences());
        Assert.Equal("192.0.2.11", preference.Address);
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

    [Fact(Timeout = 30000)]
    public async Task SaveAllResumeDataAsync_Cancelled_PropagatesCancellation()
    {
        _registry.Add(TorrentTestUtility.CreateMinimal());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Cancellation is not a per-torrent save failure and must not be swallowed as one -
        // the caller (engine shutdown) needs to see that the save did not finish.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _sessionManager.SaveAllResumeDataAsync(cts.Token));
    }

    [Fact(Timeout = 30000)]
    public async Task SaveAllResumeDataAsync_OneTorrentFails_StillSavesTheOthers()
    {
        var failing = CreateDistinctTorrent();
        _registry.Add(failing);
        _persistence.FailSavesFor.Add(failing.Hash);

        var healthy = new List<Torrent>();
        for (int i = 0; i < 5; i++)
        {
            var torrent = CreateDistinctTorrent();
            _registry.Add(torrent);
            healthy.Add(torrent);
        }

        // A single bad entry is logged and skipped; it must not abort the whole sweep.
        await _sessionManager.SaveAllResumeDataAsync(CancellationToken.None);

        var savedHashes = _persistence.SavedEntries.Select(entry => entry.Hash).ToHashSet();
        Assert.DoesNotContain(failing.Hash, savedHashes);
        Assert.All(healthy, torrent => Assert.Contains(torrent.Hash, savedHashes));
    }

    private static Torrent CreateDistinctTorrent()
    {
        var metadata = new TorrentFileMetadata
        {
            Info = { Hash = new InfoHash([.. Guid.NewGuid().ToByteArray(), .. new byte[4]]) }
        };
        return TorrentTestUtility.CreateMinimal(metadata);
    }

    // Mock for SessionPersistence
    private class MockSessionPersistence : ISessionPersistence
    {
        // SessionManager.SaveAllResumeDataAsync saves torrents in parallel, so every mutation
        // here has to be guarded or the double itself races.
        private readonly Lock _sync = new();
        private readonly List<SavedTorrentEntry> _savedEntries = [];

        public IReadOnlyList<SavedTorrentEntry> SavedEntries
        {
            get { lock (_sync) { return [.. _savedEntries]; } }
        }

        public bool DeleteCalled { get; private set; }
        public InfoHash? LastDeletedHash { get; private set; }
        public DhtState? SavedDhtState { get; private set; }

        /// <summary>Hashes whose <see cref="SaveAsync"/> should fail, to exercise error handling.</summary>
        public HashSet<InfoHash> FailSavesFor { get; } = [];

        public Task DeleteAsync(InfoHash hash, CancellationToken cancellationToken = default)
        {
            DeleteCalled = true;
            LastDeletedHash = hash;
            lock (_sync)
            {
                _savedEntries.RemoveAll(e => e.Hash == hash);
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SavedTorrentEntry>> LoadAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SavedEntries);
        }

        public async Task SaveAllAsync(IEnumerable<SavedTorrentEntry> entries, CancellationToken cancellationToken = default)
        {
            foreach (var entry in entries)
            {
                await SaveAsync(entry, cancellationToken).ConfigureAwait(false);
            }
        }

        public Task SaveAsync(SavedTorrentEntry entry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (FailSavesFor.Contains(entry.Hash))
            {
                return Task.FromException(new IOException($"Simulated save failure for {entry.Hash}"));
            }

            lock (_sync)
            {
                var existing = _savedEntries.FirstOrDefault(e => e.Hash == entry.Hash);
                if (existing != null)
                {
                    _savedEntries.Remove(existing);
                }
                _savedEntries.Add(entry);
            }
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




