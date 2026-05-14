using System.Net;
using Microsoft.Extensions.Logging;
using PeerSharp.Internals;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Integration;

public class FullSystemTests : IDisposable
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
        var fileName = "utp.bin";
        byte[] data = new byte[64 * 1024];
        Random.Shared.NextBytes(data);
        await WriteFileAsync(_pathA, fileName, data);

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(16_384)
            .AddFile(fileName, data)
            .Build();

        Action<Settings> config = s =>

        {

            s.Connection.EnableTcpIn = false; // Force uTP

            s.Connection.EnableTcpOut = false; // Force uTP outgoing

            s.Connection.EnableUtpIn = true;

            s.Connection.EnableUtpOut = true;

            s.Connection.PreferUtp = true;

            s.Connection.UtpWarmupSeconds = 0; // Bypass warmup to allow uTP immediately

            s.Dht.Enabled = false;

        };



        await using var seedEngine = await CreateEngineAsync(_pathA, config);

        var seedTorrent = await seedEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });

        await seedTorrent.ForceRecheckAsync();

        await seedTorrent.StartAsync();



        await using var leecherEngine = await CreateEngineAsync(_pathB, config);

        var leecherTorrent = await leecherEngine.AddTorrentAsync(torrentFile);



        await EnsureConnectedAsync(leecherEngine, leecherTorrent, seedEngine, TimeSpan.FromSeconds(10));

        await WaitForConditionAsync(leecherTorrent, t => t.Finished, TimeSpan.FromSeconds(30), "uTP download completion");



        byte[] downloadedData = await ReadAllBytesSharedAsync(Path.Combine(_pathB, fileName));

        Assert.Equal(data, downloadedData);

    }



    [Fact(Timeout = 30000)]
    public async Task Download_Encrypted_Succeeds()

    {

        var fileName = "encrypted.bin";

        byte[] data = new byte[64 * 1024];

        Random.Shared.NextBytes(data);

        await WriteFileAsync(_pathA, fileName, data);



        var torrentFile = new ApiTorrentFileBuilder()

            .WithName(fileName)

            .WithPieceLength(16_384)

            .AddFile(fileName, data)

            .Build();



        Action<Settings> config = s =>

        {

            s.Connection.Encryption = Encryption.Require;

            s.Dht.Enabled = false;

        };



        await using var seedEngine = await CreateEngineAsync(_pathA, config);

        var seedTorrent = await seedEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });

        await seedTorrent.ForceRecheckAsync();

        await seedTorrent.StartAsync();



        await using var leecherEngine = await CreateEngineAsync(_pathB, config);

        var leecherTorrent = await leecherEngine.AddTorrentAsync(torrentFile);



        await EnsureConnectedAsync(leecherEngine, leecherTorrent, seedEngine, TimeSpan.FromSeconds(10));

        await WaitForConditionAsync(leecherTorrent, t => t.Finished, TimeSpan.FromSeconds(20), "encrypted download completion");



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



        var fileName = "ipv6.bin";

        byte[] data = new byte[64 * 1024];

        Random.Shared.NextBytes(data);

        await WriteFileAsync(_pathA, fileName, data);



        var torrentFile = new ApiTorrentFileBuilder()

            .WithName(fileName)

            .WithPieceLength(16_384)

            .AddFile(fileName, data)

            .Build();



        Action<Settings> config = s => s.Dht.Enabled = false;



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

        await WaitForConditionAsync(leecherTorrent, t => t.Finished, TimeSpan.FromSeconds(20), "IPv6 download completion");



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
        // For uTP, we need the UDP port. For TCP, the TCP port.
        // Seed engine might have different ports for TCP and UDP if configured random (0).
        // Check settings.

        int port;
        bool isUtp = !seedEngine.Settings.Connection.EnableTcpIn;

        if (isUtp)
        {
            port = seedEngine.Settings.Connection.UdpPort;
        }
        else
        {
            port = seedEngine.Settings.Connection.TcpPort;
        }

        Assert.True(port > 0, "Seed engine port not bound");

        var seedEndpoint = new IPEndPoint(IPAddress.Loopback, port);
        var cts = new CancellationTokenSource(timeout);

        while (leecherTorrent.Peers.ConnectedCount == 0 && !cts.IsCancellationRequested)
        {
            leecherEngine.OnPeersFound(leecherTorrent.Hash, [seedEndpoint]);
            try { await Task.Delay(200, cts.Token); } catch { break; }
        }

        Assert.True(leecherTorrent.Peers.ConnectedCount > 0, "Timed out waiting for peer connection.");
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

    public void Dispose()
    {
        _loggerFactory.Dispose();
        try { Directory.Delete(_testRoot, true); } catch { }
    }
}





