using PeerSharp.Core;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Utilities;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Interop;

/// <summary>
/// Records a real MSE handshake with qBittorrent, so the wire format can be checked against bytes
/// another implementation actually produced.
/// </summary>
/// <remarks>
/// <para>
/// Everything else testing this encryption runs our initiator against our own responder, which
/// proves the two halves agree with each other and nothing about whether either agrees with the
/// specification. That distinction has already cost this repository real bugs - a late bitfield, a
/// dead socket reused for the plaintext fallback, an inbound uTP path that rejected every encrypted
/// peer - each invisible to testing against ourselves because our own parser was tolerant of it.
/// </para>
/// <para>
/// A recording is only replayable if this side brings the same Diffie-Hellman private key back
/// afterwards, so one is fixed here and written into the fixture. That is why the capture uses the
/// internal constructor taking an exchange: with a fresh key the recording decrypts to nothing.
/// </para>
/// <para>
/// Gated, like the rest of this directory. Run it to regenerate
/// <c>Core/Peers/Fixtures/qbittorrent-mse-handshake.json</c> when the counterparty is worth
/// re-recording; the test that consumes the fixture runs everywhere and needs no client.
/// </para>
/// </remarks>
public sealed class MseHandshakeCaptureTests : IAsyncLifetime
{
    private const string EnableVariable = "PEERSHARP_MSE_CAPTURE";
    private const string ExeVariable = "PEERSHARP_QBITTORRENT_EXE";
    private const string DefaultExe = @"C:\Program Files\qBittorrent\qbittorrent.exe";

    private const int QBittorrentPeerPort = 52997;

    private readonly ITestOutputHelper _output;
    private readonly string _root;

    private Process? _qbittorrent;

    public MseHandshakeCaptureTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), "peersharp-mse-" + Guid.NewGuid().ToString("N")[..8]);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_qbittorrent is { HasExited: false })
        {
            try
            {
                _qbittorrent.Kill(entireProcessTree: true);
                await _qbittorrent.WaitForExitAsync();
            }
            catch (InvalidOperationException)
            {
            }
        }

        _qbittorrent?.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact(Timeout = 300_000)]
    public async Task CaptureEncryptedHandshakeFromQBittorrent()
    {
        string exe = RequireQBittorrent();

        var seedDir = Path.Combine(_root, "seed");
        Directory.CreateDirectory(seedDir);

        var (torrent, torrentPath) = await CreatePayloadAsync(seedDir, "mse-capture.bin");
        byte[] infoHash = torrent.InfoHash.ToArray();
        _output.WriteLine($"Torrent info hash: {Convert.ToHexString(infoHash)}");

        await StartQBittorrentAsync(exe, seedDir, torrentPath);

        // Fixed so the exchange can be replayed. Arbitrary bytes, but written down.
        byte[] privateKey = new byte[96];
        for (int i = 0; i < privateKey.Length; i++)
        {
            privateKey[i] = (byte)(i * 7 + 13);
        }

        byte[] peerId = Encoding.ASCII.GetBytes("-PS0001-CAPTURE00000");
        var received = new List<byte[]>();
        var sent = new List<byte[]>();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, QBittorrentPeerPort, TestContext.Current.CancellationToken);
        using var stream = client.GetStream();

        using var handshake = new ProtocolEncryptionHandshake(infoHash, initiator: true, new DiffieHellman(privateKey))
        {
            InitialPayload = BuildBitTorrentHandshake(infoHash, peerId)
        };

        byte[] outgoing = handshake.Initiate();
        sent.Add(outgoing);
        await stream.WriteAsync(outgoing, TestContext.Current.CancellationToken);

        byte[] buffer = new byte[8192];
        var deadline = Stopwatch.StartNew();
        while (!handshake.IsComplete && !handshake.IsError && deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            readCts.CancelAfter(TimeSpan.FromSeconds(15));

            int read;
            try
            {
                read = await stream.ReadAsync(buffer, readCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (read == 0)
            {
                break;
            }

            byte[] chunk = buffer[..read];
            received.Add(chunk.ToArray());
            _output.WriteLine($"  <- {read} bytes from qBittorrent");

            byte[] reply = handshake.HandleIncoming(chunk);
            if (reply.Length > 0)
            {
                sent.Add(reply);
                await stream.WriteAsync(reply, TestContext.Current.CancellationToken);
                _output.WriteLine($"  -> {reply.Length} bytes to qBittorrent");
            }
        }

        Assert.False(handshake.IsError, "the handshake with qBittorrent failed");
        Assert.True(handshake.IsComplete, "the handshake with qBittorrent did not complete");
        Assert.NotNull(handshake.Encryption);

        // The initiator's own payload went out inside the handshake; what comes back is ordinary
        // encrypted stream data. Whatever the last handshake read left over is still encrypted, as
        // PeerCommunication finds it, and the rest arrives on later reads - qBittorrent sends its
        // BitTorrent handshake in a separate segment, so a capture that stopped at IsComplete would
        // record the key exchange and never the first thing the keys are used on.
        var plaintext = new List<byte>();
        byte[] trailing = handshake.TrailingData;
        if (trailing.Length > 0)
        {
            handshake.Encryption!.RC4In.Decrypt(trailing);
            plaintext.AddRange(trailing);
        }

        while (plaintext.Count < 68 && deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            readCts.CancelAfter(TimeSpan.FromSeconds(15));

            int read = await stream.ReadAsync(buffer, readCts.Token);
            if (read == 0)
            {
                break;
            }

            // Record the ciphertext, then decrypt a copy: RC4 works in place, so decrypting the
            // recorded array would store the plaintext and leave the recording undecryptable.
            byte[] chunk = buffer[..read];
            received.Add(chunk.ToArray());
            _output.WriteLine($"  <- {read} bytes from qBittorrent (post-handshake)");

            handshake.Encryption!.RC4In.Decrypt(chunk);
            plaintext.AddRange(chunk);
        }

        Assert.True(plaintext.Count >= 68, $"only {plaintext.Count} plaintext bytes, expected a 68-byte handshake");
        byte[] theirHandshake = plaintext.ToArray()[..68];
        Assert.Equal(19, theirHandshake[0]);
        Assert.Equal("BitTorrent protocol", Encoding.ASCII.GetString(theirHandshake, 1, 19));
        Assert.Equal(infoHash, theirHandshake[28..48]);

        _output.WriteLine($"qBittorrent BT handshake decrypted, peer id: {Encoding.ASCII.GetString(theirHandshake[48..68])}");

        var fixture = new
        {
            Description = "MSE handshake recorded against qBittorrent. PeerSharp is the initiator, "
                + "using the fixed private key below so the exchange can be replayed offline.",
            Counterparty = FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? "qBittorrent",
            RecordedUtc = DateTimeOffset.UtcNow.ToString("O"),
            PrivateKey = Convert.ToHexString(privateKey),
            InfoHash = Convert.ToHexString(infoHash),
            OurPeerId = Convert.ToHexString(peerId),
            Received = received.Select(Convert.ToHexString).ToArray(),
            TheirHandshake = Convert.ToHexString(theirHandshake)
        };

        string fixturePath = Path.Combine(FixtureDirectory(), "qbittorrent-mse-handshake.json");
        Directory.CreateDirectory(Path.GetDirectoryName(fixturePath)!);
        await File.WriteAllTextAsync(
            fixturePath,
            JsonSerializer.Serialize(fixture, new JsonSerializerOptions { WriteIndented = true }),
            TestContext.Current.CancellationToken);

        _output.WriteLine($"Wrote {fixturePath}");
    }

    /// <summary>
    /// The fixture lives beside the test that replays it, in the source tree rather than the output
    /// directory - a recording that only exists under bin is one nobody can review in a diff.
    /// </summary>
    private static string FixtureDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PeerSharp.Tests.csproj")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "Core", "Peers", "Fixtures");
    }

    private static byte[] BuildBitTorrentHandshake(byte[] infoHash, byte[] peerId)
    {
        byte[] message = new byte[68];
        message[0] = 19;
        "BitTorrent protocol"u8.CopyTo(message.AsSpan(1));
        // Reserved: advertise the extension protocol, as a real connection would.
        message[25] = 0x10;
        infoHash.CopyTo(message.AsSpan(28));
        peerId.CopyTo(message.AsSpan(48));
        return message;
    }

    private async Task<(TorrentFile Torrent, string TorrentPath)> CreatePayloadAsync(string directory, string fileName)
    {
        byte[] payload = new byte[1024 * 1024];
        RandomNumberGenerator.Fill(payload);
        string file = Path.Combine(directory, fileName);
        await File.WriteAllBytesAsync(file, payload, TestContext.Current.CancellationToken);

        var torrent = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(256 * 1024)
            .AddFileFromPath(file, fileName)
            .Build();

        string torrentPath = Path.Combine(_root, fileName + ".torrent");
        await File.WriteAllBytesAsync(torrentPath, torrent.RawData.ToArray(), TestContext.Current.CancellationToken);
        return (torrent, torrentPath);
    }

    private async Task StartQBittorrentAsync(string exe, string savePath, string torrentPath)
    {
        string profileDir = Path.Combine(_root, "profile");
        string configDir = Path.Combine(profileDir, "qBittorrent", "config");
        Directory.CreateDirectory(configDir);

        string ini = string.Join('\n',
            "[LegalNotice]",
            "Accepted=true",
            "",
            "[AddNewTorrentDialog]",
            "Enabled=false",
            "",
            "[BitTorrent]",
            $@"Session\Port={QBittorrentPeerPort}",
            @"Session\UseRandomPort=false",
            @"Session\DHTEnabled=false",
            @"Session\LSDEnabled=false",
            @"Session\PeXEnabled=false",
            // 1 is "require encryption", so the capture cannot silently record a plaintext
            // connection and call it an encrypted one.
            @"Session\Encryption=1",
            @"Session\QueueingSystemEnabled=false",
            $@"Session\DefaultSavePath={savePath.Replace('\\', '/')}",
            "");

        await File.WriteAllTextAsync(Path.Combine(configDir, "qBittorrent.ini"), ini, TestContext.Current.CancellationToken);

        var info = new ProcessStartInfo(exe) { UseShellExecute = false };
        info.ArgumentList.Add($"--profile={profileDir}");
        info.ArgumentList.Add("--confirm-legal-notice");
        info.ArgumentList.Add("--no-splash");
        info.ArgumentList.Add(torrentPath);
        _qbittorrent = Process.Start(info) ?? throw new InvalidOperationException("Failed to start qBittorrent.");

        await Task.Delay(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
        if (_qbittorrent.HasExited)
        {
            throw new InvalidOperationException(
                "qBittorrent exited immediately, which means another instance was already running.");
        }

        _output.WriteLine($"qBittorrent started on port {QBittorrentPeerPort}, seeding from {savePath}");
    }

    private static string RequireQBittorrent()
    {
        if (Environment.GetEnvironmentVariable(EnableVariable) != "1")
        {
            Assert.Skip($"Set {EnableVariable}=1 to re-record the MSE handshake fixture.");
        }

        string exe = Environment.GetEnvironmentVariable(ExeVariable) ?? DefaultExe;
        if (!File.Exists(exe))
        {
            Assert.Skip($"qBittorrent not found at {exe}. Set {ExeVariable} to its path.");
        }

        return exe;
    }
}
