using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
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

    private async Task<ClientEngine> CreateSeedEngineAsync(string seedDir, Encryption encryption)
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
            LoggerFactory = NullLoggerFactory.Instance,
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
            ["message-level"] = 3
        };

        await File.WriteAllTextAsync(
            Path.Combine(configDir, "settings.json"),
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));

        var info = new ProcessStartInfo(exe) { UseShellExecute = false };
        info.EnvironmentVariables["TRANSMISSION_HOME"] = configDir;
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
