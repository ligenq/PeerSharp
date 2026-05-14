using System.Net;
using System.Reflection;
using PeerSharp.Internals;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Utp;

namespace PeerSharp.Tests.Core;

public class ClientEnginePersistenceTests
{
    private sealed class MockSessionPersistence : ISessionPersistence
    {
        public List<SavedTorrentEntry> SavedEntries { get; } = [];
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
            => Task.FromResult<IReadOnlyList<SavedTorrentEntry>>(SavedEntries.AsReadOnly());

        public Task SaveAsync(SavedTorrentEntry entry, CancellationToken cancellationToken = default)
        {
            SavedEntries.RemoveAll(e => e.Hash == entry.Hash);
            SavedEntries.Add(entry);
            return Task.CompletedTask;
        }

        public Task SaveAllAsync(IEnumerable<SavedTorrentEntry> entries, CancellationToken cancellationToken = default)
        {
            foreach (var e in entries)
            {
                SaveAsync(e, cancellationToken);
            }

            return Task.CompletedTask;
        }

        public Task SaveDhtStateAsync(DhtState state, CancellationToken cancellationToken = default)
        {
            SavedDhtState = state;
            return Task.CompletedTask;
        }

        public Task<DhtState?> LoadDhtStateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(SavedDhtState);
    }

    private sealed class MockDhtManager : IDhtManager
    {
        public DhtState? StateToReturn { get; set; }
        public InfoHash NodeId { get; } = new InfoHash(new byte[20]);
        public void Announce(InfoHash infoHash, int port) { }
        public void FindPeers(InfoHash infoHash) { }
        public void Ping(IPEndPoint ep) { }
        public void ScrapeInfoHash(InfoHash infoHash) { }
        public void SetCallback(IDhtCallback callback) { }
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public DhtState? ConsumeStateSnapshot() => StateToReturn;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MockNetworkManager : INetworkManager
    {
        public IDhtManager Dht { get; set; } = null!;
        public IUtpManager Utp { get; set; } = null!;
        public IPortListener PortListener { get; set; } = null!;
        public ILsdManager Lsd { get; set; } = null!;
        public IpBlocklist Blocklist { get; set; } = new();
        public int BoundTcpPort { get; set; }
        public int BoundUdpPort { get; set; }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<PortMappingStatus> GetPortMappingStatus() => [];
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static TorrentFile CreateAndParseTorrentFile()
    {
        var built = new TorrentFileBuilder()
            .WithName("PersistenceTest")
            .WithPieceLength(16384)
            .AddFile("data.dat", new byte[1024])
            .Build();
        return TorrentFile.Parse(built.RawData.ToArray());
    }

    private static Settings CreateSettings()
    {
        var s = new Settings();
        s.Files.DefaultDownloadPath = Path.GetTempPath();
        return s;
    }

    private static async Task InvokePrivateAsync(object obj, string name, params object[] args)
    {
        var m = obj.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException(obj.GetType().Name, name);
        await ((Task)m.Invoke(obj, args)!).ConfigureAwait(false);
    }

    // ── SaveSessionAsync ──────────────────────────────────────────────────────

    [Fact(Timeout = 10000)]
    public async Task SaveSessionAsync_WithSession_SavesAllTorrents()
    {
        var persistence = new MockSessionPersistence();
        var settings = CreateSettings();
        settings.Session.Enabled = true;
        await using var engine = ClientEngine.Create(new TorrentClientOptions { Settings = settings, SessionPersistence = persistence });

        var torrent = await engine.AddTorrentAsync(
            CreateAndParseTorrentFile(),
            new AddTorrentOptions { StartImmediately = false, DownloadPath = Path.GetTempPath() });

        int countBefore = persistence.SavedEntries.Count;

        await engine.SaveSessionAsync();

        // SaveAllResumeDataAsync re-saves all registry entries (upsert); count stays the same or grows
        Assert.True(persistence.SavedEntries.Count >= countBefore);
        Assert.Contains(persistence.SavedEntries, e => e.Hash == torrent.Hash);
    }

    [Fact(Timeout = 10000)]
    public async Task SaveSessionAsync_WithoutSession_IsNoOp()
    {
        var settings = CreateSettings();
        await using var engine = ClientEngine.Create(new TorrentClientOptions { Settings = settings });

        // Must not throw
        await engine.SaveSessionAsync();
    }

    // ── PersistMagnetMetadataAsync ────────────────────────────────────────────

    [Fact(Timeout = 10000)]
    public async Task PersistMagnetMetadataAsync_WithValidMetadata_SavesEntry()
    {
        var persistence = new MockSessionPersistence();
        var settings = CreateSettings();
        settings.Session.Enabled = true;
        await using var engine = ClientEngine.Create(new TorrentClientOptions { Settings = settings, SessionPersistence = persistence });

        // Parsed TorrentFile has InfoBytes set → BuildTorrentBytes returns non-null
        var parsedTorrent = (Torrent)await engine.AddTorrentAsync(
            CreateAndParseTorrentFile(),
            new AddTorrentOptions { StartImmediately = false, DownloadPath = Path.GetTempPath() });

        persistence.SavedEntries.Clear();

        await InvokePrivateAsync(engine, "PersistMagnetMetadataAsync", parsedTorrent);

        Assert.Contains(persistence.SavedEntries, e => e.Hash == parsedTorrent.Hash);
    }

    [Fact(Timeout = 10000)]
    public async Task PersistMagnetMetadataAsync_WithEmptyMetadata_DoesNotSave()
    {
        var persistence = new MockSessionPersistence();
        var settings = CreateSettings();
        settings.Session.Enabled = true;
        await using var engine = ClientEngine.Create(new TorrentClientOptions { Settings = settings, SessionPersistence = persistence });

        // Magnet torrent has no InfoBytes → BuildTorrentBytes returns null → early return
        var magnet = MagnetLink.Parse($"magnet:?xt=urn:btih:{new string('b', 40)}&dn=NoMetadata");
        var magnetTorrent = (Torrent)await engine.AddMagnetAsync(
            magnet,
            new AddTorrentOptions { StartImmediately = false, DownloadPath = Path.GetTempPath() });

        persistence.SavedEntries.Clear();

        await InvokePrivateAsync(engine, "PersistMagnetMetadataAsync", magnetTorrent);

        Assert.DoesNotContain(persistence.SavedEntries, e => e.Hash == magnetTorrent.Hash);
    }

    [Fact(Timeout = 10000)]
    public async Task PersistMagnetMetadataAsync_SessionDisabled_DoesNotSaveEntry()
    {
        var persistence = new MockSessionPersistence();
        var settings = CreateSettings();
        settings.Session.Enabled = false;
        await using var engine = ClientEngine.Create(new TorrentClientOptions { Settings = settings, SessionPersistence = persistence });

        var parsedTorrent = (Torrent)await engine.AddTorrentAsync(
            CreateAndParseTorrentFile(),
            new AddTorrentOptions { StartImmediately = false, DownloadPath = Path.GetTempPath() });

        persistence.SavedEntries.Clear();

        await InvokePrivateAsync(engine, "PersistMagnetMetadataAsync", parsedTorrent);

        // Session disabled → the "if (Settings.Session.Enabled && _sessionManager != null)" branch is skipped
        Assert.Empty(persistence.SavedEntries);
    }

    // ── LoadPersistedTorrentsAsync ────────────────────────────────────────────

    [Fact(Timeout = 10000)]
    public async Task LoadPersistedTorrentsAsync_WithTorrentFile_LoadsTorrent()
    {
        var torrentFile = CreateAndParseTorrentFile();
        var torrentBytes = torrentFile.RawData.ToArray();

        var persistence = new MockSessionPersistence();
        persistence.SavedEntries.Add(new SavedTorrentEntry(
            torrentFile.InfoHash,
            torrentBytes,
            null,
            null,
            new SavedTorrentOptions(DownloadPath: Path.GetTempPath(), WasStarted: false)));

        var settings = CreateSettings();
        await using var engine = ClientEngine.Create(new TorrentClientOptions { Settings = settings, SessionPersistence = persistence });

        await InvokePrivateAsync(engine, "LoadPersistedTorrentsAsync", CancellationToken.None);

        Assert.Contains(engine.GetTorrents(), t => t.Hash == torrentFile.InfoHash);
    }

    [Fact(Timeout = 10000)]
    public async Task LoadPersistedTorrentsAsync_WithMagnetLink_LoadsTorrent()
    {
        var infoHash = new InfoHash(Enumerable.Range(1, 20).Select(i => (byte)i).ToArray());
        var magnetUri = $"magnet:?xt=urn:btih:{infoHash.ToHexString()}&dn=PersistedMagnet";

        var persistence = new MockSessionPersistence();
        persistence.SavedEntries.Add(new SavedTorrentEntry(
            infoHash,
            null,
            magnetUri,
            null,
            new SavedTorrentOptions(DownloadPath: Path.GetTempPath(), WasStarted: false)));

        var settings = CreateSettings();
        await using var engine = ClientEngine.Create(new TorrentClientOptions { Settings = settings, SessionPersistence = persistence });

        await InvokePrivateAsync(engine, "LoadPersistedTorrentsAsync", CancellationToken.None);

        Assert.Contains(engine.GetTorrents(), t => t.Hash == infoHash);
    }

    [Fact(Timeout = 10000)]
    public async Task LoadPersistedTorrentsAsync_WithNoData_SkipsEntry()
    {
        var infoHash = new InfoHash(new byte[20]);
        var persistence = new MockSessionPersistence();
        persistence.SavedEntries.Add(new SavedTorrentEntry(infoHash, null, null, null, null));

        var settings = CreateSettings();
        await using var engine = ClientEngine.Create(new TorrentClientOptions { Settings = settings, SessionPersistence = persistence });

        await InvokePrivateAsync(engine, "LoadPersistedTorrentsAsync", CancellationToken.None);

        Assert.Empty(engine.GetTorrents());
    }

    [Fact(Timeout = 10000)]
    public async Task LoadPersistedTorrentsAsync_WithDuplicateHash_SkipsSecond()
    {
        var torrentFile = CreateAndParseTorrentFile();
        var torrentBytes = torrentFile.RawData.ToArray();
        var entry = new SavedTorrentEntry(
            torrentFile.InfoHash,
            torrentBytes,
            null,
            null,
            new SavedTorrentOptions(DownloadPath: Path.GetTempPath(), WasStarted: false));

        var persistence = new MockSessionPersistence();
        persistence.SavedEntries.Add(entry);
        persistence.SavedEntries.Add(entry);

        var settings = CreateSettings();
        await using var engine = ClientEngine.Create(new TorrentClientOptions { Settings = settings, SessionPersistence = persistence });

        await InvokePrivateAsync(engine, "LoadPersistedTorrentsAsync", CancellationToken.None);

        Assert.Single(engine.GetTorrents(), t => t.Hash == torrentFile.InfoHash);
    }

    // ── RebalanceQueueAsync ───────────────────────────────────────────────────

    [Fact(Timeout = 10000)]
    public async Task RebalanceQueueAsync_NullQueueManager_ReturnsEarlyWithoutError()
    {
        var settings = CreateSettings();
        // No InitializeAsync → _queueManager stays null
        await using var engine = ClientEngine.Create(new TorrentClientOptions { Settings = settings });

        await InvokePrivateAsync(engine, "RebalanceQueueAsync", CancellationToken.None);
    }

    [Fact(Timeout = 10000)]
    public async Task RebalanceQueueAsync_QueueEnabled_ProcessesPlanWithoutError()
    {
        var settings = CreateSettings();
        settings.Queue.Enabled = true;
        settings.Queue.MaxActiveDownloads = 2;

        var mockNetwork = new MockNetworkManager();
        await using var engine = ClientEngine.Create(settings, networkManager: mockNetwork);
        await engine.InitializeAsync();

        await engine.AddMagnetAsync(
            MagnetLink.Parse($"magnet:?xt=urn:btih:{new string('c', 40)}&dn=Q1"),
            new AddTorrentOptions { StartImmediately = false, DownloadPath = Path.GetTempPath() });

        // Direct invocation (also triggered inside AddMagnetAsync above)
        await InvokePrivateAsync(engine, "RebalanceQueueAsync", CancellationToken.None);
    }

    // ── SaveDhtStateIfNeededAsync ─────────────────────────────────────────────

    [Fact(Timeout = 10000)]
    public async Task SaveDhtStateIfNeededAsync_NoSessionManager_DoesNotSave()
    {
        var settings = CreateSettings();
        // No SessionPersistence → _sessionManager is null
        await using var engine = ClientEngine.Create(new TorrentClientOptions { Settings = settings });

        await InvokePrivateAsync(engine, "SaveDhtStateIfNeededAsync", CancellationToken.None);
    }

    [Fact(Timeout = 10000)]
    public async Task SaveDhtStateIfNeededAsync_SessionDisabled_DoesNotSave()
    {
        var persistence = new MockSessionPersistence();
        var settings = CreateSettings();
        settings.Session.Enabled = false;
        settings.Dht.Enabled = true;
        await using var engine = ClientEngine.Create(new TorrentClientOptions { Settings = settings, SessionPersistence = persistence });

        await InvokePrivateAsync(engine, "SaveDhtStateIfNeededAsync", CancellationToken.None);

        Assert.Null(persistence.SavedDhtState);
    }

    [Fact(Timeout = 10000)]
    public async Task SaveDhtStateIfNeededAsync_DhtDisabled_DoesNotSave()
    {
        var persistence = new MockSessionPersistence();
        var settings = CreateSettings();
        settings.Session.Enabled = true;
        settings.Dht.Enabled = false;
        await using var engine = ClientEngine.Create(new TorrentClientOptions { Settings = settings, SessionPersistence = persistence });

        await InvokePrivateAsync(engine, "SaveDhtStateIfNeededAsync", CancellationToken.None);

        Assert.Null(persistence.SavedDhtState);
    }

    [Fact(Timeout = 10000)]
    public async Task SaveDhtStateIfNeededAsync_DhtReturnsState_SavesState()
    {
        var persistence = new MockSessionPersistence();
        var settings = CreateSettings();
        settings.Session.Enabled = true;
        settings.Dht.Enabled = true;
        await using var engine = ClientEngine.Create(new TorrentClientOptions { Settings = settings, SessionPersistence = persistence });

        var dhtState = new DhtState(new byte[20], new List<DhtNode>());
        var dht = new MockDhtManager { StateToReturn = dhtState };
        var networkManager = new MockNetworkManager { Dht = dht };

        typeof(ClientEngine)
            .GetField("_networkManager", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(engine, networkManager);

        await InvokePrivateAsync(engine, "SaveDhtStateIfNeededAsync", CancellationToken.None);

        Assert.NotNull(persistence.SavedDhtState);
        Assert.Equal(dhtState.NodeId, persistence.SavedDhtState!.NodeId);
    }

    [Fact(Timeout = 10000)]
    public async Task SaveDhtStateIfNeededAsync_DhtReturnsNull_DoesNotSave()
    {
        var persistence = new MockSessionPersistence();
        var settings = CreateSettings();
        settings.Session.Enabled = true;
        settings.Dht.Enabled = true;
        await using var engine = ClientEngine.Create(new TorrentClientOptions { Settings = settings, SessionPersistence = persistence });

        var dht = new MockDhtManager { StateToReturn = null };
        var networkManager = new MockNetworkManager { Dht = dht };

        typeof(ClientEngine)
            .GetField("_networkManager", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(engine, networkManager);

        await InvokePrivateAsync(engine, "SaveDhtStateIfNeededAsync", CancellationToken.None);

        Assert.Null(persistence.SavedDhtState);
    }

    [Fact(Timeout = 10000)]
    public async Task SaveDhtStateIfNeededAsync_NullDhtOnManager_DoesNotSave()
    {
        var persistence = new MockSessionPersistence();
        var settings = CreateSettings();
        settings.Session.Enabled = true;
        settings.Dht.Enabled = true;
        await using var engine = ClientEngine.Create(new TorrentClientOptions { Settings = settings, SessionPersistence = persistence });

        // Network manager with Dht = null (no DHT available)
        var networkManager = new MockNetworkManager { Dht = null! };

        typeof(ClientEngine)
            .GetField("_networkManager", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(engine, networkManager);

        await InvokePrivateAsync(engine, "SaveDhtStateIfNeededAsync", CancellationToken.None);

        Assert.Null(persistence.SavedDhtState);
    }
}
