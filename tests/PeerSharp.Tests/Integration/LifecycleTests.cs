using System.Net;
using Microsoft.Extensions.Logging;
using PeerSharp.Internals;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Integration;

public class LifecycleTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _pathA;
    private readonly string _pathB;
    private readonly ILoggerFactory _loggerFactory;

    public LifecycleTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "MtTorrentLifecycleTests_" + Guid.NewGuid().ToString("N"));
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

    [Fact(Timeout = 60000)]
    public async Task StopAndRestart_AllowsCompletion()
    {
        var fileName = "restart.bin";
        byte[] data = new byte[2_000_000];
        Random.Shared.NextBytes(data);
        await WriteFileAsync(_pathA, fileName, data);

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(16_384)
            .AddFile(fileName, data)
            .Build();

        Action<Settings> config = s =>
        {
            s.Dht.Enabled = false;
            s.Connection.EnableLsd = false;
        };

        await using var seedEngine = await CreateEngineAsync(_pathA, config);
        var seedTorrent = await seedEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });
        await seedTorrent.ForceRecheckAsync();
        await seedTorrent.StartAsync();

        await using var leecherEngine = await CreateEngineAsync(_pathB, config);
        var leecherTorrent = await leecherEngine.AddTorrentAsync(torrentFile);

        await EnsureConnectedAsync(leecherEngine, leecherTorrent, seedEngine, TimeSpan.FromSeconds(10));

        await WaitForProgressOrCompletionAsync(leecherTorrent, TimeSpan.FromSeconds(15));

        await leecherTorrent.StopAsync();
        Assert.Equal(TorrentState.Stopped, leecherTorrent.State);

        await leecherTorrent.StartAsync();
        await EnsureConnectedAsync(leecherEngine, leecherTorrent, seedEngine, TimeSpan.FromSeconds(10));
        await WaitForConditionAsync(leecherTorrent, t => t.Finished, TimeSpan.FromSeconds(30), "restart download completion");

        Assert.Equal(0, leecherTorrent.DataLeft);

        byte[] downloadedData = await ReadAllBytesSharedAsync(Path.Combine(_pathB, fileName));
        Assert.Equal(data, downloadedData);
    }

    [Fact(Timeout = 30000)]
    public async Task RemoveTorrent_DeleteFiles_RemovesData()
    {
        var fileName = "delete.bin";
        byte[] data = new byte[128 * 1024];
        Random.Shared.NextBytes(data);

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(16_384)
            .AddFile(fileName, data)
            .Build();

        await using var engine = await CreateEngineAsync(_pathA, s =>
        {
            s.Dht.Enabled = false;
            s.Connection.EnableLsd = false;
        });

        var torrent = await engine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });

        string filePath = Path.Combine(_pathA, fileName);
        await WriteFileAsync(_pathA, fileName, data);

        await torrent.ForceRecheckAsync();
        await WaitForConditionAsync(torrent, t => t.Finished, TimeSpan.FromSeconds(10), "recheck completion");

        Assert.True(System.IO.File.Exists(filePath));

        await engine.RemoveTorrentAsync(torrent, RemoveOptions.DeleteFiles);

        await WaitForFileDeletionAsync(filePath, TimeSpan.FromSeconds(5));
        Assert.False(System.IO.File.Exists(filePath));
    }

    [Fact(Timeout = 60000)]
    public async Task FileSelection_DownloadsOnlySelectedFiles()
    {
        var fileNameA = "selected.bin";
        var fileNameB = "unselected.bin";
        byte[] dataA = new byte[256 * 1024];
        byte[] dataB = new byte[128 * 1024];
        Random.Shared.NextBytes(dataA);
        Random.Shared.NextBytes(dataB);

        await WriteFileAsync(_pathA, fileNameA, dataA);
        await WriteFileAsync(_pathA, fileNameB, dataB);

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName("selection")
            .WithPieceLength(16_384)
            .AddFile(fileNameA, dataA)
            .AddFile(fileNameB, dataB)
            .Build();

        Action<Settings> config = s =>
        {
            s.Dht.Enabled = false;
            s.Connection.EnableLsd = false;
        };

        await using var seedEngine = await CreateEngineAsync(_pathA, config);
        var seedTorrent = await seedEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });
        await seedTorrent.ForceRecheckAsync();
        await seedTorrent.StartAsync();

        await using var leecherEngine = await CreateEngineAsync(_pathB, config);
        var leecherTorrent = await leecherEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });

        await leecherTorrent.SetFileSelectionAsync(0, new FileSelection { Selected = true, Priority = Priority.Normal });
        await leecherTorrent.SetFileSelectionAsync(1, new FileSelection { Selected = false, Priority = Priority.DoNotDownload });

        await leecherTorrent.StartAsync();
        await EnsureConnectedAsync(leecherEngine, leecherTorrent, seedEngine, TimeSpan.FromSeconds(10));

        await WaitForConditionAsync(leecherTorrent, t => t.SelectionFinished, TimeSpan.FromSeconds(30), "selection download completion");

        string selectedPath = Path.Combine(_pathB, fileNameA);
        string unselectedPath = Path.Combine(_pathB, fileNameB);

        Assert.True(System.IO.File.Exists(selectedPath));
        Assert.False(System.IO.File.Exists(unselectedPath));

        byte[] downloadedA = await ReadAllBytesSharedAsync(selectedPath);
        Assert.Equal(dataA, downloadedA);
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

    private static async Task WriteFileAsync(string rootPath, string fileName, byte[] data)
    {
        string fullPath = Path.Combine(rootPath, fileName);
        await System.IO.File.WriteAllBytesAsync(fullPath, data);
    }

    private static async Task EnsureConnectedAsync(ClientEngine leecherEngine, ITorrent leecherTorrent, ClientEngine seedEngine, TimeSpan timeout)
    {
        int port = seedEngine.Settings.Connection.TcpPort;
        Assert.True(port > 0, "Seed engine port not bound");

        var seedEndpoint = new IPEndPoint(IPAddress.Loopback, port);
        var cts = new CancellationTokenSource(timeout);

        while (leecherTorrent.Peers.ConnectedCount == 0 && !cts.IsCancellationRequested)
        {
            leecherEngine.OnPeersFound(leecherTorrent.Hash, new List<IPEndPoint> { seedEndpoint });
            try { await Task.Delay(200, cts.Token); } catch { break; }
        }

        Assert.True(leecherTorrent.Peers.ConnectedCount > 0, "Timed out waiting for peer connection.");
    }

    private static async Task WaitForProgressOrCompletionAsync(ITorrent torrent, TimeSpan timeout)
    {
        var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (torrent.PiecesReceived > 0 && torrent.PiecesReceived < torrent.PieceCount)
            {
                return;
            }

            if (torrent.Finished)
            {
                return;
            }

            if (torrent.LastException != null)
            {
                throw new InvalidOperationException($"Torrent error: {torrent.LastException.Message}", torrent.LastException);
            }

            try { await Task.Delay(200, cts.Token); } catch { break; }
        }
    }

    private static async Task WaitForConditionAsync(ITorrent torrent, Func<ITorrent, bool> condition, TimeSpan timeout, string description)
    {
        var cts = new CancellationTokenSource(timeout);
        while (!condition(torrent) && !cts.IsCancellationRequested)
        {
            if (torrent.LastException != null)
            {
                throw new InvalidOperationException($"Torrent error: {torrent.LastException.Message}", torrent.LastException);
            }
            try { await Task.Delay(200, cts.Token); } catch { break; }
        }

        Assert.True(condition(torrent), $"Timed out waiting for {description}. State={torrent.State}, Pieces={torrent.PiecesReceived}/{torrent.PieceCount}");
    }

    private static async Task WaitForFileDeletionAsync(string path, TimeSpan timeout)
    {
        var cts = new CancellationTokenSource(timeout);
        while (System.IO.File.Exists(path) && !cts.IsCancellationRequested)
        {
            try { await Task.Delay(100, cts.Token); } catch { break; }
        }
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
        try { Directory.Delete(_testRoot, true); } catch { }
    }
}




