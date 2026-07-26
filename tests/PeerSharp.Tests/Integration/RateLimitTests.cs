using Microsoft.Extensions.Logging;
using PeerSharp.Internals;
using System.Diagnostics;
using System.Net;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Integration;

/// <summary>
/// Rate limiting between two local peers.
///
/// <para>
/// A limit that silently does nothing is worse than no limit at all: anyone running on a metered or
/// shared connection is relying on it. These run over loopback, which is orders of magnitude faster
/// than any limit worth setting, so an unenforced limit shows up as the transfer finishing almost
/// instantly rather than as a marginal overshoot.
/// </para>
///
/// <para>
/// Both encryption modes are covered deliberately. Rate limiting used to live inside the encrypted
/// stream wrapper, which meant plaintext connections - what you get whenever a peer declines
/// encryption - were never limited at all.
/// </para>
/// </summary>
[Collection("Integration")]
public class RateLimitTests : IDisposable
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long to let the transfer run before measuring.</summary>
    private static readonly TimeSpan MeasureWindow = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Bytes per second the leecher is allowed. Far below loopback throughput, so the difference
    /// between enforced and ignored is unmistakable.
    /// </summary>
    private const int LimitBytesPerSecond = 512 * 1024;

    /// <summary>
    /// Large enough that it cannot finish inside the measurement window at the limit, so the window
    /// measures the limiter rather than the end of the file.
    /// </summary>
    private const int PayloadBytes = 16 * 1024 * 1024;

    private readonly string _testRoot;
    private readonly string _seedPath;
    private readonly string _leechPath;
    private readonly ILoggerFactory _loggerFactory;

    public RateLimitTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "PeerSharpRateLimit_" + Guid.NewGuid().ToString("N"));
        _seedPath = Path.Combine(_testRoot, "seed");
        _leechPath = Path.Combine(_testRoot, "leech");
        Directory.CreateDirectory(_seedPath);
        Directory.CreateDirectory(_leechPath);

        _loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
    }

    [Theory(Timeout = 120000)]
    [InlineData(Encryption.Refuse)]
    [InlineData(Encryption.Require)]
    public async Task GlobalDownloadLimit_IsEnforced(Encryption encryption)
    {
        var measured = await MeasureAsync(encryption, settings => settings.Transfer.MaxDownloadSpeed = LimitBytesPerSecond);

        AssertWithinLimit(measured, encryption);
    }

    [Theory(Timeout = 120000)]
    [InlineData(Encryption.Refuse)]
    [InlineData(Encryption.Require)]
    public async Task PerTorrentDownloadLimit_IsEnforced(Encryption encryption)
    {
        var measured = await MeasureAsync(
            encryption,
            configureSettings: null,
            configureTorrent: torrent => torrent.DownloadLimitBytesPerSecond = LimitBytesPerSecond);

        AssertWithinLimit(measured, encryption);
    }

    [Fact(Timeout = 120000)]
    public async Task NoLimit_TransfersFasterThanTheLimitedCase()
    {
        // The control. Without it a limiter that simply broke the transfer would pass every assertion
        // above, since "downloaded almost nothing" also satisfies "stayed under the cap".
        var measured = await MeasureAsync(Encryption.Refuse, configureSettings: null);

        Assert.True(
            measured.BytesDownloaded > LimitBytesPerSecond * MeasureWindow.TotalSeconds,
            $"An unlimited local transfer moved only {measured.BytesDownloaded:N0} bytes in " +
            $"{measured.Elapsed.TotalSeconds:F1}s, which is no faster than the limited case. The limit tests " +
            "above cannot distinguish a working limiter from a broken transfer.");
    }

    private static void AssertWithinLimit(TransferMeasurement measured, Encryption encryption)
    {
        // Generous: quota is granted in batches and bursts up to 3x the limit by design, plus the
        // measurement window starts before the first byte arrives. The failure this catches is a limit
        // ignored entirely, which overshoots by orders of magnitude rather than by a factor of three.
        double allowed = LimitBytesPerSecond * measured.Elapsed.TotalSeconds * 4;

        Assert.True(
            measured.BytesDownloaded <= allowed,
            $"With {encryption} encryption and a {LimitBytesPerSecond:N0} bytes/s limit, {measured.BytesDownloaded:N0} " +
            $"bytes arrived in {measured.Elapsed.TotalSeconds:F1}s " +
            $"({measured.BytesDownloaded / measured.Elapsed.TotalSeconds:N0} bytes/s), above the {allowed:N0} byte " +
            "allowance. The download rate limit is not being enforced on this path.");
    }

    private readonly record struct TransferMeasurement(long BytesDownloaded, TimeSpan Elapsed);

    private async Task<TransferMeasurement> MeasureAsync(
        Encryption encryption,
        Action<Settings>? configureSettings,
        Action<ITorrent>? configureTorrent = null)
    {
        const string fileName = "payload.bin";
        byte[] payload = new byte[PayloadBytes];
        Random.Shared.NextBytes(payload);

        await File.WriteAllBytesAsync(Path.Combine(_seedPath, fileName), payload, TestContext.Current.CancellationToken);

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(256 * 1024)
            .AddFile(fileName, payload)
            .Build();

        await using var seedEngine = await CreateEngineAsync(_seedPath, encryption, configure: null);
        var seedTorrent = await seedEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });

        int validPieces = await seedTorrent.ForceRecheckAsync();
        Assert.Equal(torrentFile.PieceCount, validPieces);
        await seedTorrent.StartAsync();

        await using var leechEngine = await CreateEngineAsync(_leechPath, encryption, configureSettings);
        var leechTorrent = await leechEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = true });
        configureTorrent?.Invoke(leechTorrent);

        await EnsureConnectedAsync(leechEngine, leechTorrent, seedEngine, ConnectionTimeout);

        // Time from first byte, so connection setup does not inflate the allowance.
        var clock = Stopwatch.StartNew();
        while (leechTorrent.FinishedBytes == 0 && clock.Elapsed < ConnectionTimeout)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        clock.Restart();
        await Task.Delay(MeasureWindow, TestContext.Current.CancellationToken);
        var elapsed = clock.Elapsed;
        long downloaded = (long)leechTorrent.FinishedBytes;

        await leechTorrent.StopAsync();
        await seedTorrent.StopAsync();

        return new TransferMeasurement(downloaded, elapsed);
    }

    private async Task<ClientEngine> CreateEngineAsync(string downloadPath, Encryption encryption, Action<Settings>? configure)
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
                Encryption = encryption
            },
            Dht = { Enabled = false }
        };

        configure?.Invoke(settings);

        var engine = ClientEngine.Create(new TorrentClientOptions
        {
            LoggerFactory = _loggerFactory,
            Settings = settings
        });

        await engine.InitializeAsync();
        return engine;
    }

    private static async Task EnsureConnectedAsync(ClientEngine leechEngine, ITorrent leechTorrent, ClientEngine seedEngine, TimeSpan timeout)
    {
        var portListener = seedEngine.PortListener ?? throw new InvalidOperationException("Seed engine has no port listener.");
        var seedEndpoint = new IPEndPoint(IPAddress.Loopback, portListener.Port);

        var clock = Stopwatch.StartNew();
        while (leechTorrent.Peers.ConnectedCount == 0 && clock.Elapsed < timeout)
        {
            leechEngine.OnPeersFound(leechTorrent.Hash, [seedEndpoint]);
            await Task.Delay(200);
        }

        Assert.True(leechTorrent.Peers.ConnectedCount > 0, $"Timed out after {timeout} waiting for a peer connection.");
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
