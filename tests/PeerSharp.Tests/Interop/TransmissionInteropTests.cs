using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Interop;

/// <summary>
/// Serves a torrent to a real Transmission and reports how it went.
///
/// <para>
/// This is the experiment the public-swarm soak cannot run. A mature distribution swarm is almost
/// entirely seeds, so nobody asks us for data and "we uploaded nothing" has two explanations that
/// look identical from the outside: a swarm with no demand, and a client the swarm refuses to take
/// data from. Driving one known leecher removes the ambiguity - Transmission wants the whole file
/// and we are the only peer that has it, so either it completes or the reason it did not is ours.
/// </para>
///
/// <para>
/// Opt-in twice over, like the rest of this namespace: <c>PeerSharp.Tests.Interop</c> is excluded
/// from every CI job, and the test additionally requires <c>PEERSHARP_TRANSMISSION_INTEROP=1</c>
/// because it launches a real Transmission process and moves real bytes.
/// </para>
///
/// <para>
/// Nothing here touches the operator's own Transmission configuration. The process is started with
/// <c>TRANSMISSION_HOME</c> pointed at a temporary directory, and DHT, PEX, LSD and port forwarding
/// are all off on both sides, so the generated torrent - random bytes, no trackers - cannot reach
/// anything outside this machine.
/// </para>
/// </summary>
public sealed class TransmissionInteropTests : IAsyncLifetime
{
    private const string EnableVariable = "PEERSHARP_TRANSMISSION_INTEROP";
    private const string ExeVariable = "PEERSHARP_TRANSMISSION_EXE";
    private const string DefaultExe = @"C:\Program Files\Transmission\transmission-qt.exe";

    private const int RpcPort = 9099;
    private const int TransmissionPeerPort = 51999;

    private readonly ITestOutputHelper _output;
    private readonly string _root;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private Process? _transmission;
    private string? _sessionId;

    public TransmissionInteropTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), "peersharp-transmission-" + Guid.NewGuid().ToString("N")[..8]);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_transmission is { HasExited: false })
        {
            try
            {
                _transmission.Kill(entireProcessTree: true);
                await _transmission.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Failed to stop Transmission: {ex.Message}");
            }
        }

        _transmission?.Dispose();
        _http.Dispose();

        if (Environment.GetEnvironmentVariable("PEERSHARP_TRANSMISSION_KEEP") == "1")
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
            // A file the process still holds is not worth failing the run over.
        }
    }

    /// <summary>
    /// The measurement: how long a real Transmission takes to take a complete file from us, and
    /// what it thought of us while doing it.
    /// </summary>
    [Fact(Timeout = 900_000)]
    public async Task Seeding_ToTransmission_DeliversTheWholeFile()
    {
        RequireEnabled();
        string exe = Environment.GetEnvironmentVariable(ExeVariable) ?? DefaultExe;
        if (!File.Exists(exe))
        {
            Assert.Skip($"Transmission not found at {exe}. Set {ExeVariable} to its path.");
        }

        int sizeMiB = IntFromEnvironment("PEERSHARP_TRANSMISSION_SIZE_MIB", 64);
        var encryption = Enum.TryParse<Encryption>(
            Environment.GetEnvironmentVariable("PEERSHARP_TRANSMISSION_ENCRYPTION"), out var parsed)
            ? parsed
            : Encryption.Allow;

        var configDir = Path.Combine(_root, "config");
        var seedDir = Path.Combine(_root, "seed");
        var downloadDir = Path.Combine(_root, "download");
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(seedDir);
        Directory.CreateDirectory(downloadDir);

        // Random bytes: incompressible, and an info hash nothing else on earth is sharing.
        const string fileName = "interop-payload.bin";
        var payload = new byte[sizeMiB * 1024 * 1024];
        RandomNumberGenerator.Fill(payload);
        var seedFile = Path.Combine(seedDir, fileName);
        await File.WriteAllBytesAsync(seedFile, payload);
        var expectedHash = Convert.ToHexString(SHA256.HashData(payload));

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(256 * 1024)
            .AddFileFromPath(seedFile, fileName)
            .Build();

        var torrentPath = Path.Combine(_root, "interop.torrent");
        await File.WriteAllBytesAsync(torrentPath, torrentFile.RawData.ToArray());

        _output.WriteLine($"Payload    : {sizeMiB} MiB, {torrentFile.PieceCount} pieces of {torrentFile.PieceSize} B");
        _output.WriteLine($"Info hash  : {torrentFile.InfoHash}");
        _output.WriteLine($"Encryption : PeerSharp={encryption}, Transmission=preferred");
        ReportNetworkInterfaces();

        await StartTransmissionAsync(exe, configDir, downloadDir);
        var session = await RpcAsync("session-get");
        _output.WriteLine($"Transmission: {session.GetProperty("arguments").GetProperty("version").GetString()}");

        await using var engine = await CreateSeedEngineAsync(seedDir, encryption);
        var seedTorrent = await engine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });
        int valid = await seedTorrent.ForceRecheckAsync();
        Assert.Equal(torrentFile.PieceCount, valid);
        await seedTorrent.StartAsync();

        var listener = engine.PortListener ?? throw new InvalidOperationException("Seed engine has no port listener.");
        _output.WriteLine($"PeerSharp listening on {listener.Port}, Transmission on {TransmissionPeerPort}");

        await AddTorrentToTransmissionAsync(torrentPath, downloadDir);

        var transmissionEndpoint = new IPEndPoint(IPAddress.Loopback, TransmissionPeerPort);
        var result = await DriveTransferAsync(engine, seedTorrent, torrentFile.InfoHash, transmissionEndpoint);

        ReportPeerSharpView(seedTorrent);

        Assert.True(
            result.PercentDone >= 1.0,
            $"Transmission stopped at {result.PercentDone:P1} after {result.Elapsed.TotalSeconds:F0}s. " +
            $"Last error: '{result.ErrorString}'. Peers connected to us: {result.PeersConnected}.");

        var downloaded = Path.Combine(downloadDir, fileName);
        Assert.True(File.Exists(downloaded), $"Transmission reported complete but {downloaded} is missing.");
        var actualHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(downloaded)));
        Assert.Equal(expectedHash, actualHash);

        double mib = payload.Length / 1024d / 1024d;
        _output.WriteLine(
            $"RESULT: {mib:F0} MiB in {result.Elapsed.TotalSeconds:F1}s " +
            $"({mib / result.Elapsed.TotalSeconds:F1} MiB/s), content verified.");
    }

    /// <summary>
    /// The same connection, carrying data the other way: Transmission seeds and we leech.
    ///
    /// <para>
    /// We still dial Transmission, so the TCP direction and the encryption negotiation are exactly
    /// as they were when we were seeding - only which side sends the pieces changes. If the ceiling
    /// measured while seeding follows the data, it belongs to our send path; if it stays put, it
    /// belongs to Transmission or to the connection between us.
    /// </para>
    /// </summary>
    [Fact(Timeout = 900_000)]
    public async Task Leeching_FromTransmission_ReceivesTheWholeFile()
    {
        RequireEnabled();
        string exe = Environment.GetEnvironmentVariable(ExeVariable) ?? DefaultExe;
        if (!File.Exists(exe))
        {
            Assert.Skip($"Transmission not found at {exe}. Set {ExeVariable} to its path.");
        }

        int sizeMiB = IntFromEnvironment("PEERSHARP_TRANSMISSION_SIZE_MIB", 64);
        var encryption = Enum.TryParse<Encryption>(
            Environment.GetEnvironmentVariable("PEERSHARP_TRANSMISSION_ENCRYPTION"), out var parsed)
            ? parsed
            : Encryption.Allow;

        var configDir = Path.Combine(_root, "config");
        var transmissionDir = Path.Combine(_root, "transmission-seed");
        var leechDir = Path.Combine(_root, "leech");
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(transmissionDir);
        Directory.CreateDirectory(leechDir);

        // The payload goes where Transmission will look for it, so adding the torrent turns it into
        // a seed rather than a second leecher.
        const string fileName = "reverse-payload.bin";
        var payload = new byte[sizeMiB * 1024 * 1024];
        RandomNumberGenerator.Fill(payload);
        await File.WriteAllBytesAsync(Path.Combine(transmissionDir, fileName), payload);
        var expectedHash = Convert.ToHexString(SHA256.HashData(payload));

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(256 * 1024)
            .AddFileFromPath(Path.Combine(transmissionDir, fileName), fileName)
            .Build();

        var torrentPath = Path.Combine(_root, "reverse.torrent");
        await File.WriteAllBytesAsync(torrentPath, torrentFile.RawData.ToArray());

        _output.WriteLine($"Payload    : {sizeMiB} MiB, {torrentFile.PieceCount} pieces of {torrentFile.PieceSize} B");
        _output.WriteLine($"Direction  : Transmission seeds, PeerSharp leeches (PeerSharp still dials)");
        _output.WriteLine($"Encryption : PeerSharp={encryption}, Transmission=preferred");
        ReportNetworkInterfaces();

        await StartTransmissionAsync(exe, configDir, transmissionDir);
        await AddTorrentToTransmissionAsync(torrentPath, transmissionDir);
        await WaitForTransmissionToSeedAsync();

        // Opt-in: the log is large and only wanted when investigating the stalls.
        using var logProvider = Environment.GetEnvironmentVariable("PEERSHARP_TRANSMISSION_LOG") == "1"
            ? new TimestampedFileLoggerProvider(Path.Combine(_root, "peersharp.log"))
            : null;
        using var loggerFactory = logProvider is null
            ? null
            : LoggerFactory.Create(builder =>
            {
                builder.AddProvider(logProvider);
                builder.SetMinimumLevel(LogLevel.Trace);
            });

        if (loggerFactory is not null)
        {
            _output.WriteLine($"Engine log : {Path.Combine(_root, "peersharp.log")}");
        }

        await using var engine = await CreateSeedEngineAsync(leechDir, encryption, loggerFactory);
        var leechTorrent = await engine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = true });

        var transmissionEndpoint = new IPEndPoint(IPAddress.Loopback, TransmissionPeerPort);
        var overall = Stopwatch.StartNew();
        var timeout = TimeSpan.FromMinutes(IntFromEnvironment("PEERSHARP_TRANSMISSION_TIMEOUT_MINUTES", 10));
        Stopwatch? transferring = null;
        var lastLog = TimeSpan.Zero;

        while (overall.Elapsed < timeout && leechTorrent.Progress < 1.0f)
        {
            engine.OnPeersFound(torrentFile.InfoHash, [transmissionEndpoint]);

            if (transferring == null && leechTorrent.Progress > 0)
            {
                transferring = Stopwatch.StartNew();
                _output.WriteLine($"  [{overall.Elapsed.TotalSeconds,6:F1}s] first data received");
            }

            if (overall.Elapsed - lastLog > TimeSpan.FromSeconds(2))
            {
                lastLog = overall.Elapsed;
                _output.WriteLine(
                    $"  [{overall.Elapsed.TotalSeconds,5:F1}s] {leechTorrent.Progress,7:P1} " +
                    $"peers={leechTorrent.Peers.ConnectedCount} " +
                    $"transmissionUploadedEver={await TransmissionUploadedEverAsync()}");
            }

            await Task.Delay(250);
        }

        ReportPeerSharpView(leechTorrent);

        Assert.True(
            leechTorrent.Progress >= 1.0f,
            $"PeerSharp stalled at {leechTorrent.Progress:P1} after {overall.Elapsed.TotalSeconds:F0}s.");

        var received = Path.Combine(leechDir, fileName);
        Assert.True(File.Exists(received), $"Reported complete but {received} is missing.");
        Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(await ReadAllBytesSharedAsync(received))));

        double mib = payload.Length / 1024d / 1024d;
        var elapsed = transferring?.Elapsed ?? overall.Elapsed;
        _output.WriteLine(
            $"REVERSE: {mib:F0} MiB from Transmission in {elapsed.TotalSeconds:F1}s " +
            $"({mib / elapsed.TotalSeconds:F1} MiB/s), content verified.");
    }

    /// <summary>
    /// The same leech, run by MonoTorrent instead of us.
    ///
    /// <para>
    /// Every other measurement of the MSE desynchronisation has been PeerSharp-internal, which can
    /// establish that our side is self-consistent but not that it is correct. MonoTorrent is an
    /// independent, mature .NET MSE implementation - and, judging by the shared <c>Skip(1024)</c>,
    /// the identical RC4 inner loop and the near-identical "Invalid message length" wording, the one
    /// ours descends from. Pointing it at the same Transmission seeder splits the last hypothesis:
    /// if it also desynchronises the fault is Transmission's and we are exonerated, and if it does
    /// not the fault is definitively ours and the diff between the two receive paths is short.
    /// </para>
    ///
    /// <para>
    /// The leecher is an external process so that this repository carries no dependency on a
    /// research checkout. Point <c>PEERSHARP_MONOTORRENT_LEECHER</c> at the built executable; the
    /// test skips without it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Leeching_FromTransmission_WithMonoTorrent_ControlArm()
    {
        RequireEnabled();
        string exe = Environment.GetEnvironmentVariable(ExeVariable) ?? DefaultExe;
        if (!File.Exists(exe))
        {
            Assert.Skip($"Transmission not found at {exe}. Set {ExeVariable} to its path.");
        }

        string? leecher = Environment.GetEnvironmentVariable("PEERSHARP_MONOTORRENT_LEECHER");
        if (string.IsNullOrWhiteSpace(leecher) || !File.Exists(leecher))
        {
            Assert.Skip("Set PEERSHARP_MONOTORRENT_LEECHER to the built MonoTorrent leecher executable.");
        }

        int sizeMiB = IntFromEnvironment("PEERSHARP_TRANSMISSION_SIZE_MIB", 64);
        int timeoutMinutes = IntFromEnvironment("PEERSHARP_TRANSMISSION_TIMEOUT_MINUTES", 10);

        var configDir = Path.Combine(_root, "config");
        var transmissionDir = Path.Combine(_root, "transmission-seed");
        var leechDir = Path.Combine(_root, "monotorrent-leech");
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(transmissionDir);
        Directory.CreateDirectory(leechDir);

        // Byte-for-byte the same setup as the PeerSharp arm, so the only variable is the leecher.
        const string fileName = "reverse-payload.bin";
        var payload = new byte[sizeMiB * 1024 * 1024];
        RandomNumberGenerator.Fill(payload);
        await File.WriteAllBytesAsync(Path.Combine(transmissionDir, fileName), payload);
        var expectedHash = Convert.ToHexString(SHA256.HashData(payload));

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(256 * 1024)
            .AddFileFromPath(Path.Combine(transmissionDir, fileName), fileName)
            .Build();

        var torrentPath = Path.Combine(_root, "reverse.torrent");
        await File.WriteAllBytesAsync(torrentPath, torrentFile.RawData.ToArray());

        _output.WriteLine($"Payload    : {sizeMiB} MiB, {torrentFile.PieceCount} pieces of {torrentFile.PieceSize} B");
        _output.WriteLine("Direction  : Transmission seeds, MonoTorrent leeches (control arm)");
        _output.WriteLine("Encryption : MonoTorrent=RC4Full only, Transmission=preferred");
        _output.WriteLine($"Leecher    : {leecher}");
        ReportNetworkInterfaces();

        await StartTransmissionAsync(exe, configDir, transmissionDir);
        await AddTorrentToTransmissionAsync(torrentPath, transmissionDir);
        await WaitForTransmissionToSeedAsync();

        var start = new ProcessStartInfo(leecher!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _root
        };
        start.ArgumentList.Add(torrentPath);
        start.ArgumentList.Add(leechDir);
        start.ArgumentList.Add($"127.0.0.1:{TransmissionPeerPort}");
        start.ArgumentList.Add((timeoutMinutes * 60).ToString());

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the MonoTorrent leecher.");

        var stdout = new List<string>();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (stdout)
            {
                stdout.Add(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stdout)
                {
                    stdout.Add("ERR: " + e.Data);
                }
            }
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var clock = Stopwatch.StartNew();
        await process.WaitForExitAsync(new CancellationTokenSource(
            TimeSpan.FromMinutes(timeoutMinutes + 2)).Token);
        clock.Stop();

        lock (stdout)
        {
            foreach (var line in stdout)
            {
                _output.WriteLine(line);
            }
        }

        _output.WriteLine($"Transmission uploadedEver: {await TransmissionUploadedEverAsync()}");

        Assert.True(
            process.ExitCode == 0,
            $"MonoTorrent did not complete: exit {process.ExitCode} after {clock.Elapsed.TotalSeconds:F0}s.");

        var received = Path.Combine(leechDir, fileName);
        Assert.True(File.Exists(received), $"Reported complete but {received} is missing.");
        Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(await ReadAllBytesSharedAsync(received))));
        _output.WriteLine("Content verified.");
    }

    /// <summary>
    /// Waits until Transmission has verified the payload and is seeding it. Adding a torrent whose
    /// files are already present makes it check them first, and it has nothing to serve until that
    /// finishes.
    /// </summary>
    /// <summary>
    /// What the seeder believes it has sent us. Compared with our own decrypted total it says
    /// whether bytes went missing between the two, which is the other half of the byte audit.
    /// </summary>
    private async Task<string> TransmissionUploadedEverAsync()
    {
        try
        {
            var torrents = (await RpcAsync("torrent-get", new { fields = new[] { "uploadedEver" } }))
                .GetProperty("arguments").GetProperty("torrents");
            return torrents.GetArrayLength() > 0
                ? torrents[0].GetProperty("uploadedEver").GetInt64().ToString()
                : "n/a";
        }
        catch (Exception)
        {
            return "n/a";
        }
    }

    private async Task WaitForTransmissionToSeedAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            var torrents = (await RpcAsync("torrent-get", new { fields = new[] { "percentDone", "status" } }))
                .GetProperty("arguments").GetProperty("torrents");

            if (torrents.GetArrayLength() > 0)
            {
                var t = torrents[0];
                // 6 is TR_STATUS_SEED; anything lower is still stopped, queued or verifying.
                if (t.GetProperty("percentDone").GetDouble() >= 1.0 && t.GetProperty("status").GetInt32() == 6)
                {
                    _output.WriteLine("Transmission is seeding the payload");
                    return;
                }
            }

            await Task.Delay(500);
        }

        throw new InvalidOperationException("Transmission did not finish verifying and start seeding.");
    }

    /// <summary>
    /// The control for the run above: the same payload between two PeerSharp engines on the same
    /// loopback. Comparing the two rates says whether a throughput ceiling belongs to our send
    /// path or to something about the Transmission connection, which is otherwise unattributable.
    /// </summary>
    [Fact(Timeout = 900_000)]
    public async Task Seeding_ToPeerSharp_ControlArm()
    {
        RequireEnabled();

        int sizeMiB = IntFromEnvironment("PEERSHARP_TRANSMISSION_SIZE_MIB", 64);
        var seedDir = Path.Combine(_root, "seed");
        var leechDir = Path.Combine(_root, "leech");
        Directory.CreateDirectory(seedDir);
        Directory.CreateDirectory(leechDir);

        const string fileName = "control-payload.bin";
        var payload = new byte[sizeMiB * 1024 * 1024];
        RandomNumberGenerator.Fill(payload);
        var seedFile = Path.Combine(seedDir, fileName);
        await File.WriteAllBytesAsync(seedFile, payload);

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(256 * 1024)
            .AddFileFromPath(seedFile, fileName)
            .Build();

        // The arm that matters: a plaintext control and an encrypted one, over the same loopback.
        // Real peers almost always negotiate MSE and local tests almost never do, so a cost that
        // lives only in the encrypted path is invisible everywhere except here.
        var controlEncryption = Enum.TryParse<Encryption>(
            Environment.GetEnvironmentVariable("PEERSHARP_TRANSMISSION_ENCRYPTION"), out var mode)
            ? mode
            : Encryption.Refuse;
        _output.WriteLine($"Control encryption: {controlEncryption}");

        await using var seedEngine = await CreateSeedEngineAsync(seedDir, controlEncryption);
        var seedTorrent = await seedEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });
        Assert.Equal(torrentFile.PieceCount, await seedTorrent.ForceRecheckAsync());
        await seedTorrent.StartAsync();

        var listener = seedEngine.PortListener ?? throw new InvalidOperationException("Seed engine has no port listener.");
        var seedEndpoint = new IPEndPoint(IPAddress.Loopback, listener.Port);

        await using var leechEngine = await CreateSeedEngineAsync(leechDir, controlEncryption);
        var leechTorrent = await leechEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = true });

        var sw = Stopwatch.StartNew();
        Stopwatch? transferring = null;
        var timeout = TimeSpan.FromMinutes(IntFromEnvironment("PEERSHARP_TRANSMISSION_TIMEOUT_MINUTES", 10));

        while (sw.Elapsed < timeout && leechTorrent.Progress < 1.0f)
        {
            leechEngine.OnPeersFound(torrentFile.InfoHash, [seedEndpoint]);
            if (transferring == null && leechTorrent.Progress > 0)
            {
                transferring = Stopwatch.StartNew();
            }

            await Task.Delay(250);
        }

        Assert.True(leechTorrent.Progress >= 1.0f, $"Control arm stalled at {leechTorrent.Progress:P1}.");

        var received = Path.Combine(leechDir, fileName);
        Assert.True(File.Exists(received), $"Control arm reported complete but {received} is missing.");
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(payload)),
            Convert.ToHexString(SHA256.HashData(await ReadAllBytesSharedAsync(received))));

        double mib = payload.Length / 1024d / 1024d;
        var elapsed = transferring?.Elapsed ?? sw.Elapsed;
        _output.WriteLine(
            $"CONTROL: {mib:F0} MiB PeerSharp to PeerSharp in {elapsed.TotalSeconds:F1}s " +
            $"({mib / elapsed.TotalSeconds:F1} MiB/s)");
    }

    private async Task<TransferResult> DriveTransferAsync(
        ClientEngine engine,
        ITorrent seedTorrent,
        InfoHash infoHash,
        IPEndPoint transmissionEndpoint)
    {
        var overall = Stopwatch.StartNew();
        var timeout = TimeSpan.FromMinutes(IntFromEnvironment("PEERSHARP_TRANSMISSION_TIMEOUT_MINUTES", 10));
        double percentDone = 0;
        string errorString = string.Empty;
        int peersConnected = 0;
        var lastLog = TimeSpan.Zero;
        Stopwatch? transferring = null;

        // Milestones, because the interesting number is not the transfer rate - loopback makes that
        // meaningless - but how long a peer sits connected before it asks us for anything.
        TimeSpan? connectedAt = null;
        TimeSpan? interestedAt = null;
        TimeSpan? firstByteAt = null;

        // Holding off the introduction discriminates between "Transmission rechokes on a fixed
        // cadence from torrent start" and "Transmission takes N seconds to warm to a new peer".
        var connectDelay = TimeSpan.FromSeconds(IntFromEnvironment("PEERSHARP_TRANSMISSION_CONNECT_DELAY_SECONDS", 0));
        if (connectDelay > TimeSpan.Zero)
        {
            _output.WriteLine($"Holding off the peer introduction for {connectDelay.TotalSeconds:F0}s");
        }

        while (overall.Elapsed < timeout)
        {
            // Transmission has no "add peer" API, so we dial it. Repeated because the announce is
            // the only introduction there is: DHT, PEX and LSD are all off.
            if (overall.Elapsed >= connectDelay)
            {
                engine.OnPeersFound(infoHash, [transmissionEndpoint]);
            }

            var torrents = (await RpcAsync("torrent-get", new
            {
                fields = new[] { "percentDone", "rateDownload", "peersConnected", "errorString", "status", "peers" }
            })).GetProperty("arguments").GetProperty("torrents");

            if (torrents.GetArrayLength() > 0)
            {
                var t = torrents[0];
                percentDone = t.GetProperty("percentDone").GetDouble();
                peersConnected = t.GetProperty("peersConnected").GetInt32();
                errorString = t.GetProperty("errorString").GetString() ?? string.Empty;

                if (connectedAt == null && peersConnected > 0)
                {
                    connectedAt = overall.Elapsed;
                    _output.WriteLine($"  [{overall.Elapsed.TotalSeconds,6:F1}s] Transmission connected to us");
                }

                if (interestedAt == null && PeerIsInterested(t))
                {
                    interestedAt = overall.Elapsed;
                    _output.WriteLine($"  [{overall.Elapsed.TotalSeconds,6:F1}s] Transmission became interested");
                }

                if (transferring == null && percentDone > 0)
                {
                    transferring = Stopwatch.StartNew();
                    firstByteAt = overall.Elapsed;
                    _output.WriteLine($"  [{overall.Elapsed.TotalSeconds,6:F1}s] first data received");
                }

                if (overall.Elapsed - lastLog > TimeSpan.FromSeconds(5))
                {
                    lastLog = overall.Elapsed;
                    double rate = t.GetProperty("rateDownload").GetInt64() / 1024d / 1024d;
                    _output.WriteLine(
                        $"  [{overall.Elapsed.TotalSeconds,5:F0}s] {percentDone,7:P1} " +
                        $"{rate,6:F1} MiB/s  peers={peersConnected}  {DescribePeers(t)}");
                }

                if (percentDone >= 1.0)
                {
                    break;
                }
            }

            await Task.Delay(500);
        }

        _output.WriteLine(
            $"Milestones : connected +{connectedAt?.TotalSeconds ?? -1:F1}s, " +
            $"interested +{interestedAt?.TotalSeconds ?? -1:F1}s, " +
            $"first byte +{firstByteAt?.TotalSeconds ?? -1:F1}s");

        return new TransferResult(percentDone, transferring?.Elapsed ?? overall.Elapsed, errorString, peersConnected);
    }

    /// <summary>
    /// Whether Transmission has told us it wants data. In its RPC the peer flags are named from the
    /// reporting side, so "clientIsInterested" is Transmission-the-client being interested in us.
    /// </summary>
    private static bool PeerIsInterested(JsonElement torrent)
    {
        return torrent.TryGetProperty("peers", out var peers)
            && peers.EnumerateArray().Any(p => p.TryGetProperty("clientIsInterested", out var i) && i.GetBoolean());
    }

    private static string DescribePeers(JsonElement torrent)
    {
        if (!torrent.TryGetProperty("peers", out var peers) || peers.GetArrayLength() == 0)
        {
            return "(no peers)";
        }

        var described = peers.EnumerateArray().Select(p =>
        {
            string client = p.TryGetProperty("clientName", out var c) ? c.GetString() ?? "?" : "?";
            bool interested = p.TryGetProperty("clientIsInterested", out var i) && i.GetBoolean();
            bool choked = p.TryGetProperty("peerIsChoking", out var ch) && ch.GetBoolean();
            bool encrypted = p.TryGetProperty("isEncrypted", out var e) && e.GetBoolean();
            return $"{client}{(encrypted ? " [enc]" : " [clear]")}{(choked ? " choking-us" : "")}{(interested ? " interested" : "")}";
        });

        return string.Join(", ", described);
    }

    /// <summary>
    /// Records what the machine's network looked like. Interop rates are environment-sensitive, and
    /// a run whose conditions were not written down cannot be compared with one taken later - a VPN
    /// tunnel that comes up at boot is exactly the kind of thing nobody remembers afterwards.
    /// </summary>
    private void ReportNetworkInterfaces()
    {
        var addresses = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => $"{nic.Name}={a.Address}"));

        _output.WriteLine($"Interfaces : {string.Join(", ", addresses)}");
    }

    private void ReportPeerSharpView(ITorrent seedTorrent)
    {
        var peers = seedTorrent.Peers.GetConnectedPeers();
        _output.WriteLine($"PeerSharp saw {peers.Count} connected peer(s):");
        foreach (var peer in peers)
        {
            _output.WriteLine(
                $"  {peer.EndPoint} client='{peer.ClientName}' uploaded={peer.Uploaded} " +
                $"encrypted={peer.IsEncrypted} utp={peer.IsUtp} peerInterested={peer.PeerInterested} amChoking={peer.AmChoking}");
        }
    }

    private async Task<ClientEngine> CreateSeedEngineAsync(
        string seedDir,
        Encryption encryption,
        ILoggerFactory? loggerFactory = null)
    {
        var settings = new Settings
        {
            Files = { DefaultDownloadPath = seedDir },
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
        };

        var engine = ClientEngine.Create(new TorrentClientOptions
        {
            LoggerFactory = loggerFactory ?? NullLoggerFactory.Instance,
            Settings = settings
        });

        await engine.InitializeAsync();
        return engine;
    }

    private async Task StartTransmissionAsync(string exe, string configDir, string downloadDir)
    {
        var settings = new Dictionary<string, object>
        {
            ["download-dir"] = downloadDir.Replace('\\', '/'),
            ["incomplete-dir-enabled"] = false,
            ["rpc-enabled"] = true,
            ["rpc-bind-address"] = "127.0.0.1",
            ["rpc-port"] = RpcPort,
            ["rpc-authentication-required"] = false,
            ["rpc-whitelist-enabled"] = false,
            ["rpc-host-whitelist-enabled"] = false,
            ["peer-port"] = TransmissionPeerPort,
            ["peer-port-random-on-start"] = false,
            ["port-forwarding-enabled"] = false,
            // Isolated: the only way it can learn about us is the connection we make to it.
            ["dht-enabled"] = false,
            ["pex-enabled"] = false,
            ["lpd-enabled"] = false,
            ["utp-enabled"] = false,
            ["encryption"] = 1,
            ["start-added-torrents"] = true,
            ["blocklist-enabled"] = false,
            // 4 is debug. The whole point of an investigation run is seeing what the other side
            // thought was happening at the moment ours went quiet.
            ["message-level"] = IntFromEnvironment("PEERSHARP_TRANSMISSION_MESSAGE_LEVEL", 3)
        };

        await File.WriteAllTextAsync(
            Path.Combine(configDir, "settings.json"),
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));

        var info = new ProcessStartInfo(exe) { UseShellExecute = false };
        info.EnvironmentVariables["TRANSMISSION_HOME"] = configDir;

        // transmission-daemon needs telling not to fork, and it is the only build that writes the log
        // queue to a file: both it and transmission-qt enable queuing, but only the daemon drains the
        // queue to disk, where the Qt client keeps it in memory for its Message Log window. At
        // message-level 6 that log records every protocol message Transmission sends, immediately
        // before it hands the bytes to its encryption filter - which is the record this investigation
        // needs and cannot get from the shipped GUI build.
        if (Path.GetFileNameWithoutExtension(exe).Contains("daemon", StringComparison.OrdinalIgnoreCase))
        {
            var logPath = Path.Combine(_root, "transmission.log");
            info.ArgumentList.Add("--foreground");
            info.ArgumentList.Add("--logfile");
            info.ArgumentList.Add(logPath);
            _output.WriteLine($"Transmission log: {logPath}");
        }

        _transmission = Process.Start(info) ?? throw new InvalidOperationException("Failed to start Transmission.");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await RpcAsync("session-get");
                return;
            }
            catch (Exception)
            {
                await Task.Delay(500);
            }
        }

        throw new InvalidOperationException($"Transmission RPC did not come up on port {RpcPort}.");
    }

    private async Task AddTorrentToTransmissionAsync(string torrentPath, string downloadDir)
    {
        var metainfo = Convert.ToBase64String(await File.ReadAllBytesAsync(torrentPath));
        var added = await RpcAsync("torrent-add", new { metainfo, downloadDir = downloadDir.Replace('\\', '/') });
        var args = added.GetProperty("arguments");
        Assert.True(
            args.TryGetProperty("torrent-added", out _) || args.TryGetProperty("torrent-duplicate", out _),
            $"Transmission did not accept the torrent: {added}");
    }

    /// <summary>
    /// One RPC call, handling the 409 handshake: the first request of a session is answered with
    /// the session id to use for every request after it.
    /// </summary>
    private async Task<JsonElement> RpcAsync(string method, object? arguments = null)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{RpcPort}/transmission/rpc")
            {
                Content = JsonContent.Create(new { method, arguments = arguments ?? new { } })
            };

            if (_sessionId != null)
            {
                request.Headers.Add("X-Transmission-Session-Id", _sessionId);
            }

            using var response = await _http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                _sessionId = response.Headers.TryGetValues("X-Transmission-Session-Id", out var values)
                    ? values.FirstOrDefault()
                    : null;
                continue;
            }

            response.EnsureSuccessStatusCode();
            return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        }

        throw new InvalidOperationException($"RPC '{method}' failed the session-id handshake.");
    }

    /// <summary>
    /// Reads a file the engine still has open. The leecher keeps its handles until disposal, and
    /// disposing it first would end the run before the content can be checked.
    /// </summary>
    private static async Task<byte[]> ReadAllBytesSharedAsync(string path)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static void RequireEnabled()
    {
        if (Environment.GetEnvironmentVariable(EnableVariable) != "1")
        {
            Assert.Skip($"Set {EnableVariable}=1 to run the Transmission interop test. It starts a real Transmission and transfers real data.");
        }
    }

    private static int IntFromEnvironment(string name, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
    }

    private readonly record struct TransferResult(
        double PercentDone,
        TimeSpan Elapsed,
        string ErrorString,
        int PeersConnected);
}
