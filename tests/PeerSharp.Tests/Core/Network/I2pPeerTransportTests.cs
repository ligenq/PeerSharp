using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using PeerSharp.BEncoding;
using PeerSharp.Internals;
using PeerSharp.Internals.Network;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Core.Network;

[Collection("Integration")]
public sealed class I2pPeerTransportTests
{
    [Fact(Timeout = 60000)]
    public async Task I2pTrackerAndPeerTrafficUseTheSameSamSessionAndDownloadWithoutIpPeers()
    {
        await using var router = new Router();
        string root = Path.Combine(Path.GetTempPath(), "peersharp-i2p-" + Guid.NewGuid().ToString("N"));
        string seedPath = Path.Combine(root, "seed"); Directory.CreateDirectory(seedPath);
        const string name = "i2p.bin";
        byte[] payload = RandomNumberGenerator.GetBytes(65536 + 37);
        await File.WriteAllBytesAsync(Path.Combine(seedPath, name), payload, TestContext.Current.CancellationToken);
        var file = new ApiTorrentFileBuilder().WithName(name).WithPieceLength(16384).WithAnnounce("http://tracker.i2p/announce").AddFile(name, payload).Build();
        await using var seedEngine = ClientEngine.Create(new TorrentClientOptions { Settings = Settings(seedPath, router.Port) });
        await seedEngine.InitializeAsync();
        var seed = await seedEngine.AddTorrentAsync(file, new AddTorrentOptions { StartImmediately = false });
        Assert.Equal(file.PieceCount, await seed.ForceRecheckAsync()); await seed.StartAsync();
        await using var leechEngine = ClientEngine.Create(new TorrentClientOptions { Settings = Settings(Path.Combine(root, "leech"), router.Port) });
        await leechEngine.InitializeAsync();
        var leech = await leechEngine.AddTorrentAsync(file);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        while (!leech.Finished) await Task.Delay(50, deadline.Token);
        var connections = ((Torrent)leech).PeersInternal.GetConnectedPeersInternal().ToArray();
        Assert.NotEmpty(connections);
        Assert.All(connections, connection => Assert.Null(connection.RemoteEndPoint));
        await leech.StopAsync();
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(root, "leech", name), deadline.Token));
        Assert.Equal(2, router.SessionCount); // one per engine, reused for HTTP and peer streams
        Assert.True(router.AnnounceCount > 0);
        Assert.True(((Torrent)seed).IsI2p);
        ((Torrent)seed).I2pTransport!.AddPeers([]);
    }

    private static Settings Settings(string path, int port) => new()
    {
        Files = { DefaultDownloadPath = path },
        Dht = { Enabled = false },
        Connection = { TcpPort = 0, UdpPort = 0, I2pSamHost = "127.0.0.1", I2pSamPort = port,
            EnableLsd = false, UpnpPortMapping = false, NatPmpPortMapping = false, Encryption = Encryption.Require }
    };

    private sealed class Router : IAsyncDisposable
    {
        private sealed record Pending(Stream Stream, string Source, TaskCompletionSource Completed);
        private sealed record Session(string Destination, Channel<Pending> Incoming);
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentDictionary<string, Session> _sessions = new();
        private readonly ConcurrentBag<Task> _handlers = [];
        private readonly Task _loop;
        private int _announces;
        public int SessionCount => _sessions.Count;
        public int AnnounceCount => _announces;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public Router() { _listener.Start(); _loop = AcceptAsync(); }
        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested) _handlers.Add(HandleAsync(await _listener.AcceptTcpClientAsync(_stop.Token)));
            }
            catch (OperationCanceledException) { }
        }
        private static string Token(string command, string name) => command.Split(' ').Single(item => item.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..];
        private Task SendAsync(Stream stream, string line) => stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\n"), _stop.Token).AsTask();
        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    await I2pSamSession.ReadLineAsync(stream, _stop.Token);
                    await SendAsync(stream, "HELLO REPLY RESULT=OK VERSION=3.1");
                    string command = await I2pSamSession.ReadLineAsync(stream, _stop.Token);
                    if (command.StartsWith("SESSION CREATE", StringComparison.Ordinal))
                    {
                        var session = new Session(Convert.ToBase64String(RandomNumberGenerator.GetBytes(387)).Replace('+', '-').Replace('/', '~'), Channel.CreateUnbounded<Pending>());
                        _sessions[Token(command, "ID")] = session;
                        await SendAsync(stream, "SESSION STATUS RESULT=OK DESTINATION=private");
                        await I2pSamSession.ReadLineAsync(stream, _stop.Token);
                        await SendAsync(stream, "NAMING REPLY RESULT=OK VALUE=" + session.Destination);
                        await Task.Delay(Timeout.Infinite, _stop.Token);
                    }
                    else if (command.StartsWith("NAMING LOOKUP", StringComparison.Ordinal))
                    {
                        string name = Token(command, "NAME");
                        var destination = _sessions.Values.FirstOrDefault(session => I2pSamSession.HashToHostname(SHA256.HashData(Convert.FromBase64String(session.Destination.Replace('-', '+').Replace('~', '/')))) == name)?.Destination;
                        await SendAsync(stream, "NAMING REPLY RESULT=OK VALUE=" + (destination ?? "tracker"));
                    }
                    else if (command.StartsWith("STREAM CONNECT", StringComparison.Ordinal))
                    {
                        string destination = Token(command, "DESTINATION");
                        await SendAsync(stream, "STREAM STATUS RESULT=OK");
                        if (destination == "tracker")
                        {
                            string request = await I2pSamSession.ReadLineAsync(stream, _stop.Token);
                            Assert.Contains("ip=", request); Assert.Contains("port=6881", request);
                            while ((await I2pSamSession.ReadLineAsync(stream, _stop.Token)).Length > 0) { }
                            Interlocked.Increment(ref _announces);
                            var body = new BDict(); body.Dict["interval"] = new BNumber(30);
                            body.Dict["peers"] = new BString(_sessions.Values.SelectMany(session => SHA256.HashData(Convert.FromBase64String(session.Destination.Replace('-', '+').Replace('~', '/')))).ToArray());
                            byte[] bytes = BencodeWriter.Write(body);
                            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                            await stream.WriteAsync(bytes, _stop.Token);
                        }
                        else
                        {
                            var target = _sessions.Values.Single(session => session.Destination == destination);
                            var pending = new Pending(stream, _sessions[Token(command, "ID")].Destination, new(TaskCreationOptions.RunContinuationsAsynchronously));
                            await target.Incoming.Writer.WriteAsync(pending, _stop.Token);
                            await pending.Completed.Task.WaitAsync(_stop.Token);
                        }
                    }
                    else if (command.StartsWith("STREAM ACCEPT", StringComparison.Ordinal))
                    {
                        await SendAsync(stream, "STREAM STATUS RESULT=OK");
                        var pending = await _sessions[Token(command, "ID")].Incoming.Reader.ReadAsync(_stop.Token);
                        try
                        {
                            await SendAsync(stream, pending.Source);
                            await Task.WhenAny(pending.Stream.CopyToAsync(stream, _stop.Token), stream.CopyToAsync(pending.Stream, _stop.Token));
                        }
                        finally { pending.Completed.TrySetResult(); }
                    }
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }
        }
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync(); _listener.Stop(); await _loop;
            await Task.WhenAll(_handlers); _stop.Dispose();
        }
    }
}
