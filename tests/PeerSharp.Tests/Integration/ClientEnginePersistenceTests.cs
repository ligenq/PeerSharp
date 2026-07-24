using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;

namespace PeerSharp.Tests.Integration;

[Collection("Integration")]
public class ClientEnginePersistenceTests
{
    private readonly ITestOutputHelper _output;

    public ClientEnginePersistenceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Session_PersistsAndRestoresTorrents()
    {
        string sessionPath = Path.Combine(Path.GetTempPath(), "PeerSharpTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(sessionPath);

        try
        {
            var settings = new Settings();
            settings.Session.Enabled = true;
            settings.Session.SessionPath = sessionPath;
            settings.Session.AutoSaveIntervalSeconds = 3600;
            settings.Files.DefaultDownloadPath = sessionPath;

            // Build a proper torrent file with raw bytes so it can be persisted
            var torrentFile = new TorrentFileBuilder()
                .WithName("PersistedTorrent")
                .WithPieceLength(16384)
                .AddFile("test.dat", new byte[16384])
                .Build();

            _output.WriteLine($"Built torrent hash: {torrentFile.InfoHash.ToHexString()}");
            _output.WriteLine($"RawData length: {torrentFile.RawData.Length}");

            // 1. Start Engine 1
            var engine1 = ClientEngine.Create(new TorrentClientOptions { Settings = settings });
            await engine1.InitializeAsync();

            var torrent = await engine1.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });
            _output.WriteLine($"Added torrent: {torrent.Name}, hash: {torrent.Hash.ToHexString()}");
            _output.WriteLine($"Torrents in engine1: {engine1.GetTorrents().Count}");

            // 2. Stop Engine 1 (Trigger Save)
            await engine1.StopAsync();
            await engine1.DisposeAsync();

            // Debug: Check what was saved
            var torrentsDir = Path.Combine(sessionPath, "torrents");
            if (Directory.Exists(torrentsDir))
            {
                foreach (var dir in Directory.GetDirectories(torrentsDir))
                {
                    _output.WriteLine($"Saved dir: {dir}");
                    foreach (var f in Directory.GetFiles(dir))
                    {
                        var fi = new FileInfo(f);
                        _output.WriteLine($"  File: {fi.Name} ({fi.Length} bytes)");
                    }
                }
            }
            else
            {
                _output.WriteLine("No torrents directory exists!");
            }

            // 3. Start Engine 2
            var engine2 = ClientEngine.Create(new TorrentClientOptions { Settings = settings });
            await engine2.InitializeAsync(); // Should load

            _output.WriteLine($"Torrents in engine2: {engine2.GetTorrents().Count}");

            // 4. Verify
            Assert.Single(engine2.GetTorrents());
            var loaded = engine2.GetTorrents()[0];
            Assert.Equal("PersistedTorrent", loaded.Name);
            Assert.Equal(torrentFile.InfoHash, loaded.Hash);

            await engine2.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(sessionPath))
            {
                Directory.Delete(sessionPath, true);
            }
        }
    }

    [Fact]
    public async Task Restore_LoadsAllTorrents_WithoutRedundantWriteBack()
    {
        string sessionPath = Path.Combine(Path.GetTempPath(), "PeerSharpTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(sessionPath);

        try
        {
            const int torrentCount = 6;

            Settings BuildSettings()
            {
                var s = new Settings();
                s.Session.Enabled = true;
                s.Session.SessionPath = sessionPath;
                s.Session.AutoSaveIntervalSeconds = 3600; // Keep auto-save from firing during the test.
                s.Files.DefaultDownloadPath = sessionPath;

                // Keep the test hermetic: no sockets are bound, so restore logic is exercised
                // without depending on network port availability.
                s.Dht.Enabled = false;
                s.Connection.EnableUtpIn = false;
                s.Connection.EnableUtpOut = false;
                s.Connection.EnableLsd = false;
                s.Connection.EnableTcpIn = false;
                return s;
            }

            // 1. Persist several torrents so restore exercises the concurrent load path.
            var engine1 = ClientEngine.Create(new TorrentClientOptions { Settings = BuildSettings() });
            await engine1.InitializeAsync();

            var expectedHashes = new HashSet<InfoHash>();
            for (int i = 0; i < torrentCount; i++)
            {
                var torrentFile = new TorrentFileBuilder()
                    .WithName($"Persisted-{i}")
                    .WithPieceLength(16384)
                    .AddFile($"test-{i}.dat", new byte[16384])
                    .Build();
                expectedHashes.Add(torrentFile.InfoHash);
                await engine1.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });
            }

            await engine1.StopAsync();
            await engine1.DisposeAsync();

            // 2. Restore through a spy that counts persistence traffic during InitializeAsync.
            var spy = new CountingPersistence(new SessionPersistence(sessionPath, NullLogger<SessionPersistence>.Instance));

            var engine2 = ClientEngine.Create(new TorrentClientOptions
            {
                Settings = BuildSettings(),
                SessionPersistence = spy
            });
            await engine2.InitializeAsync(); // Performs the full restore.

            // 3. All torrents restored (verifies the parallel load produced no lost/duplicated entries).
            var restored = engine2.GetTorrents();
            Assert.Equal(torrentCount, restored.Count);
            Assert.Equal(expectedHashes, restored.Select(t => t.Hash).ToHashSet());

            // 4. Restore must not re-write entries it just read from disk.
            Assert.Equal(1, spy.LoadAllCount);
            Assert.Equal(0, spy.SaveCount);

            await engine2.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(sessionPath))
            {
                Directory.Delete(sessionPath, true);
            }
        }
    }

    /// <summary>
    /// Wraps a real <see cref="ISessionPersistence"/> and counts calls so tests can assert
    /// that session restore does not trigger redundant write-backs.
    /// </summary>
    private sealed class CountingPersistence(ISessionPersistence inner) : ISessionPersistence
    {
        private int _saveCount;
        private int _loadAllCount;

        public int SaveCount => Volatile.Read(ref _saveCount);
        public int LoadAllCount => Volatile.Read(ref _loadAllCount);

        public Task DeleteAsync(InfoHash hash, CancellationToken cancellationToken = default)
            => inner.DeleteAsync(hash, cancellationToken);

        public Task<IReadOnlyList<SavedTorrentEntry>> LoadAllAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _loadAllCount);
            return inner.LoadAllAsync(cancellationToken);
        }

        public Task SaveAllAsync(IEnumerable<SavedTorrentEntry> entries, CancellationToken cancellationToken = default)
            => inner.SaveAllAsync(entries, cancellationToken);

        public Task SaveAsync(SavedTorrentEntry entry, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _saveCount);
            return inner.SaveAsync(entry, cancellationToken);
        }

        public Task SaveDhtStateAsync(DhtState state, CancellationToken cancellationToken = default)
            => inner.SaveDhtStateAsync(state, cancellationToken);

        public Task<DhtState?> LoadDhtStateAsync(CancellationToken cancellationToken = default)
            => inner.LoadDhtStateAsync(cancellationToken);
    }
}
