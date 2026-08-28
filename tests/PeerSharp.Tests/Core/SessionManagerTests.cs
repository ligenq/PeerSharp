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

    // ── Ordering between the bitfield and the bytes it claims ────────────────
    //
    // Resume data is written durably: temp file, flush to the device, atomic rename. Piece data was
    // not flushed at all, so the durable half was the claim and the volatile half was the data it
    // claimed. After a power cut the engine would restart, trust the bitfield, and serve whatever
    // the disk actually held - piece verification runs when a piece arrives and never again.

    [Fact(Timeout = 30000)]
    public async Task SaveTorrentEntryAsync_FlushesPieceDataBeforeWritingTheBitfield()
    {
        using var fixture = await StorageBackedTorrent.CreateAsync();
        await fixture.Torrent.FilesInternal.WriteAsync(0, new byte[16384], TestContext.Current.CancellationToken);

        Assert.True(fixture.HasUnflushedWrites, "the write should leave the file dirty until a flush");

        await _sessionManager.SaveTorrentEntryAsync(fixture.Torrent, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(fixture.HasUnflushedWrites, "saving resume data must flush the pieces it is about to claim");
        Assert.NotNull(Assert.Single(_persistence.SavedEntries).ResumeData);
    }

    [Fact(Timeout = 30000)]
    public async Task SaveTorrentEntryAsync_WhenPieceDataCannotBeFlushed_WritesNoResumeData()
    {
        // The older resume file on disk claims no more than this one would have, so leaving it alone
        // is always the safe direction. Writing a fresh bitfield we cannot stand behind is not.
        using var fixture = await StorageBackedTorrent.CreateAsync();
        await fixture.Torrent.FilesInternal.WriteAsync(0, new byte[16384], TestContext.Current.CancellationToken);
        await fixture.Torrent.FilesInternal.DisposeAsync();

        await _sessionManager.SaveTorrentEntryAsync(fixture.Torrent, cancellationToken: TestContext.Current.CancellationToken);

        var saved = Assert.Single(_persistence.SavedEntries);
        Assert.Null(saved.ResumeData);

        // The rest of the entry is still written: options and the torrent file itself do not depend
        // on the disk being in any particular state.
        Assert.NotNull(saved.Options);
    }

    [Fact(Timeout = 30000)]
    public async Task SaveTorrentEntryAsync_SavesForOneTorrent_DoNotOverlap()
    {
        // The persistence layer serialises its file writes, but that alone does not order the
        // snapshots: two saves that captured in one order can reach the writes in the other, leaving
        // the older bitfield on disk. Holding a gate across capture, flush and write is what makes
        // the sequence indivisible.
        var torrent = TorrentTestUtility.CreateMinimal();
        _registry.Add(torrent);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _persistence.SaveGate = gate;

        var first = _sessionManager.SaveTorrentEntryAsync(torrent, cancellationToken: TestContext.Current.CancellationToken);
        var second = _sessionManager.SaveTorrentEntryAsync(torrent, cancellationToken: TestContext.Current.CancellationToken);

        // The first save is parked inside the persistence layer. The second must be waiting on the
        // gate outside it, not queued up behind it having already taken its snapshot.
        await TorrentTestUtility.WaitUntilAsync(
            () => _persistence.PeakConcurrentSaves >= 1,
            because: "the first save to reach the persistence layer");

        Assert.Equal(1, _persistence.PeakConcurrentSaves);

        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, _persistence.PeakConcurrentSaves);
    }

    [Fact(Timeout = 30000)]
    public async Task SaveAllResumeDataAsync_StillSavesDifferentTorrentsInParallel()
    {
        // Serialising per torrent must not serialise the whole sweep: a session with many torrents
        // would then save them one at a time.
        for (int i = 0; i < 4; i++)
        {
            var metadata = new TorrentFileMetadata();
            metadata.Info.Name = $"torrent-{i}";
            metadata.Info.Hash = InfoHash.CreateRandom();
            _registry.Add(TorrentTestUtility.CreateMinimal(metadata));
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _persistence.SaveGate = gate;

        var sweep = _sessionManager.SaveAllResumeDataAsync(TestContext.Current.CancellationToken);

        await TorrentTestUtility.WaitUntilAsync(
            () => _persistence.PeakConcurrentSaves > 1,
            because: "several torrents to be saved at once");

        gate.SetResult();
        await sweep;
    }

    /// <summary>A torrent with real metadata and real files behind it, on a temp path.</summary>
    private sealed class StorageBackedTorrent : IDisposable
    {
        public required Torrent Torrent { get; init; }
        public required string Path { get; init; }

        /// <summary>Whether the storage still holds writes that have not reached the device.</summary>
        public bool HasUnflushedWrites
        {
            get
            {
                var files = Torrent.FilesInternal;
                var storage = files.GetType()
                    .GetField("_storage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .GetValue(files)!;
                var flags = (bool[])storage.GetType()
                    .GetField("_fileDirty", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .GetValue(storage)!;
                return flags.Any(dirty => dirty);
            }
        }

        public static async Task<StorageBackedTorrent> CreateAsync()
        {
            var metadata = new TorrentFileMetadata();
            metadata.Info.Name = "flush-ordering";
            metadata.Info.PieceSize = 16384;
            metadata.Info.FullSize = 16384 * 4;
            metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "payload.bin", Size = metadata.Info.FullSize, Offset = 0 });
            metadata.Info.Pieces = [.. Enumerable.Range(0, 4).Select(_ => new byte[20])];

            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "PeerSharpFlushOrdering",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);

            var torrent = TorrentTestUtility.CreateMinimal(metadata, path);
            await torrent.FilesInternal.InitializeAsync([]);

            return new StorageBackedTorrent { Torrent = torrent, Path = path };
        }

        public void Dispose()
        {
            Torrent.DisposeAsync().AsTask().GetAwaiter().GetResult();
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
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

        private int _concurrentSaves;

        /// <summary>Highest number of saves seen inside <c>SaveAsync</c> at one time.</summary>
        public int PeakConcurrentSaves { get; private set; }

        /// <summary>Held open by <c>SaveAsync</c> while set, so a save can be parked mid-flight.</summary>
        public TaskCompletionSource? SaveGate { get; set; }

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

        public async Task SaveAsync(SavedTorrentEntry entry, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                _concurrentSaves++;
                PeakConcurrentSaves = Math.Max(PeakConcurrentSaves, _concurrentSaves);
            }

            try
            {
                if (SaveGate is { } gate)
                {
                    await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                await SaveCoreAsync(entry, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_sync)
                {
                    _concurrentSaves--;
                }
            }
        }

        private Task SaveCoreAsync(SavedTorrentEntry entry, CancellationToken cancellationToken)
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




