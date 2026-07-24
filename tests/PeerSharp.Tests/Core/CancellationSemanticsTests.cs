using PeerSharp.Internals;
using PeerSharp.Internals.Dht;
using System.Reflection;

namespace PeerSharp.Tests.Core;

/// <summary>
/// Covers the cancellation contract of the public API: a cancelled or failed operation must
/// leave the engine exactly as it found it, and a token must never be silently ignored.
/// </summary>
public class CancellationSemanticsTests
{
    #region Initialization rollback

    [Fact(Timeout = 30000)]
    public async Task InitializeAsync_CancelledBeforeStarting_CanBeRetried()
    {
        await using var engine = CreateEngine();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.InitializeAsync(cts.Token));

        await engine.InitializeAsync(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 30000)]
    public async Task InitializeAsync_CancelledDuringSessionLoad_CleansUpBeforeRetry()
    {
        var persistence = new CancelOnceSessionPersistence();
        var settings = new Settings { Files = { DefaultDownloadPath = Path.GetTempPath() } };
        settings.Session.Enabled = true;
        settings.Session.AutoSaveIntervalSeconds = 0;
        settings.Dht.Enabled = false;
        settings.Connection.EnableTcpIn = false;
        settings.Connection.EnableTcpOut = false;
        settings.Connection.EnableUtpIn = false;
        settings.Connection.EnableUtpOut = false;

        await using var engine = ClientEngine.Create(new TorrentClientOptions
        {
            Settings = settings,
            SessionPersistence = persistence
        });
        using var cts = new CancellationTokenSource();

        Task initialization = engine.InitializeAsync(cts.Token);
        await persistence.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);

        // The failed attempt must not leave its queue loop behind. A retry would otherwise
        // dispose the live source without cancelling it and launch a second loop beside it.
        Assert.Null(GetPrivateField(engine, "_queueCts"));
        Assert.Null(GetPrivateField(engine, "_queueTask"));
        Assert.Empty(engine.GetTorrents());

        await engine.InitializeAsync(TestContext.Current.CancellationToken);
    }

    #endregion Initialization rollback

    #region Add cleanup

    [Fact(Timeout = 30000)]
    public async Task AddTorrentAsync_FailedAdd_LeavesNoTorrentRegistered()
    {
        // The torrent is registered before the add finishes configuring it. If a later step
        // fails, leaving it behind would both hide it from the caller (who got an exception)
        // and block every later add of the same hash with TorrentAlreadyExistsException.
        await using var engine = CreateEngine();
        var torrentFile = CreateTorrentFile();

        await Assert.ThrowsAsync<ArgumentException>(() => engine.AddTorrentAsync(
            torrentFile,
            new AddTorrentOptions { DownloadPath = "   ", StartImmediately = false },
            TestContext.Current.CancellationToken));

        Assert.Empty(engine.GetTorrents());
        Assert.Null(engine.GetTorrent(torrentFile.InfoHash));
    }

    [Fact(Timeout = 30000)]
    public async Task AddTorrentAsync_AfterFailedAdd_SameTorrentCanBeAddedAgain()
    {
        await using var engine = CreateEngine();
        var torrentFile = CreateTorrentFile();

        await Assert.ThrowsAsync<ArgumentException>(() => engine.AddTorrentAsync(
            torrentFile,
            new AddTorrentOptions { DownloadPath = "   ", StartImmediately = false },
            TestContext.Current.CancellationToken));

        // No orphan left behind, so the retry must not trip the duplicate guard.
        var torrent = await engine.AddTorrentAsync(
            torrentFile,
            new AddTorrentOptions { DownloadPath = Path.GetTempPath(), StartImmediately = false },
            TestContext.Current.CancellationToken);

        Assert.NotNull(torrent);
        Assert.Single(engine.GetTorrents());
    }

    [Fact(Timeout = 30000)]
    public async Task AddMagnetAsync_FailedAdd_LeavesNoTorrentRegistered()
    {
        await using var engine = CreateEngine();
        var magnet = MagnetLink.Parse($"magnet:?xt=urn:btih:{new string('b', 40)}&dn=Cleanup");

        await Assert.ThrowsAsync<ArgumentException>(() => engine.AddMagnetAsync(
            magnet,
            new AddTorrentOptions { DownloadPath = "   ", StartImmediately = false },
            TestContext.Current.CancellationToken));

        Assert.Empty(engine.GetTorrents());
        Assert.Null(engine.GetTorrent(magnet.InfoHash));
    }

    [Fact(Timeout = 30000)]
    public async Task AddTorrentAsync_CancelledToken_ThrowsAndRegistersNothing()
    {
        await using var engine = CreateEngine();
        var torrentFile = CreateTorrentFile();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.AddTorrentAsync(torrentFile, new AddTorrentOptions(Path.GetTempPath()), cts.Token));

        Assert.Empty(engine.GetTorrents());
    }

    #endregion Add cleanup

    #region Remove atomicity

    [Fact(Timeout = 30000)]
    public async Task RemoveTorrentAsync_CancelledToken_LeavesTorrentFullyRegistered()
    {
        // Removal is all-or-nothing: the token is checked up front, and a cancelled remove must
        // not leave a torrent that is stopped, unregistered, or otherwise half-removed.
        await using var engine = CreateEngine();
        var torrentFile = CreateTorrentFile();
        var torrent = await engine.AddTorrentAsync(
            torrentFile,
            new AddTorrentOptions { DownloadPath = Path.GetTempPath(), StartImmediately = false },
            TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.RemoveTorrentAsync(torrent, RemoveOptions.None, cts.Token));

        Assert.Single(engine.GetTorrents());
        Assert.NotNull(engine.GetTorrent(torrentFile.InfoHash));
    }

    [Fact(Timeout = 30000)]
    public async Task RemoveTorrentAsync_Twice_ThrowsNotFoundOnSecondCall()
    {
        await using var engine = CreateEngine();
        var torrentFile = CreateTorrentFile();
        var torrent = await engine.AddTorrentAsync(
            torrentFile,
            new AddTorrentOptions { DownloadPath = Path.GetTempPath(), StartImmediately = false },
            TestContext.Current.CancellationToken);

        await engine.RemoveTorrentAsync(torrent, RemoveOptions.None, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<TorrentNotFoundException>(() =>
            engine.RemoveTorrentAsync(torrent, RemoveOptions.None, TestContext.Current.CancellationToken));
    }

    #endregion Remove atomicity

    #region Start rollback

    [Fact(Timeout = 30000)]
    public async Task StartAsync_PeerTransportCancels_RollsBackToStopped()
    {
        // A transport that honours the token must not leave the torrent claiming to be started
        // with its transports down - and a retry must not hit "already started".
        var torrent = CreateStartableTorrent(out string downloadPath);
        var transport = new FakePeerTransport(throwOnStart: new OperationCanceledException());
        torrent.RegisterPeerTransport(transport);

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                torrent.StartAsync(TestContext.Current.CancellationToken));

            Assert.False(torrent.Started);
            Assert.Equal(TorrentState.Stopped, torrent.State);
            Assert.Equal(1, transport.StopCount);
        }
        finally
        {
            await CleanupAsync(torrent, downloadPath);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task StartAsync_AfterRolledBackStart_CanBeStartedAgain()
    {
        var torrent = CreateStartableTorrent(out string downloadPath);
        var transport = new FakePeerTransport(throwOnStart: new InvalidOperationException("boom"));
        torrent.RegisterPeerTransport(transport);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                torrent.StartAsync(TestContext.Current.CancellationToken));

            // The rollback released the "started" flag, so this must not throw "already started".
            transport.ThrowOnStart = null;
            await torrent.StartAsync(TestContext.Current.CancellationToken);

            Assert.True(torrent.Started);
        }
        finally
        {
            await CleanupAsync(torrent, downloadPath);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task StartAsync_RollbackTeardownFails_DoesNotReportStoppedOrAllowRetry()
    {
        var torrent = CreateStartableTorrent(out string downloadPath);
        var transport = new FakePeerTransport(
            throwOnStart: new InvalidOperationException("start failed"),
            throwOnStop: new InvalidOperationException("stop failed"));
        torrent.RegisterPeerTransport(transport);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                torrent.StartAsync(TestContext.Current.CancellationToken));

            Assert.False(torrent.Started);
            Assert.Equal(TorrentState.Stopping, torrent.State);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                torrent.StartAsync(TestContext.Current.CancellationToken));

            transport.ThrowOnStop = null;
            await torrent.StopAsync(TestContext.Current.CancellationToken);
            Assert.Equal(TorrentState.Stopped, torrent.State);
        }
        finally
        {
            await CleanupAsync(torrent, downloadPath);
        }
    }

    #endregion Start rollback

    #region Tokens that used to be ignored

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_CancelledToken_Throws()
    {
        var torrent = CreateStartableTorrent(out string downloadPath);
        try
        {
            torrent.Trackers.AddTracker("http://tracker.example/announce");
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                torrent.Trackers.AnnounceAsync(cancellationToken: cts.Token));
        }
        finally
        {
            await CleanupAsync(torrent, downloadPath);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task AttachPeerTransportAsync_CancelledToken_ThrowsAndClosesStream()
    {
        var torrent = CreateStartableTorrent(out string downloadPath);
        try
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            var stream = new TrackingStream();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                torrent.AttachPeerTransportAsync(stream, initiator: true, cts.Token));

            // The torrent takes ownership of the stream, so a rejected attach has to close it.
            Assert.True(stream.Closed);
        }
        finally
        {
            await CleanupAsync(torrent, downloadPath);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task SetDownloadPathAsync_CancelledToken_Throws()
    {
        var torrent = CreateStartableTorrent(out string downloadPath);
        try
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                torrent.SetDownloadPathAsync(Path.GetTempPath(), cts.Token));
        }
        finally
        {
            await CleanupAsync(torrent, downloadPath);
        }
    }

    #endregion Tokens that used to be ignored

    #region Alert streaming

    [Fact(Timeout = 30000)]
    public async Task GetAlertsAsync_CancelledWithQueuedAlerts_Throws()
    {
        // Used to depend on timing: cancelling while the queue was non-empty ended the
        // enumeration gracefully, while cancelling while idle threw. Now it always throws.
        var alerts = new AlertsManager(TimeProvider.System);
        alerts.RegisterAlerts(uint.MaxValue);
        alerts.ConfigAlert(AlertId.ConfigChanged, "test");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var alert in alerts.GetAlertsAsync(cancellationToken: cts.Token))
            {
                Assert.NotNull(alert);
            }
        });
    }

    [Fact(Timeout = 30000)]
    public async Task GetAlertsAsync_CancelledWhileIdle_Throws()
    {
        var alerts = new AlertsManager(TimeProvider.System);
        alerts.RegisterAlerts(uint.MaxValue);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var alert in alerts.GetAlertsAsync(cancellationToken: cts.Token))
            {
                Assert.NotNull(alert);
            }
        });
    }

    #endregion Alert streaming

    #region Helpers

    private static ClientEngine CreateEngine()
    {
        var settings = new Settings { Files = { DefaultDownloadPath = Path.GetTempPath() } };
        settings.Session.Enabled = false;
        return ClientEngine.Create(new TorrentClientOptions { Settings = settings });
    }

    private static object? GetPrivateField(object instance, string name)
    {
        return instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance);
    }

    private static TorrentFile CreateTorrentFile()
    {
        return new TorrentFileBuilder()
            .WithName($"cancel-{Guid.NewGuid():N}")
            .WithPieceLength(16384)
            .AddFile("data.bin", new byte[1024])
            .Build();
    }

    private static Torrent CreateStartableTorrent(out string downloadPath)
    {
        downloadPath = Path.Combine(Path.GetTempPath(), "PeerSharpTests_Cancel", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(downloadPath);

        var info = new TorrentFileMetadata();
        info.Info.Name = "cancel-test";
        info.Info.Hash = InfoHash.CreateRandom();
        info.Info.PieceSize = 16384;
        info.Info.FullSize = 1000;
        info.Info.Pieces.Add(new byte[20]);
        info.Info.Files.Add(new Internals.TorrentFileEntry { Path = "file.bin", Size = 1000, Offset = 0 });

        return TorrentTestUtility.CreateMinimal(info, downloadPath);
    }

    private static async Task CleanupAsync(Torrent torrent, string downloadPath)
    {
        await torrent.DisposeAsync();
        try { Directory.Delete(downloadPath, true); } catch (IOException) { /* best effort */ }
    }

    private sealed class FakePeerTransport : IPeerTransport
    {
        public FakePeerTransport(Exception? throwOnStart = null, Exception? throwOnStop = null)
        {
            ThrowOnStart = throwOnStart;
            ThrowOnStop = throwOnStop;
        }

        public Exception? ThrowOnStart { get; set; }
        public Exception? ThrowOnStop { get; set; }
        public int StopCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (ThrowOnStart != null)
            {
                return Task.FromException(ThrowOnStart);
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            if (ThrowOnStop != null)
            {
                return Task.FromException(ThrowOnStop);
            }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TrackingStream : MemoryStream
    {
        public bool Closed { get; private set; }

        public override void Close()
        {
            Closed = true;
            base.Close();
        }
    }

    private sealed class CancelOnceSessionPersistence : ISessionPersistence
    {
        private int _loadCount;

        public TaskCompletionSource LoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DeleteAsync(InfoHash hash, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<IReadOnlyList<SavedTorrentEntry>> LoadAllAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _loadCount) == 1)
            {
                LoadStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return [];
        }

        public Task SaveAsync(SavedTorrentEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAllAsync(IEnumerable<SavedTorrentEntry> entries, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveDhtStateAsync(DhtState state, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<DhtState?> LoadDhtStateAsync(CancellationToken cancellationToken = default) => Task.FromResult<DhtState?>(null);
    }

    #endregion Helpers
}
