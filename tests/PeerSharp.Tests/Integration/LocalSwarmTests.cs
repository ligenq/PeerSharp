using System.Net;
using PeerSharp.Internals;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Integration;

[Collection("Integration")]
public class LocalSwarmTests : IDisposable
{
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(5);

    private readonly string _testRoot;
    private readonly string _pathA;
    private readonly string _pathB;
    private readonly ILoggerFactory _loggerFactory;

    public LocalSwarmTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "MtTorrentTests_" + Guid.NewGuid().ToString("N"));
        _pathA = Path.Combine(_testRoot, "PeerA");
        _pathB = Path.Combine(_testRoot, "PeerB");
        Directory.CreateDirectory(_pathA);
        Directory.CreateDirectory(_pathB);

        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Warning);
        });
    }

    [Fact(Timeout = 30000)]
    public async Task DownloadFile_BetweenTwoLocalPeers_Succeeds()
    {
        const string fileName = "dummy.bin";
        byte[] dummyData = new byte[64 * 1024];
        Random.Shared.NextBytes(dummyData);

        await WriteFilesAsync(_pathA, (fileName, dummyData));

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(16_384)
            .AddFile(fileName, dummyData)
            .Build();

        await using var seedEngine = await CreateEngineAsync(_pathA);
        var seedTorrent = await seedEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });

        int validPieces = await seedTorrent.ForceRecheckAsync();
        Assert.Equal(torrentFile.PieceCount, validPieces);
        Assert.True(seedTorrent.Finished);

        await seedTorrent.StartAsync();

        await using var leecherEngine = await CreateEngineAsync(_pathB);
        var leecherTorrent = await leecherEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = true });

        await EnsureConnectedAsync(leecherEngine, leecherTorrent, seedEngine, ConnectionTimeout, cancellationToken: TestContext.Current.CancellationToken);

        await WaitForConditionAsync(leecherTorrent, t => t.Finished, DownloadTimeout, "download completion", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(dummyData.Length, (long)leecherTorrent.FinishedBytes);

        var downloadedInfo = leecherTorrent.GetFileInfo(0);
        byte[] downloadedData = await ReadAllBytesSharedAsync(Path.Combine(_pathB, downloadedInfo.Path));
        Assert.Equal(dummyData, downloadedData);

        // Stated from the seeder's side as well, not just inferred from the leecher finishing. A real
        // run raised the alarm that no peer ever received a byte from us, and the serving path had no
        // assertion of its own to answer it with - the data arriving was only ever implied.
        var servedPeers = seedTorrent.Peers.GetConnectedPeers();
        Assert.True(
            servedPeers.Sum(static peer => peer.Uploaded) >= dummyData.Length,
            "The seeder finished the transfer without recording the bytes it served. Either the upload " +
            "accounting is wrong or the leecher was fed by something other than this peer. Peers: " +
            string.Join(
                ", ",
                servedPeers.Select(static peer => $"{peer.EndPoint} up={peer.Uploaded} down={peer.Downloaded}")));

        // The leecher reported what it held, so a consumer can tell it apart from one that said nothing.
        Assert.All(
            leecherTorrent.Peers.GetConnectedPeers(),
            peer => Assert.True(
                peer.HasReportedPieces,
                $"Peer {peer.EndPoint} completed a transfer without ever reporting its pieces, which " +
                "leaves consumers unable to distinguish it from a peer that holds nothing."));
    }

    [Fact(Timeout = 30000)]
    public async Task DownloadFile_FromMagnetMetadataExchange_Succeeds()
    {
        const string fileName = "metadata.bin";
        byte[] dummyData = new byte[96 * 1024];
        Random.Shared.NextBytes(dummyData);

        await WriteFilesAsync(_pathA, (fileName, dummyData));

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(16_384)
            .AddFile(fileName, dummyData)
            .Build();

        await using var seedEngine = await CreateEngineAsync(_pathA);
        var seedTorrent = await seedEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });

        int validPieces = await seedTorrent.ForceRecheckAsync();
        Assert.Equal(torrentFile.PieceCount, validPieces);
        await seedTorrent.StartAsync();

        string magnet = $"magnet:?xt=urn:btih:{torrentFile.InfoHash.ToHexString()}&dn={Uri.EscapeDataString(torrentFile.Name)}";

        await using var leecherEngine = await CreateEngineAsync(_pathB);
        var leecherTorrent = await leecherEngine.AddMagnetAsync(magnet, new AddTorrentOptions { StartImmediately = true });

        await EnsureConnectedAsync(leecherEngine, leecherTorrent, seedEngine, ConnectionTimeout, cancellationToken: TestContext.Current.CancellationToken);

        await WaitForConditionAsync(leecherTorrent, t => t.HasMetadata, MetadataTimeout, "metadata download", cancellationToken: TestContext.Current.CancellationToken);
        await WaitForConditionAsync(leecherTorrent, t => t.Finished, DownloadTimeout, "download completion", cancellationToken: TestContext.Current.CancellationToken);

        var downloadedInfo = leecherTorrent.GetFileInfo(0);
        byte[] downloadedData = await ReadAllBytesSharedAsync(Path.Combine(_pathB, downloadedInfo.Path));
        Assert.Equal(dummyData, downloadedData);
    }

    [Fact(Timeout = 30000)]
    public async Task PreviewMagnet_StopAfterMetadata_AllowsDeselectionBeforeDownload()
    {
        var fileA = (Path: "keep.bin", Data: new byte[16_384]);
        var fileB = (Path: "skip.bin", Data: new byte[16_384]);
        Random.Shared.NextBytes(fileA.Data);
        Random.Shared.NextBytes(fileB.Data);

        await WriteFilesAsync(_pathA, fileA, fileB);

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName("PreviewTest")
            .WithPieceLength(16_384)
            .AddFile(fileA.Path, fileA.Data)
            .AddFile(fileB.Path, fileB.Data)
            .Build();

        await using var seedEngine = await CreateEngineAsync(_pathA);
        var seedTorrent = await seedEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });
        await seedTorrent.ForceRecheckAsync();
        await seedTorrent.StartAsync();

        string magnet = $"magnet:?xt=urn:btih:{torrentFile.InfoHash.ToHexString()}";

        await using var leecherEngine = await CreateEngineAsync(_pathB);
        var leecherTorrent = await leecherEngine.AddMagnetAsync(magnet, new AddTorrentOptions { StopAfterMetadata = true });

        // Bootstrap the connection and wait for the preview window. On loopback the metadata
        // exchange can finish (and StopAfterMetadata re-stop the torrent) faster than a
        // connected peer can be observed, so drive the loop by metadata completion.
        using var metadataCts = new CancellationTokenSource(MetadataTimeout);
        var metadataTask = leecherTorrent.WaitForMetadataAsync(metadataCts.Token);
        var seedListener = seedEngine.PortListener ?? throw new InvalidOperationException("Seed engine has no port listener.");
        var bootstrapEndpoint = new IPEndPoint(IPAddress.Loopback, seedListener.Port);
        while (!metadataTask.IsCompleted && !metadataCts.IsCancellationRequested)
        {
            leecherEngine.OnPeersFound(leecherTorrent.Hash, [bootstrapEndpoint]);
            await Task.Delay(100);
        }

        await metadataTask;

        Assert.True(leecherTorrent.HasMetadata);
        Assert.False(leecherTorrent.Started);
        Assert.Equal(0, leecherTorrent.PiecesReceived);
        Assert.Equal(2, leecherTorrent.FileCount);

        // The user deselects one file, then starts the real download
        await leecherTorrent.SetFilePriorityAsync(1, Priority.DoNotDownload);
        await leecherTorrent.StartAsync();

        await EnsureConnectedAsync(leecherEngine, leecherTorrent, seedEngine, ConnectionTimeout, cancellationToken: TestContext.Current.CancellationToken);
        await WaitForConditionAsync(leecherTorrent, t => t.SelectionFinished, DownloadTimeout, "selected files completion", cancellationToken: TestContext.Current.CancellationToken);

        var keptInfo = leecherTorrent.GetFileInfo(0);
        var skippedInfo = leecherTorrent.GetFileInfo(1);
        Assert.Equal(fileA.Data.Length, keptInfo.DownloadedBytes);
        Assert.Equal(0, skippedInfo.DownloadedBytes);

        byte[] downloadedA = await ReadAllBytesSharedAsync(Path.Combine(_pathB, keptInfo.Path));
        Assert.Equal(fileA.Data, downloadedA);
    }

    [Fact(Timeout = 30000)]
    public async Task GetMagnetMetadata_ReturnsTorrentFileAndRemovesTransientTorrent()
    {
        const string fileName = "fetched.bin";
        byte[] dummyData = new byte[32 * 1024];
        Random.Shared.NextBytes(dummyData);

        await WriteFilesAsync(_pathA, (fileName, dummyData));

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(16_384)
            .AddFile(fileName, dummyData)
            .Build();

        await using var seedEngine = await CreateEngineAsync(_pathA);
        var seedTorrent = await seedEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });
        await seedTorrent.ForceRecheckAsync();
        await seedTorrent.StartAsync();

        string magnet = $"magnet:?xt=urn:btih:{torrentFile.InfoHash.ToHexString()}";

        await using var leecherEngine = await CreateEngineAsync(_pathB);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(MetadataTimeout + ConnectionTimeout);
        var fetchTask = leecherEngine.GetMagnetMetadataAsync(magnet, cts.Token);

        // Bootstrap the transient torrent's connection to the seed (no tracker/DHT in tests)
        var portListener = seedEngine.PortListener ?? throw new InvalidOperationException("Seed engine has no port listener.");
        var seedEndpoint = new IPEndPoint(IPAddress.Loopback, portListener.Port);
        while (!fetchTask.IsCompleted && !cts.IsCancellationRequested)
        {
            // Offered unconditionally: the fetch torrent is deliberately invisible to GetTorrent,
            // so there is nothing to probe for. OnPeersFound resolves through the engine's own
            // routing, which does see it, and no-ops until it exists.
            leecherEngine.OnPeersFound(torrentFile.InfoHash, [seedEndpoint]);
            await Task.Delay(100, cancellationToken: TestContext.Current.CancellationToken);
        }

        var fetched = await fetchTask;

        Assert.Equal(torrentFile.InfoHash, fetched.InfoHash);
        Assert.Equal(1, fetched.FileCount);
        Assert.False(fetched.RawData.IsEmpty);

        // The transient fetch torrent must be gone
        Assert.Empty(leecherEngine.GetTorrents());

        // Metadata reuse: the returned bytes can be cached and re-added with no further
        // metadata download - the torrent has its file list immediately
        var reparsed = TorrentFile.Parse(fetched.RawData.ToArray());
        var added = await leecherEngine.AddTorrentAsync(reparsed, new AddTorrentOptions { StartImmediately = false });
        Assert.True(added.HasMetadata);
        Assert.Equal(1, added.FileCount);
    }

    [Fact(Timeout = 30000)]
    public async Task GetMagnetMetadata_Cancelled_RemovesTransientTorrent()
    {
        await using var engine = await CreateEngineAsync(_pathB);

        // Unreachable swarm: metadata can never arrive
        string magnet = $"magnet:?xt=urn:btih:{new string('a', 40)}";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.GetMagnetMetadataAsync(magnet, cts.Token));

        // The transient torrent must be cleaned up even on cancellation
        Assert.Empty(engine.GetTorrents());
    }

    [Fact(Timeout = 30000)]
    public async Task GetMagnetMetadata_IsInvisibleToTheCallerWhileItRuns()
    {
        // The fetch adds a real torrent to do its work. Everything a caller can observe - the
        // torrent list, the alert stream, a lookup by hash - must not show it.
        await using var engine = await CreateEngineAsync(_pathB);
        engine.Alerts.RegisterAlerts((uint)AlertCategory.Torrent | (uint)AlertCategory.Metadata);

        var infoHash = InfoHash.FromHex(new string('b', 40));
        string magnet = $"magnet:?xt=urn:btih:{infoHash.ToHexString()}";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(1));
        var fetchTask = engine.GetMagnetMetadataAsync(magnet, cts.Token);

        await Task.Delay(300, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(engine.GetTorrents());
        Assert.Null(engine.GetTorrent(infoHash));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetchTask);

        Assert.Empty(engine.GetTorrents());
        Assert.Empty(engine.Alerts.PopAlerts());
    }

    [Fact(Timeout = 30000)]
    public async Task GetMagnetMetadata_DoesNotBlockTheCallerFromAddingTheSameHash()
    {
        // The fetch used to occupy the registry slot for its hash, so a caller adding that hash
        // while a preview was running got TorrentAlreadyExistsException for no reason of their own.
        const string fileName = "concurrent.bin";
        byte[] data = new byte[16_384];
        Random.Shared.NextBytes(data);

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(16_384)
            .AddFile(fileName, data)
            .Build();

        await using var engine = await CreateEngineAsync(_pathB);

        string magnet = $"magnet:?xt=urn:btih:{torrentFile.InfoHash.ToHexString()}";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        var fetchTask = engine.GetMagnetMetadataAsync(magnet, cts.Token);
        await Task.Delay(300, cancellationToken: TestContext.Current.CancellationToken);

        var added = await engine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });

        Assert.Equal(torrentFile.InfoHash, added.Hash);
        Assert.Same(added, Assert.Single(engine.GetTorrents()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetchTask);

        // Discarding the fetch must not take the caller's torrent with it: the old teardown
        // resolved by hash, which would have removed exactly this one.
        Assert.Same(added, Assert.Single(engine.GetTorrents()));
        Assert.NotNull(engine.GetTorrent(torrentFile.InfoHash));
    }

    [Fact(Timeout = 30000)]
    public async Task DownloadSelectedFiles_SkipsUnselectedPieces()
    {
        var fileA = (Path: "file-a.bin", Data: new byte[16_384]);
        var fileB = (Path: "file-b.bin", Data: new byte[16_384]);
        Random.Shared.NextBytes(fileA.Data);
        Random.Shared.NextBytes(fileB.Data);

        await WriteFilesAsync(_pathA, fileA, fileB);

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName("SelectionTest")
            .WithPieceLength(16_384)
            .AddFile(fileA.Path, fileA.Data)
            .AddFile(fileB.Path, fileB.Data)
            .Build();

        await using var seedEngine = await CreateEngineAsync(_pathA);
        var seedTorrent = await seedEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });

        int validPieces = await seedTorrent.ForceRecheckAsync();
        Assert.Equal(torrentFile.PieceCount, validPieces);
        await seedTorrent.StartAsync();

        await using var leecherEngine = await CreateEngineAsync(_pathB);
        var leecherTorrent = await leecherEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });

        await leecherTorrent.SetFileSelectionAsync(0, new FileSelection { Selected = false, Priority = Priority.DoNotDownload });
        await leecherTorrent.SetFileSelectionAsync(1, new FileSelection { Selected = true, Priority = Priority.Normal });

        await leecherTorrent.StartAsync();

        await EnsureConnectedAsync(leecherEngine, leecherTorrent, seedEngine, ConnectionTimeout, cancellationToken: TestContext.Current.CancellationToken);

        await WaitForConditionAsync(leecherTorrent, t => t.SelectionFinished, DownloadTimeout, "selected file completion", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(leecherTorrent.Finished);
        Assert.Equal(1, leecherTorrent.PiecesReceived);
        Assert.Equal(fileB.Data.Length, (long)leecherTorrent.FinishedSelectedBytes);

        var fileInfoA = leecherTorrent.GetFileInfo(0);
        var fileInfoB = leecherTorrent.GetFileInfo(1);

        Assert.Equal(0, fileInfoA.DownloadedBytes);
        Assert.Equal(fileB.Data.Length, fileInfoB.DownloadedBytes);

        byte[] downloadedData = await ReadAllBytesSharedAsync(Path.Combine(_pathB, fileB.Path));
        Assert.Equal(fileB.Data, downloadedData);
    }

    [Fact(Timeout = 30000)]
    public async Task ResumeData_RestoresProgressAndCompletesDownload()
    {
        const string fileName = "resume.bin";
        byte[] data = new byte[128 * 1024];
        Random.Shared.NextBytes(data);

        await WriteFilesAsync(_pathA, (fileName, data));

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(16_384)
            .AddFile(fileName, data)
            .Build();

        Action<Settings> throttle = settings =>
        {
            settings.Transfer.MaxDownloadSpeed = 16 * 1024;
            settings.Transfer.MaxUploadSpeed = 16 * 1024;
        };

        await using var seedEngine = await CreateEngineAsync(_pathA, throttle);
        var seedTorrent = await seedEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });

        int validPieces = await seedTorrent.ForceRecheckAsync();
        Assert.Equal(torrentFile.PieceCount, validPieces);
        await seedTorrent.StartAsync();

        TorrentResumeData resumeData;
        int piecesBefore;

        await using (var leecherEngine = await CreateEngineAsync(_pathB, throttle))
        {
            var leecherTorrent = await leecherEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = true });
            await EnsureConnectedAsync(leecherEngine, leecherTorrent, seedEngine, ConnectionTimeout, cancellationToken: TestContext.Current.CancellationToken);

            await WaitForConditionAsync(leecherTorrent, t => t.PiecesReceived >= 2 || t.Finished, TimeSpan.FromSeconds(10), "partial download", cancellationToken: TestContext.Current.CancellationToken);

            await leecherTorrent.StopAsync();
            piecesBefore = leecherTorrent.PiecesReceived;
            resumeData = leecherTorrent.GetResumeData();
        }

        await using var resumedEngine = await CreateEngineAsync(_pathB, throttle);
        var resumedTorrent = await resumedEngine.AddTorrentAsync(
            torrentFile,
            new AddTorrentOptions
            {
                StartImmediately = false,
                ResumeData = resumeData
            });

        Assert.True(resumedTorrent.PiecesReceived >= piecesBefore);

        await resumedTorrent.StartAsync();
        await EnsureConnectedAsync(resumedEngine, resumedTorrent, seedEngine, ConnectionTimeout, cancellationToken: TestContext.Current.CancellationToken);

        await WaitForConditionAsync(resumedTorrent, t => t.Finished, DownloadTimeout, "resume completion", cancellationToken: TestContext.Current.CancellationToken);

        byte[] downloadedData = await ReadAllBytesSharedAsync(Path.Combine(_pathB, fileName));
        Assert.Equal(data, downloadedData);
    }

    private async Task<ClientEngine> CreateEngineAsync(string downloadPath, Action<Settings>? configure = null)
    {
        var settings = new Settings
        {
            Files = { DefaultDownloadPath = downloadPath },
            Connection =
            {
                TcpPort = 0,
                UdpPort = 0,
                EnableLsd = false,
                EnableUtpIn = false,
                EnableUtpOut = false,
                PreferUtp = false,
                UpnpPortMapping = false,
                NatPmpPortMapping = false,
                Encryption = Encryption.Refuse
            },
            Dht = { Enabled = false }
        };

        configure?.Invoke(settings);

        var options = new TorrentClientOptions
        {
            LoggerFactory = _loggerFactory,
            Settings = settings
        };

        var engine = ClientEngine.Create(options);
        await engine.InitializeAsync();
        return engine;
    }

    private static async Task EnsureConnectedAsync(ClientEngine leecherEngine, ITorrent leecherTorrent, ClientEngine seedEngine, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var portListener = seedEngine.PortListener ?? throw new InvalidOperationException("Seed engine has no port listener.");
        int port = portListener.Port;
        Assert.True(port > 0);

        var seedEndpoint = new IPEndPoint(IPAddress.Loopback, port);
        var sw = Stopwatch.StartNew();
        while (leecherTorrent.Peers.ConnectedCount == 0 && sw.Elapsed < timeout && !cancellationToken.IsCancellationRequested)
        {
            leecherEngine.OnPeersFound(leecherTorrent.Hash, [seedEndpoint]);
            await Task.Delay(200);
        }

        Assert.True(leecherTorrent.Peers.ConnectedCount > 0,
            $"Timed out after {timeout} waiting for peer connection. {IntegrationTestDiagnostics.DescribeTorrent(leecherTorrent)}");
    }

    private static async Task WaitForConditionAsync(ITorrent torrent, Func<ITorrent, bool> condition, TimeSpan timeout, string description, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        while (!condition(torrent) && sw.Elapsed < timeout && !cancellationToken.IsCancellationRequested)
        {
            if (torrent.LastException != null)
            {
                throw new InvalidOperationException($"Torrent error while waiting for {description}: {torrent.LastException.Message}", torrent.LastException);
            }

            await Task.Delay(200);
        }

        Assert.True(condition(torrent), $"Timed out after {timeout} waiting for {description}. {IntegrationTestDiagnostics.DescribeTorrent(torrent)}");
    }

    private static async Task WriteFilesAsync(string rootPath, params (string Path, byte[] Data)[] files)
    {
        foreach (var file in files)
        {
            string fullPath = Path.Combine(rootPath, file.Path);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            await File.WriteAllBytesAsync(fullPath, file.Data);
        }
    }

    private static async Task<byte[]> ReadAllBytesSharedAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
        byte[] buffer = new byte[stream.Length];
        int read = 0;
        while (read < buffer.Length)
        {
            int bytesRead = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read));
            if (bytesRead == 0)
            {
                break;
            }
            read += bytesRead;
        }
        return buffer;
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, true);
            }
        }
        catch { }
    }
}





