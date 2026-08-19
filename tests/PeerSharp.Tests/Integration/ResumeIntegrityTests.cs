using Microsoft.Extensions.Logging;
using PeerSharp.Internals;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Integration;

/// <summary>
/// Stopping, restarting and verifying a real transfer on disk.
///
/// <para>
/// A download that completes is not the same as a download that is correct. Piece verification runs on
/// the way in, so an engine can report success while the bytes on disk are wrong - through a partial
/// write at shutdown, an offset mistake in a multi-file layout, or resume state that disagrees with
/// what was actually written. These check the file, not the progress counter.
/// </para>
///
/// <para>
/// Restart is covered because it is where the two sources of truth - the piece bitfield and the bytes
/// on disk - can drift apart. Re-downloading everything after a restart is a bug too, just a quieter
/// one: it looks like a slow download rather than a broken one.
/// </para>
/// </summary>
[Collection("Integration")]
public class ResumeIntegrityTests : IDisposable
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(60);

    private const int PieceLength = 64 * 1024;

    private readonly string _testRoot;
    private readonly string _seedPath;
    private readonly string _leechPath;
    private readonly ILoggerFactory _loggerFactory;

    public ResumeIntegrityTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "PeerSharpResume_" + Guid.NewGuid().ToString("N"));
        _seedPath = Path.Combine(_testRoot, "seed");
        _leechPath = Path.Combine(_testRoot, "leech");
        Directory.CreateDirectory(_seedPath);
        Directory.CreateDirectory(_leechPath);
        _loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
    }

    [Fact(Timeout = 180000)]
    public async Task CompletedDownload_MatchesTheSourceByteForByte()
    {
        var (torrentFile, payload) = await CreatePayloadAsync("single.bin", 2 * 1024 * 1024);

        await using var seedEngine = await CreateEngineAsync(_seedPath);
        await StartSeedAsync(seedEngine, torrentFile);

        await using var leechEngine = await CreateEngineAsync(_leechPath);
        var leech = await leechEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = true });

        await EnsureConnectedAsync(leechEngine, leech, seedEngine);
        await WaitForAsync(() => leech.Finished, CompletionTimeout, "download completion");

        await leech.StopAsync();

        byte[] downloaded = await File.ReadAllBytesAsync(
            Path.Combine(_leechPath, leech.GetFileInfo(0).Path),
            TestContext.Current.CancellationToken);

        Assert.Equal(SHA256.HashData(payload), SHA256.HashData(downloaded));
    }

    [Fact(Timeout = 180000)]
    public async Task MultiFileDownload_MatchesEverySourceFile()
    {
        // Multi-file layouts are where offset arithmetic goes wrong: pieces straddle file boundaries, so
        // an off-by-one lands bytes in the neighbouring file and every piece still verifies.
        var files = new[]
        {
            ("a.bin", RandomBytes(PieceLength * 3 + 1234)),
            ("nested/b.bin", RandomBytes(PieceLength + 7)),
            ("nested/deeper/c.bin", RandomBytes(PieceLength * 2)),
        };

        foreach (var (path, data) in files)
        {
            string full = Path.Combine(_seedPath, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllBytesAsync(full, data, cancellationToken: TestContext.Current.CancellationToken);
        }

        var builder = new ApiTorrentFileBuilder().WithName("multi").WithPieceLength(PieceLength);
        foreach (var (path, data) in files)
        {
            builder.AddFile(path, data);
        }

        var torrentFile = builder.Build();

        await using var seedEngine = await CreateEngineAsync(_seedPath);
        await StartSeedAsync(seedEngine, torrentFile);

        await using var leechEngine = await CreateEngineAsync(_leechPath);
        var leech = await leechEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = true });

        await EnsureConnectedAsync(leechEngine, leech, seedEngine);
        await WaitForAsync(() => leech.Finished, CompletionTimeout, "download completion");
        await leech.StopAsync();

        for (int i = 0; i < files.Length; i++)
        {
            string actualPath = Path.Combine(_leechPath, leech.GetFileInfo(i).Path);
            byte[] actual = await File.ReadAllBytesAsync(actualPath, cancellationToken: TestContext.Current.CancellationToken);

            var expected = files.Single(f => Path.GetFileName(f.Item1) == Path.GetFileName(actualPath));
            Assert.Equal(SHA256.HashData(expected.Item2), SHA256.HashData(actual));
        }
    }

    [Fact(Timeout = 180000)]
    public async Task RestartMidDownload_KeepsVerifiedPiecesAndCompletesCorrectly()
    {
        var (torrentFile, payload) = await CreatePayloadAsync("resume.bin", 4 * 1024 * 1024);

        await using var seedEngine = await CreateEngineAsync(_seedPath);
        await StartSeedAsync(seedEngine, torrentFile);

        int piecesBeforeStop;
        int totalPieces = torrentFile.PieceCount;
        {
            // Rate limited so the stop genuinely lands mid-transfer. Unthrottled loopback finishes a few
            // megabytes before the first progress check, which would turn this into a restart of an
            // already-complete download and prove nothing about resume.
            await using var firstRun = await CreateEngineAsync(_leechPath);
            var leech = await firstRun.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = true });
            leech.DownloadLimitBytesPerSecond = 512 * 1024;

            await EnsureConnectedAsync(firstRun, leech, seedEngine);

            // Some pieces verified, others half-written: the moment the two sources of truth can diverge.
            await WaitForAsync(() => leech.PiecesReceived >= 4, CompletionTimeout, "partial progress");
            piecesBeforeStop = leech.PiecesReceived;
            await leech.StopAsync();
        }

        Assert.True(piecesBeforeStop > 0, "Nothing was downloaded before the restart, so resume was not exercised.");
        Assert.True(
            piecesBeforeStop < totalPieces,
            $"The download finished ({piecesBeforeStop}/{totalPieces} pieces) before it could be stopped, so this " +
            "run restarted a complete torrent rather than a partial one and did not exercise resume.");

        await using var secondRun = await CreateEngineAsync(_leechPath);
        var resumed = await secondRun.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });

        // A recheck is how a fresh engine learns what is already on disk. It must find at least what the
        // first run verified; finding zero means the completed data was not durable.
        int recheckedPieces = await resumed.ForceRecheckAsync();
        Assert.True(
            recheckedPieces >= piecesBeforeStop,
            $"After restart only {recheckedPieces} of the {piecesBeforeStop} pieces verified before the stop were " +
            "found on disk. Completed pieces are not surviving shutdown.");

        await resumed.StartAsync();
        await EnsureConnectedAsync(secondRun, resumed, seedEngine);
        await WaitForAsync(() => resumed.Finished, CompletionTimeout, "resumed download completion");
        await resumed.StopAsync();

        byte[] downloaded = await File.ReadAllBytesAsync(
            Path.Combine(_leechPath, resumed.GetFileInfo(0).Path),
            TestContext.Current.CancellationToken);

        Assert.Equal(SHA256.HashData(payload), SHA256.HashData(downloaded));
    }

    [Fact(Timeout = 180000)]
    public async Task Recheck_DetectsCorruptionOnDisk()
    {
        // The inverse guarantee: verification has to actually verify. A recheck that always reports
        // success would make the resume test above meaningless.
        var (torrentFile, payload) = await CreatePayloadAsync("corrupt.bin", 512 * 1024);

        string seededFile = Path.Combine(_seedPath, "corrupt.bin");

        await using var engine = await CreateEngineAsync(_seedPath);
        var torrent = await engine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });

        Assert.Equal(torrentFile.PieceCount, await torrent.ForceRecheckAsync());

        // Flip bytes in the middle of the first piece.
        byte[] corrupted = payload.ToArray();
        for (int i = 0; i < 64; i++)
        {
            corrupted[100 + i] ^= 0xFF;
        }

        await File.WriteAllBytesAsync(seededFile, corrupted, cancellationToken: TestContext.Current.CancellationToken);

        int validAfterCorruption = await torrent.ForceRecheckAsync();

        Assert.True(
            validAfterCorruption < torrentFile.PieceCount,
            "A recheck reported every piece valid after the file was corrupted on disk, so verification is not " +
            "actually reading and hashing the data.");
    }

    private static byte[] RandomBytes(int length)
    {
        byte[] data = new byte[length];
        Random.Shared.NextBytes(data);
        return data;
    }

    private async Task<(TorrentFile TorrentFile, byte[] Payload)> CreatePayloadAsync(string name, int size)
    {
        byte[] payload = RandomBytes(size);
        await File.WriteAllBytesAsync(Path.Combine(_seedPath, name), payload, cancellationToken: TestContext.Current.CancellationToken);

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName(name)
            .WithPieceLength(PieceLength)
            .AddFile(name, payload)
            .Build();

        return (torrentFile, payload);
    }

    private static async Task StartSeedAsync(ClientEngine engine, TorrentFile torrentFile)
    {
        var seed = await engine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });
        Assert.Equal(torrentFile.PieceCount, await seed.ForceRecheckAsync());
        await seed.StartAsync();
    }

    private async Task<ClientEngine> CreateEngineAsync(string downloadPath)
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

        var engine = ClientEngine.Create(new TorrentClientOptions
        {
            LoggerFactory = _loggerFactory,
            Settings = settings
        });

        await engine.InitializeAsync();
        return engine;
    }

    private static async Task EnsureConnectedAsync(ClientEngine leechEngine, ITorrent leechTorrent, ClientEngine seedEngine)
    {
        var portListener = seedEngine.PortListener ?? throw new InvalidOperationException("Seed engine has no port listener.");
        var seedEndpoint = new IPEndPoint(IPAddress.Loopback, portListener.Port);

        var clock = Stopwatch.StartNew();
        while (leechTorrent.Peers.ConnectedCount == 0 && clock.Elapsed < ConnectionTimeout)
        {
            leechEngine.OnPeersFound(leechTorrent.Hash, [seedEndpoint]);
            await Task.Delay(200);
        }

        Assert.True(leechTorrent.Peers.ConnectedCount > 0, "Timed out waiting for a peer connection.");
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string description)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < timeout)
        {
            await Task.Delay(100);
        }

        Assert.True(condition(), $"Timed out after {timeout} waiting for {description}.");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch (IOException) { /* Best effort. */ }
        catch (UnauthorizedAccessException) { /* Best effort. */ }

        _loggerFactory.Dispose();
    }
}
