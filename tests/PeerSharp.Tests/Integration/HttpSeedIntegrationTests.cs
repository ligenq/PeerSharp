using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using PeerSharp.BEncoding;
using PeerSharp.Internals;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Integration;

[Collection("Integration")]
public sealed class HttpSeedIntegrationTests
{
    [Fact(Timeout = 30000)]
    public async Task AHttpSeedOnlyTorrentDownloadsThroughTheNormalVerifiedPiecePipeline()
    {
        const int pieceLength = 16384;
        const string name = "http-seed-only.bin";
        byte[] payload = RandomNumberGenerator.GetBytes(pieceLength + 137);
        var original = new ApiTorrentFileBuilder().WithName(name).WithPieceLength(pieceLength).AddFile(name, payload).Build();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var root = (BDict)BencodeParser.Parse(original.RawData.ToArray());
        var httpSeeds = new BList();
        httpSeeds.List.Add(new BString(Encoding.ASCII.GetBytes($"http://127.0.0.1:{port}/seed.php?token=test-token")));
        root.Dict["httpseeds"] = httpSeeds;
        var file = PeerSharp.Core.TorrentFile.Parse(BencodeWriter.Write(root));
        Assert.Empty(file.WebSeeds);
        Assert.Single(file.HttpSeeds);
        var requests = new ConcurrentBag<string>();
        var handlers = new ConcurrentBag<Task>();
        using var stopServer = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task serving = ServeAsync();
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "PeerSharpHttpSeedOnly_" + Guid.NewGuid().ToString("N"));
            await using var engine = ClientEngine.Create(new TorrentClientOptions
            {
                Settings = new Settings
                {
                    Files = { DefaultDownloadPath = path },
                    Dht = { Enabled = false },
                    Connection = { TcpPort = 0, UdpPort = 0, EnableTcpIn = false, EnableTcpOut = false,
                        EnableUtpIn = false, EnableUtpOut = false, EnableLsd = false, UpnpPortMapping = false, NatPmpPortMapping = false }
                }
            });
            await engine.InitializeAsync();
            var torrent = await engine.AddTorrentAsync(file);
            await TorrentTestUtility.WaitUntilAsync(() => torrent.Finished, timeoutMs: 10000);
            Assert.Equal(file.PieceCount, ((Torrent)torrent).Pieces.ReceivedCount);
            Assert.Equal(0, torrent.Peers.ConnectedCount);
            await torrent.StopAsync();
            Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(path, name), TestContext.Current.CancellationToken));
            Assert.Equal(new[] { 0, 1 }, requests.Select(query => int.Parse(Query(query)["piece"], CultureInfo.InvariantCulture)).Distinct().Order().ToArray());
            foreach (string query in requests)
            {
                var fields = Query(query);
                Assert.Equal("test-token", fields["token"]);
                Assert.Equal(original.InfoHash.ToArray(), DecodeBinaryParameter(fields["info_hash"]));
                int piece = int.Parse(fields["piece"], CultureInfo.InvariantCulture);
                int length = Math.Min(pieceLength, payload.Length - piece * pieceLength);
                Assert.Equal($"0-{length - 1}", fields["ranges"]);
            }
        }
        finally
        {
            await stopServer.CancelAsync();
            listener.Stop();
            await serving;
            await Task.WhenAll(handlers);
        }

        async Task ServeAsync()
        {
            try
            {
                while (!stopServer.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(stopServer.Token);
                    handlers.Add(RespondAsync(client));
                }
            }
            catch (OperationCanceledException) when (stopServer.IsCancellationRequested) { }
        }

        async Task RespondAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                string line = (await reader.ReadLineAsync(stopServer.Token))!;
                string target = line.Split(' ')[1];
                requests.Add(target);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stopServer.Token))) { }
                int piece = int.Parse(Query(target)["piece"], CultureInfo.InvariantCulture);
                var bytes = payload.AsMemory(piece * pieceLength, Math.Min(pieceLength, payload.Length - piece * pieceLength));
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), stopServer.Token);
                await stream.WriteAsync(bytes, stopServer.Token);
            }
        }
    }

    private static Dictionary<string, string> Query(string target) => target[(target.IndexOf('?') + 1)..]
        .Split('&').Select(parameter => parameter.Split('=', 2)).ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);

    private static byte[] DecodeBinaryParameter(string value)
    {
        var decoded = new List<byte>();
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '%') { decoded.Add(byte.Parse(value.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)); i += 2; }
            else decoded.Add(value[i] == '+' ? (byte)' ' : checked((byte)value[i]));
        }
        return decoded.ToArray();
    }
}
