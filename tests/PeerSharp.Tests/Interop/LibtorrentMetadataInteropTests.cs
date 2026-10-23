using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PeerSharp.Internals;
using PeerSharp.Tests.Integration.Synthetic;
using PeerSharp.BEncoding;

namespace PeerSharp.Tests.Interop;

[Collection("Integration")]
public sealed class LibtorrentMetadataInteropTests
{
    private readonly ITestOutputHelper _output;
    public LibtorrentMetadataInteropTests(ITestOutputHelper output) => _output = output;

    [Theory(Timeout = 120000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MagnetMetadataCompletesInBothDirections(bool peerSharpFetches)
    {
        if (Environment.GetEnvironmentVariable("PEERSHARP_INTEROP") != "1") Assert.Skip("Set PEERSHARP_INTEROP=1.");
        string? executable = LibtorrentOracleTests.FindClientTest();
        if (executable == null) Assert.Skip("Build libtorrent client_test first.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var ct = deadline.Token;
        string root = Path.Combine(Path.GetTempPath(), "peersharp-metadata-oracle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "libtorrent"));
        var fixture = SyntheticMetadataFixture.Create();
        var dictionary = new BDict();
        dictionary.Dict["info"] = BencodeParser.Parse(fixture.InfoBytes);
        string torrentPath = Path.Combine(root, "metadata.torrent");
        byte[] torrentBytes = BencodeWriter.Write(dictionary);
        await File.WriteAllBytesAsync(torrentPath, torrentBytes, ct);
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        var settings = new Settings
        {
            Files = { DefaultDownloadPath = Path.Combine(root, "peersharp") },
            Dht = { Enabled = false },
            Connection = { TcpPort = 0, UdpPort = 0, EnableUtpIn = false, EnableUtpOut = false,
                EnableLsd = false, UpnpPortMapping = false, NatPmpPortMapping = false, Encryption = Encryption.Refuse }
        };
        await using var engine = ClientEngine.Create(new TorrentClientOptions { Settings = settings });
        await engine.InitializeAsync(ct);
        var torrent = peerSharpFetches
            ? await engine.AddMagnetAsync(MagnetLink.Parse($"magnet:?xt=urn:btih:{fixture.InfoHashHex}"), new AddTorrentOptions { StartImmediately = true }, ct)
            : await engine.AddTorrentAsync(PeerSharp.Core.TorrentFile.Parse(torrentBytes), new AddTorrentOptions { StartImmediately = true }, ct);
        var start = new ProcessStartInfo(executable!)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        string source = peerSharpFetches ? torrentPath : $"magnet:?xt=urn:btih:{fixture.InfoHashHex}&x.pe=127.0.0.1:{engine.BoundTcpPort}";
        foreach (string argument in new[] { source, "-k", "-s", Path.Combine(root,"libtorrent"), "-f", Path.Combine(root,"events.log"),
            $"--listen_interfaces=127.0.0.1:{port}", "--enable_dht=0", "--enable_lsd=0", "--enable_upnp=0", "--enable_natpmp=0",
            "--enable_outgoing_utp=0", "--enable_incoming_utp=0", "--allow_multiple_connections_per_ip=1", "--out_enc_policy=2", "--in_enc_policy=2", "--alert_mask=all" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> stdout = PumpOutputAsync(process.StandardOutput, received);
        Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        // Do not poison PeerSharp's reconnect history by dialling before the external
        // process has bound its listener.
        if (peerSharpFetches)
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                using var probe = new TcpClient();
                try { await probe.ConnectAsync(IPAddress.Loopback, port, ct); break; }
                catch (SocketException) { await Task.Delay(100, ct); }
            }
        }
        bool completed = false;
        try
        {
            while (!ct.IsCancellationRequested && !process.HasExited)
            {
                if (peerSharpFetches) engine.OnPeersFound(torrent.Hash, [new IPEndPoint(IPAddress.Loopback, port)]);
                string events = File.Exists(Path.Combine(root, "events.log")) ? await ReadSharedAsync(Path.Combine(root, "events.log")) : "";
                completed = peerSharpFetches ? torrent.HasMetadata : (received.Task.IsCompleted || events.Contains("metadata successfully received", StringComparison.OrdinalIgnoreCase));
                if (completed) break;
                await Task.Delay(200, ct);
            }
            Assert.True(completed, $"Metadata did not complete. Logs: {root}");
            if (peerSharpFetches) Assert.Equal(1800, torrent.PieceCount);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
            _output.WriteLine($"Logs: {root}\n{await stderr}\n{(await stdout)[^Math.Min((await stdout).Length, 1500)..]}");
        }
    }

    private static async Task<string> PumpOutputAsync(StreamReader reader, TaskCompletionSource received)
    {
        var output = new System.Text.StringBuilder();
        char[] buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            output.Append(buffer, 0, count);
            // The libtorrent terminal changes from "downloading metadata" to the name from
            // the verified dictionary. No payload exists, so completion cannot mask this signal.
            if (output.ToString().Contains("synthetic-metadata.bin", StringComparison.Ordinal)) received.TrySetResult();
        }
        return output.ToString();
    }

    private static async Task<string> ReadSharedAsync(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(file);
        return await reader.ReadToEndAsync();
    }
}
