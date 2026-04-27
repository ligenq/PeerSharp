using System.Net;
using PeerSharp.Internals;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Integration;

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
        var fileName = "dummy.bin";
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

        await EnsureConnectedAsync(leecherEngine, leecherTorrent, seedEngine, ConnectionTimeout);

        await WaitForConditionAsync(leecherTorrent, t => t.Finished, DownloadTimeout, "download completion");

        Assert.Equal(dummyData.Length, (long)leecherTorrent.FinishedBytes);

        var downloadedInfo = leecherTorrent.GetFileInfo(0);
        byte[] downloadedData = await ReadAllBytesSharedAsync(Path.Combine(_pathB, downloadedInfo.Path));
        Assert.Equal(dummyData, downloadedData);
    }

    [Fact(Timeout = 30000)]
    public async Task DownloadFile_FromMagnetMetadataExchange_Succeeds()
    {
        var fileName = "metadata.bin";
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

        await EnsureConnectedAsync(leecherEngine, leecherTorrent, seedEngine, ConnectionTimeout);

        await WaitForConditionAsync(leecherTorrent, t => t.HasMetadata, MetadataTimeout, "metadata download");
        await WaitForConditionAsync(leecherTorrent, t => t.Finished, DownloadTimeout, "download completion");

        var downloadedInfo = leecherTorrent.GetFileInfo(0);
        byte[] downloadedData = await ReadAllBytesSharedAsync(Path.Combine(_pathB, downloadedInfo.Path));
        Assert.Equal(dummyData, downloadedData);
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

        await EnsureConnectedAsync(leecherEngine, leecherTorrent, seedEngine, ConnectionTimeout);

        await WaitForConditionAsync(leecherTorrent, t => t.SelectionFinished, DownloadTimeout, "selected file completion");

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
        var fileName = "resume.bin";
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
            await EnsureConnectedAsync(leecherEngine, leecherTorrent, seedEngine, ConnectionTimeout);

            await WaitForConditionAsync(leecherTorrent, t => t.PiecesReceived >= 2 || t.Finished, TimeSpan.FromSeconds(10), "partial download");

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
        await EnsureConnectedAsync(resumedEngine, resumedTorrent, seedEngine, ConnectionTimeout);

        await WaitForConditionAsync(resumedTorrent, t => t.Finished, DownloadTimeout, "resume completion");

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

    private static async Task EnsureConnectedAsync(ClientEngine leecherEngine, ITorrent leecherTorrent, ClientEngine seedEngine, TimeSpan timeout)
    {
        var portListener = seedEngine.PortListener ?? throw new InvalidOperationException("Seed engine has no port listener.");
        int port = portListener.Port;
        Assert.True(port > 0);

        var seedEndpoint = new IPEndPoint(IPAddress.Loopback, port);
        var sw = Stopwatch.StartNew();
        while (leecherTorrent.Peers.ConnectedCount == 0 && sw.Elapsed < timeout)
        {
            leecherEngine.OnPeersFound(leecherTorrent.Hash, new List<IPEndPoint> { seedEndpoint });
            await Task.Delay(200);
        }

        Assert.True(leecherTorrent.Peers.ConnectedCount > 0,
            $"Timed out after {timeout} waiting for peer connection. {DescribeTorrent(leecherTorrent)}");
    }

    private static async Task WaitForConditionAsync(ITorrent torrent, Func<ITorrent, bool> condition, TimeSpan timeout, string description)
    {
        var sw = Stopwatch.StartNew();
        while (!condition(torrent) && sw.Elapsed < timeout)
        {
            if (torrent.LastException != null)
            {
                throw new InvalidOperationException($"Torrent error while waiting for {description}: {torrent.LastException.Message}", torrent.LastException);
            }

            await Task.Delay(200);
        }

        Assert.True(condition(torrent), $"Timed out after {timeout} waiting for {description}. {DescribeTorrent(torrent)}");
    }

    private static string DescribeTorrent(ITorrent torrent)
    {
        return $"State={torrent.State}, Progress={torrent.Progress:P0}, SelectionProgress={torrent.SelectionProgress:P0}, Pieces={torrent.PiecesReceived}/{torrent.PieceCount}, Peers={torrent.Peers.ConnectedCount}";
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





