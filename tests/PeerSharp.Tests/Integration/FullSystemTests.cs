using System.Net;
using Microsoft.Extensions.Logging;
using PeerSharp.Internals;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Integration;

public sealed class FullSystemTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _pathA;
    private readonly string _pathB;
    private readonly ILoggerFactory _loggerFactory;

    public FullSystemTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "MtTorrentSystemTests_" + Guid.NewGuid().ToString("N"));
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
    public async Task Download_OverUtp_Succeeds()
    {
        const string fileName = "utp.bin";
        byte[] data = new byte[64 * 1024];
        Random.Shared.NextBytes(data);
        await WriteFileAsync(_pathA, fileName, data);

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(16_384)
            .AddFile(fileName, data)
            .Build();

        static void config(Settings s)
        {
            s.Connection.EnableTcpIn = false; // Force uTP
            s.Connection.EnableTcpOut = false; // Force uTP outgoing
            s.Connection.EnableUtpIn = true;
            s.Connection.EnableUtpOut = true;
            s.Connection.PreferUtp = true;
            s.Connection.UtpWarmupSeconds = 0; // Bypass warmup to allow uTP immediately
            s.Connection.PeerReconnectBaseSeconds = 1;
            s.Connection.PeerReconnectJitterMs = 100;
            s.Dht.Enabled = false;
        }

        await using var seedEngine = await CreateEngineAsync(_pathA, config);

        var seedTorrent = await seedEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });

        await seedTorrent.ForceRecheckAsync();

        await seedTorrent.StartAsync();

        await using var leecherEngine = await CreateEngineAsync(_pathB, config);

        var leecherTorrent = await leecherEngine.AddTorrentAsync(torrentFile);

        await EnsureConnectedAsync(leecherEngine, leecherTorrent, seedEngine, TimeSpan.FromSeconds(10));

        await WaitForConditionAsync(leecherTorrent, t => t.Finished, TimeSpan.FromSeconds(30), "uTP download completion",
            onPoll: () => leecherEngine.OnPeersFound(leecherTorrent.Hash, [GetSeedEndpoint(seedEngine)]));

        byte[] downloadedData = await ReadAllBytesSharedAsync(Path.Combine(_pathB, fileName));

        Assert.Equal(data, downloadedData);
    }

    [Fact(Timeout = 90000)]
    public async Task Download_Encrypted_Succeeds()
    {
        const string fileName = "encrypted.bin";

        byte[] data = new byte[64 * 1024];

        Random.Shared.NextBytes(data);

        await WriteFileAsync(_pathA, fileName, data);

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(16_384)
            .AddFile(fileName, data)
            .Build();

        static void config(Settings s)
        {
            s.Connection.Encryption = Encryption.Require;
            s.Connection.PeerReconnectBaseSeconds = 1;
            s.Connection.PeerReconnectJitterMs = 100;
            s.Dht.Enabled = false;
        }

        await using var seedEngine = await CreateEngineAsync(_pathA, config);

        var seedTorrent = await seedEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });

        int validPieces = await seedTorrent.ForceRecheckAsync();

        Assert.Equal(seedTorrent.PieceCount, validPieces);
        Assert.True(seedTorrent.Finished, "Seed torrent did not finish recheck before starting.");

        await seedTorrent.StartAsync();

        await using var leecherEngine = await CreateEngineAsync(_pathB, config);

        var leecherTorrent = await leecherEngine.AddTorrentAsync(torrentFile);

        await WaitForConditionAsync(leecherTorrent, t => t.PiecesReceived > 0 || t.Finished, TimeSpan.FromSeconds(30), "encrypted download start",
            onPoll: () => leecherEngine.OnPeersFound(leecherTorrent.Hash, [GetSeedEndpoint(seedEngine)]));

        await WaitForConditionAsync(leecherTorrent, t => t.Finished, TimeSpan.FromSeconds(45), "encrypted download completion",
            onPoll: () => leecherEngine.OnPeersFound(leecherTorrent.Hash, [GetSeedEndpoint(seedEngine)]));

        byte[] downloadedData = await ReadAllBytesSharedAsync(Path.Combine(_pathB, fileName));

        Assert.Equal(data, downloadedData);
    }

    [Fact(Timeout = 30000)]
    public async Task IPv6_Download_Succeeds()
    {
        if (!System.Net.Sockets.Socket.OSSupportsIPv6)
        {
            return; // Skip on non-IPv6 systems
        }

        const string fileName = "ipv6.bin";

        byte[] data = new byte[64 * 1024];

        Random.Shared.NextBytes(data);

        await WriteFileAsync(_pathA, fileName, data);

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(16_384)
            .AddFile(fileName, data)
            .Build();

        static void config(Settings s)
        {
            s.Dht.Enabled = false;
            s.Connection.PeerReconnectBaseSeconds = 1;
            s.Connection.PeerReconnectJitterMs = 100;
        }

        await using var seedEngine = await CreateEngineAsync(_pathA, config);

        var seedTorrent = await seedEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });

        await seedTorrent.ForceRecheckAsync();

        await seedTorrent.StartAsync();

        await using var leecherEngine = await CreateEngineAsync(_pathB, config);

        var leecherTorrent = await leecherEngine.AddTorrentAsync(torrentFile);

        await EnsureConnectedAsync(leecherEngine, leecherTorrent, seedEngine, TimeSpan.FromSeconds(10));

        // Connect via IPv6 Loopback

        var seedPort = seedEngine.Settings.Connection.TcpPort;

        var ipv6Endpoint = new IPEndPoint(IPAddress.IPv6Loopback, seedPort);

        leecherEngine.OnPeersFound(leecherTorrent.Hash, [ipv6Endpoint]);

        await WaitForConditionAsync(leecherTorrent, t => t.Peers.ConnectedCount > 0, TimeSpan.FromSeconds(5), "IPv6 connection");

        await WaitForConditionAsync(leecherTorrent, t => t.Finished, TimeSpan.FromSeconds(20), "IPv6 download completion",
            onPoll: () => leecherEngine.OnPeersFound(leecherTorrent.Hash, [ipv6Endpoint]));

        byte[] downloadedData = await ReadAllBytesSharedAsync(Path.Combine(_pathB, fileName));

        Assert.Equal(data, downloadedData);
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
                TcpPort = 0, // Random
                UdpPort = 0, // Random
                EnableLsd = false,
                EnableUtpIn = false, // Default off, enabled by test
                EnableUtpOut = false,
                PreferUtp = false,
                UpnpPortMapping = false,
                NatPmpPortMapping = false,
                Encryption = Encryption.Refuse // Default refuse, enabled by test
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
        await File.WriteAllBytesAsync(fullPath, data);
    }

    private static async Task EnsureConnectedAsync(ClientEngine leecherEngine, ITorrent leecherTorrent, ClientEngine seedEngine, TimeSpan timeout)
    {
        var seedEndpoint = GetSeedEndpoint(seedEngine);
        var cts = new CancellationTokenSource(timeout);

        while (leecherTorrent.Peers.ConnectedCount == 0 && !cts.IsCancellationRequested)
        {
            leecherEngine.OnPeersFound(leecherTorrent.Hash, [seedEndpoint]);
            try { await Task.Delay(200, cts.Token); } catch { break; }
        }

        Assert.True(leecherTorrent.Peers.ConnectedCount > 0, "Timed out waiting for peer connection.");
    }

    private static async Task WaitForConditionAsync(ITorrent torrent, Func<ITorrent, bool> condition, TimeSpan timeout, string description, Action? onPoll = null)
    {
        var cts = new CancellationTokenSource(timeout);
        while (!condition(torrent) && !cts.IsCancellationRequested)
        {
            if (torrent.LastException != null)
            {
                throw new InvalidOperationException($"Torrent error: {torrent.LastException.Message}", torrent.LastException);
            }
            onPoll?.Invoke();
            try { await Task.Delay(200, cts.Token); } catch { break; }
        }

        Assert.True(condition(torrent), $"Timed out waiting for {description}. State={torrent.State}, Pieces={torrent.PiecesReceived}/{torrent.PieceCount}");
    }

    /// <summary>
    /// The seed's connectable endpoint on loopback (TCP port, or UDP port for uTP-only engines).
    /// </summary>
    private static IPEndPoint GetSeedEndpoint(ClientEngine seedEngine)
    {
        bool isUtp = !seedEngine.Settings.Connection.EnableTcpIn;
        int port = isUtp ? seedEngine.Settings.Connection.UdpPort : seedEngine.Settings.Connection.TcpPort;
        Assert.True(port > 0, "Seed engine port not bound");
        return new IPEndPoint(IPAddress.Loopback, port);
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
        try { Directory.Delete(_testRoot, true); } catch { }
    }
}
