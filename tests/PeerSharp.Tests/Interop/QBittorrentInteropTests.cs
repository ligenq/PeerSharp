using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Interop;

/// <summary>
/// The same experiment as <see cref="TransmissionInteropTests"/>, against qBittorrent.
///
/// <para>
/// One implementation cannot tell a fault of ours from a quirk of theirs. qBittorrent is built on
/// libtorrent, a wholly separate lineage from Transmission's own peer code, so running both
/// directions against both clients turns a suspicious number into an attributable one: a result
/// that reproduces across both is ours, one that does not is theirs.
/// </para>
///
/// <para>
/// Gated on <c>PEERSHARP_QBITTORRENT_INTEROP=1</c> and excluded from CI with the rest of this
/// namespace. qBittorrent runs on a throwaway <c>--profile</c>, so the operator's own configuration
/// is untouched, and DHT, PeX and LSD are off on both sides - the connection we make is the only
/// way the two can find each other.
/// </para>
/// </summary>
public sealed class QBittorrentInteropTests : IAsyncLifetime
{
    private const string EnableVariable = "PEERSHARP_QBITTORRENT_INTEROP";
    private const string ExeVariable = "PEERSHARP_QBITTORRENT_EXE";
    private const string DefaultExe = @"C:\Program Files\qBittorrent\qbittorrent.exe";

    private const int QBittorrentPeerPort = 52999;

    private readonly ITestOutputHelper _output;
    private readonly string _root;

    private Process? _qbittorrent;
    private string _peerSeen = "(never sampled - transfer finished between polls)";

    public QBittorrentInteropTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), "peersharp-qbittorrent-" + Guid.NewGuid().ToString("N")[..8]);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_qbittorrent is { HasExited: false })
        {
            try
            {
                _qbittorrent.Kill(entireProcessTree: true);
                await _qbittorrent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Failed to stop qBittorrent: {ex.Message}");
            }
        }

        _qbittorrent?.Dispose();

        if (Environment.GetEnvironmentVariable("PEERSHARP_QBITTORRENT_KEEP") == "1")
        {
            _output.WriteLine($"Kept working directory: {_root}");
            return;
        }

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact(Timeout = 900_000)]
    public async Task Seeding_ToQBittorrent_DeliversTheWholeFile()
    {
        var exe = RequireQBittorrent();
        int sizeMiB = SizeFromEnvironment();
        var encryption = EncryptionFromEnvironment();

        var seedDir = Path.Combine(_root, "seed");
        var downloadDir = Path.Combine(_root, "download");
        Directory.CreateDirectory(seedDir);
        Directory.CreateDirectory(downloadDir);

        const string fileName = "qbt-payload.bin";
        var (payload, torrentFile, torrentPath) = await CreatePayloadAsync(seedDir, fileName, sizeMiB);
        var expectedHash = Convert.ToHexString(SHA256.HashData(payload));

        _output.WriteLine($"Payload    : {sizeMiB} MiB, {torrentFile.PieceCount} pieces");
        _output.WriteLine("Direction  : PeerSharp seeds, qBittorrent leeches (PeerSharp dials)");
        _output.WriteLine($"Encryption : PeerSharp={encryption}, qBittorrent=prefer");

        await using var engine = await CreateEngineAsync(seedDir, encryption);
        var seedTorrent = await engine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });
        Assert.Equal(torrentFile.PieceCount, await seedTorrent.ForceRecheckAsync());
        await seedTorrent.StartAsync();

        await StartQBittorrentAsync(exe, downloadDir, torrentPath);

        var destination = Path.Combine(downloadDir, fileName);
        Assert.False(File.Exists(destination), $"Destination {destination} already exists before the transfer.");

        var elapsed = await DriveAsync(
            engine,
            torrentFile.InfoHash,
            // Completion is read from the file itself: a hash match is the only signal that does not
            // depend on qBittorrent telling us anything.
            async () => await FileMatchesAsync(destination, payload.Length, expectedHash),
            () =>
            {
                var f = new FileInfo(destination);
                return f.Exists ? $"{f.Length / 1024d / 1024d:F1} MiB on disk" : "no file yet";
            },
            "qBittorrent");

        ReportPeerSharpView(seedTorrent);
        Assert.True(elapsed.HasValue, $"qBittorrent never completed the file at {destination}.");
        Report("SEEDING TO qBITTORRENT", payload.Length, elapsed!.Value);
    }

    [Fact(Timeout = 900_000)]
    public async Task Leeching_FromQBittorrent_ReceivesTheWholeFile()
    {
        var exe = RequireQBittorrent();
        int sizeMiB = SizeFromEnvironment();
        var encryption = EncryptionFromEnvironment();

        var qbtDir = Path.Combine(_root, "qbt-seed");
        var leechDir = Path.Combine(_root, "leech");
        Directory.CreateDirectory(qbtDir);
        Directory.CreateDirectory(leechDir);

        const string fileName = "qbt-reverse-payload.bin";
        var (payload, torrentFile, torrentPath) = await CreatePayloadAsync(qbtDir, fileName, sizeMiB);
        var expectedHash = Convert.ToHexString(SHA256.HashData(payload));

        _output.WriteLine($"Payload    : {sizeMiB} MiB, {torrentFile.PieceCount} pieces");
        _output.WriteLine("Direction  : qBittorrent seeds, PeerSharp leeches (PeerSharp dials)");
        _output.WriteLine($"Encryption : PeerSharp={encryption}, qBittorrent=prefer");

        // The payload is already in qBittorrent's save path, so adding the torrent makes it check
        // the data and come up as a seed rather than as a second leecher.
        await StartQBittorrentAsync(exe, qbtDir, torrentPath);

        await using var engine = await CreateEngineAsync(leechDir, encryption);
        var leechTorrent = await engine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = true });

        var elapsed = await DriveAsync(
            engine,
            torrentFile.InfoHash,
            () => Task.FromResult(leechTorrent.Progress >= 1.0f),
            () => $"{leechTorrent.Progress:P1} peers={leechTorrent.Peers.ConnectedCount}",
            "PeerSharp");

        ReportPeerSharpView(leechTorrent);
        Assert.True(elapsed.HasValue, $"PeerSharp stalled at {leechTorrent.Progress:P1}.");

        var received = Path.Combine(leechDir, fileName);
        Assert.True(File.Exists(received), $"Reported complete but {received} is missing.");
        Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(await ReadAllBytesSharedAsync(received))));

        Report("LEECHING FROM qBITTORRENT", payload.Length, elapsed!.Value);
    }

    /// <summary>
    /// Keeps offering qBittorrent's endpoint until <paramref name="isComplete"/> says the transfer
    /// finished, and times the part that actually moved data rather than the wait before it.
    /// </summary>
    private async Task<TimeSpan?> DriveAsync(
        ClientEngine engine,
        InfoHash infoHash,
        Func<Task<bool>> isComplete,
        Func<string> describe,
        string watching)
    {
        var endpoint = new IPEndPoint(IPAddress.Loopback, QBittorrentPeerPort);
        var overall = Stopwatch.StartNew();
        var timeout = TimeSpan.FromMinutes(IntFromEnvironment("PEERSHARP_QBITTORRENT_TIMEOUT_MINUTES", 10));
        var lastLog = TimeSpan.Zero;
        TimeSpan? connectedAt = null;

        while (overall.Elapsed < timeout)
        {
            engine.OnPeersFound(infoHash, [endpoint]);

            // Sampled inside the loop: on loopback the transfer can finish between two polls, and
            // by the time it has, the peer is gone and there is nothing left to report.
            if (connectedAt == null)
            {
                var peer = engine.GetTorrents()
                    .SelectMany(t => t.Peers.GetConnectedPeers())
                    .FirstOrDefault();
                if (peer != null)
                {
                    connectedAt = overall.Elapsed;
                    _peerSeen = $"{peer.EndPoint} client='{peer.ClientName}' encrypted={peer.IsEncrypted} utp={peer.IsUtp}";
                    _output.WriteLine($"  [{overall.Elapsed.TotalSeconds,6:F1}s] connected: {_peerSeen}");
                }
            }

            if (await isComplete())
            {
                _output.WriteLine($"  [{overall.Elapsed.TotalSeconds,6:F1}s] complete ({describe()})");
                return connectedAt is { } start ? overall.Elapsed - start : overall.Elapsed;
            }

            if (overall.Elapsed - lastLog > TimeSpan.FromSeconds(5))
            {
                lastLog = overall.Elapsed;
                _output.WriteLine($"  [{overall.Elapsed.TotalSeconds,5:F0}s] {watching}: {describe()}");
            }

            await Task.Delay(500);
        }

        return null;
    }

    private static async Task<bool> FileMatchesAsync(string path, long expectedLength, string expectedHash)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != expectedLength)
        {
            return false;
        }

        // Length alone proves nothing: qBittorrent preallocates, so the file reaches full size long
        // before it holds the right bytes.
        try
        {
            return Convert.ToHexString(SHA256.HashData(await ReadAllBytesSharedAsync(path))) == expectedHash;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void Report(string label, long bytes, TimeSpan elapsed)
    {
        double mib = bytes / 1024d / 1024d;
        _output.WriteLine(
            $"{label}: {mib:F0} MiB in {elapsed.TotalSeconds:F1}s " +
            $"({mib / elapsed.TotalSeconds:F1} MiB/s), content verified.");
    }

    private void ReportPeerSharpView(ITorrent torrent)
    {
        _output.WriteLine($"Peer PeerSharp talked to: {_peerSeen}");
        foreach (var peer in torrent.Peers.GetConnectedPeers())
        {
            _output.WriteLine(
                $"  still connected: {peer.EndPoint} uploaded={peer.Uploaded} downloaded={peer.Downloaded}");
        }
    }

    private async Task<(byte[] Payload, TorrentFile Torrent, string TorrentPath)> CreatePayloadAsync(
        string directory, string fileName, int sizeMiB)
    {
        var payload = new byte[sizeMiB * 1024 * 1024];
        RandomNumberGenerator.Fill(payload);
        var file = Path.Combine(directory, fileName);
        await File.WriteAllBytesAsync(file, payload);

        var torrent = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(256 * 1024)
            .AddFileFromPath(file, fileName)
            .Build();

        var torrentPath = Path.Combine(_root, fileName + ".torrent");
        await File.WriteAllBytesAsync(torrentPath, torrent.RawData.ToArray());
        return (payload, torrent, torrentPath);
    }

    private static async Task<ClientEngine> CreateEngineAsync(string downloadPath, Encryption encryption)
    {
        var engine = ClientEngine.Create(new TorrentClientOptions
        {
            LoggerFactory = NullLoggerFactory.Instance,
            Settings = new Settings
            {
                Files = { DefaultDownloadPath = downloadPath },
                Connection =
                {
                    TcpPort = 0,
                    UdpPort = 0,
                    EnableLsd = false,
                    UpnpPortMapping = false,
                    NatPmpPortMapping = false,
                    Encryption = encryption
                },
                Dht = { Enabled = false },
                Session = { Enabled = false }
            }
        });

        await engine.InitializeAsync();
        return engine;
    }

    /// <summary>
    /// Starts qBittorrent on a throwaway profile with the torrent handed to it as an argument.
    ///
    /// <para>
    /// Deliberately no WebUI. Enabling it from a pre-written config does not take in the GUI build -
    /// the peer port binds but the interface never starts - and it is not needed here: the torrent
    /// can be added on the command line, and everything worth measuring is visible from our own side
    /// or from the file qBittorrent writes.
    /// </para>
    /// </summary>
    private async Task StartQBittorrentAsync(string exe, string savePath, string torrentPath)
    {
        var profileDir = Path.Combine(_root, "profile");
        var configDir = Path.Combine(profileDir, "qBittorrent", "config");
        Directory.CreateDirectory(configDir);

        var ini = string.Join('\n',
            "[LegalNotice]",
            "Accepted=true",
            "",
            // Modern qBittorrent keys are top-level QSettings paths, so each one is its own INI
            // section - a [Preferences] section is silently ignored and the dialog still appears.
            "[AddNewTorrentDialog]",
            "Enabled=false",
            "",
            "[BitTorrent]",
            $@"Session\Port={QBittorrentPeerPort}",
            @"Session\UseRandomPort=false",
            @"Session\DHTEnabled=false",
            @"Session\LSDEnabled=false",
            @"Session\PeXEnabled=false",
            @"Session\Encryption=0",
            @"Session\QueueingSystemEnabled=false",
            @"Session\GlobalUPSpeedLimit=0",
            @"Session\GlobalDLSpeedLimit=0",
            $@"Session\DefaultSavePath={savePath.Replace('\\', '/')}",
            "");

        await File.WriteAllTextAsync(Path.Combine(configDir, "qBittorrent.ini"), ini);

        var info = new ProcessStartInfo(exe) { UseShellExecute = false };
        info.ArgumentList.Add($"--profile={profileDir}");
        info.ArgumentList.Add("--confirm-legal-notice");
        info.ArgumentList.Add("--no-splash");
        info.ArgumentList.Add(torrentPath);
        _qbittorrent = Process.Start(info) ?? throw new InvalidOperationException("Failed to start qBittorrent.");

        // qBittorrent is single-instance: a stale one would swallow this launch and run on its own
        // profile, leaving the run measuring a client it did not configure.
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (_qbittorrent.HasExited)
        {
            throw new InvalidOperationException(
                "qBittorrent exited immediately, which means another instance was already running and " +
                "took over the launch. Close it and run again.");
        }

        _output.WriteLine($"qBittorrent started, peer port {QBittorrentPeerPort}, save path {savePath}");
    }

    private static async Task<byte[]> ReadAllBytesSharedAsync(string path)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static string RequireQBittorrent()
    {
        if (Environment.GetEnvironmentVariable(EnableVariable) != "1")
        {
            Assert.Skip($"Set {EnableVariable}=1 to run the qBittorrent interop tests.");
        }

        string exe = Environment.GetEnvironmentVariable(ExeVariable) ?? DefaultExe;
        if (!File.Exists(exe))
        {
            Assert.Skip($"qBittorrent not found at {exe}. Set {ExeVariable} to its path.");
        }

        return exe;
    }

    private static Encryption EncryptionFromEnvironment()
    {
        return Enum.TryParse<Encryption>(
            Environment.GetEnvironmentVariable("PEERSHARP_QBITTORRENT_ENCRYPTION"), out var parsed)
            ? parsed
            : Encryption.Allow;
    }

    private static int SizeFromEnvironment() => IntFromEnvironment("PEERSHARP_QBITTORRENT_SIZE_MIB", 64);

    private static int IntFromEnvironment(string name, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
    }
}
