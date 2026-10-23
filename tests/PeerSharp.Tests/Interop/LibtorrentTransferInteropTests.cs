using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using PeerSharp.Core;
using PeerSharp.Internals;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Interop;

[Collection("Integration")]
public sealed class LibtorrentTransferInteropTests
{
    [Theory(Timeout = 100000)]
    [InlineData(TorrentFileVersion.V1, true, true)]
    [InlineData(TorrentFileVersion.V1, true, false)]
    [InlineData(TorrentFileVersion.V1, false, true)]
    [InlineData(TorrentFileVersion.V1, false, false)]
    [InlineData(TorrentFileVersion.V2, true, true)]
    [InlineData(TorrentFileVersion.V2, true, false)]
    [InlineData(TorrentFileVersion.V2, false, true)]
    [InlineData(TorrentFileVersion.V2, false, false)]
    [InlineData(TorrentFileVersion.Hybrid, true, true)]
    [InlineData(TorrentFileVersion.Hybrid, true, false)]
    [InlineData(TorrentFileVersion.Hybrid, false, true)]
    [InlineData(TorrentFileVersion.Hybrid, false, false)]
    public async Task MultiFilePayloadTransfersInBothDirections(TorrentFileVersion version, bool tcp, bool peerSharpSeeds)
    {
        if (Environment.GetEnvironmentVariable("PEERSHARP_INTEROP") != "1") Assert.Skip("Set PEERSHARP_INTEROP=1.");
        string? executable = LibtorrentOracleTests.FindClientTest();
        if (executable == null) Assert.Skip("Build libtorrent client_test first.");
        string root = Path.Combine(Path.GetTempPath(), "peersharp-transfer-oracle-" + Guid.NewGuid().ToString("N"));
        string psPath = Path.Combine(root, "peersharp"), ltPath = Path.Combine(root, "libtorrent");
        Directory.CreateDirectory(psPath); Directory.CreateDirectory(Path.Combine(ltPath, "payload"));
        byte[] first = RandomNumberGenerator.GetBytes(1000), second = RandomNumberGenerator.GetBytes(16384 + 37);
        var file = new ApiTorrentFileBuilder().WithVersion(version).WithName("payload").WithPieceLength(16384)
            .AddFile("a.bin", first).AddFile("b.bin", second).Build();
        string source = peerSharpSeeds ? psPath : Path.Combine(ltPath, "payload");
        await File.WriteAllBytesAsync(Path.Combine(source, "a.bin"), first, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(source, "b.bin"), second, TestContext.Current.CancellationToken);
        string torrentPath = Path.Combine(root, "test.torrent");
        await File.WriteAllBytesAsync(torrentPath, file.RawData.ToArray(), TestContext.Current.CancellationToken);
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0)) { listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; }
        var settings = new Settings
        {
            Files = { DefaultDownloadPath = psPath },
            Dht = { Enabled = false },
            Connection = { TcpPort = 0, UdpPort = 0, EnableTcpOut = tcp, EnableUtpOut = !tcp, PreferUtp = !tcp,
                Encryption = Encryption.Refuse, EnableLsd = false, UpnpPortMapping = false, NatPmpPortMapping = false }
        };
        await using var engine = ClientEngine.Create(new TorrentClientOptions { Settings = settings });
        await engine.InitializeAsync();
        var torrent = await engine.AddTorrentAsync(file, new AddTorrentOptions { StartImmediately = false });
        if (peerSharpSeeds) Assert.Equal(file.PieceCount, await torrent.ForceRecheckAsync());
        await torrent.StartAsync();
        var start = new ProcessStartInfo(executable!)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in new[] { torrentPath, "-k", "-s", ltPath, "--listen_interfaces=127.0.0.1:" + port,
            "--enable_dht=0", "--enable_lsd=0", "--enable_upnp=0", "--enable_natpmp=0", "--out_enc_policy=2", "--in_enc_policy=2",
            "--allow_multiple_connections_per_ip=1", "--enable_incoming_tcp=" + (tcp ? 1:0), "--enable_incoming_utp=" + (tcp ? 0:1) }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(75));
        string destination = peerSharpSeeds ? Path.Combine(ltPath, "payload") : psPath;
        bool complete = false;
        try
        {
            // TCP disabled by the counterpart also disables its listener. Do not probe that
            // absent transport before a uTP test. Give the local process time to bind UDP.
            if (!tcp) await Task.Delay(500, timeout.Token);
            else
            {
                for (int attempt = 0; attempt < 100; attempt++)
                {
                    using var probe = new TcpClient();
                    try { await probe.ConnectAsync(IPAddress.Loopback, port, timeout.Token); break; }
                    catch (SocketException) { await Task.Delay(100, timeout.Token); }
                }
            }
            while (!complete && !timeout.IsCancellationRequested)
            {
                engine.OnPeersFound(file.IsV1 ? file.InfoHash : file.InfoHashV2, [new IPEndPoint(IPAddress.Loopback, port)]);
                complete = await MatchesAsync(Path.Combine(destination, "a.bin"), first) && await MatchesAsync(Path.Combine(destination, "b.bin"), second);
                if (!complete) await Task.Delay(100, timeout.Token);
            }
            Assert.True(complete, $"{version} TCP={tcp} PeerSharpSeeds={peerSharpSeeds}; logs: {root}");
        }
        catch (OperationCanceledException ex) { throw new InvalidOperationException($"Transfer timed out: {version} TCP={tcp} PeerSharpSeeds={peerSharpSeeds}; logs: {root}", ex); }
        finally
        {
            // Keep the counterpart alive to acknowledge uTP FIN during PeerSharp shutdown.
            // Killing it first makes each otherwise successful uTP case wait for the FIN timeout.
            await torrent.StopAsync();
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
            await File.WriteAllTextAsync(Path.Combine(root, "stdout.log"), await stdout, CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(root, "stderr.log"), await stderr, CancellationToken.None);
        }
    }

    private static async Task<bool> MatchesAsync(string path, byte[] expected)
    {
        try
        {
            if (!File.Exists(path)) return false;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length != expected.Length) return false;
            byte[] bytes = new byte[expected.Length];
            await stream.ReadExactlyAsync(bytes, TestContext.Current.CancellationToken);
            return bytes.AsSpan().SequenceEqual(expected);
        }
        catch (IOException) { return false; }
    }
}
