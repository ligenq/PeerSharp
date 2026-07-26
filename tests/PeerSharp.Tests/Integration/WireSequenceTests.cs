using Microsoft.Extensions.Logging;
using PeerSharp.Internals;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Integration;

/// <summary>
/// The exact byte sequence a fresh peer receives from us, captured from a real socket.
///
/// <para>
/// Interop is decided by these bytes and nothing else. Tests that assert on calls to a mocked send
/// method skip the two things most likely to be wrong - the serialization itself, and the ordering
/// between messages queued from different code paths - so they can pass while the wire is malformed.
/// Here a stub peer completes a real handshake over a real TCP connection and decodes whatever arrives.
/// </para>
///
/// <para>
/// The rules being checked are other clients' actual parsers. Transmission, for instance, drops the
/// connection on any message id it does not recognise, and enforces an exact length for every message
/// it does.
/// </para>
/// </summary>
[Collection("Integration")]
public class WireSequenceTests : IDisposable
{
    private readonly string _path;
    private readonly ILoggerFactory _loggerFactory;

    public WireSequenceTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "PeerSharpWire_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_path);
        _loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
    }

    /// <summary>Message ids Transmission's is_message_length_correct accepts; anything else kills the connection.</summary>
    private static readonly Dictionary<byte, string> KnownIds = new()
    {
        [0] = "choke", [1] = "unchoke", [2] = "interested", [3] = "not-interested",
        [4] = "have", [5] = "bitfield", [6] = "request", [7] = "piece", [8] = "cancel",
        [9] = "port", [13] = "suggest", [14] = "have-all", [15] = "have-none",
        [16] = "reject", [17] = "allowed-fast", [20] = "ltep"
    };

    [Fact(Timeout = 90000)]
    public async Task SeedingToAFreshPeer_SendsAConformantOpeningSequence()
    {
        var captured = await CaptureOpeningSequenceAsync();

        Assert.NotEmpty(captured);

        // 1. BEP 3: the piece advertisement comes first among BitTorrent messages.
        //
        // The BEP 10 extension handshake is the one exemption, and a universal one: BEP 10 asks for it
        // "immediately after the standard bittorrent handshake", every modern client sends it there, and
        // Transmission's own opening sequence is ltep -> bitfield -> dht port. Our parser grants the
        // same exemption, so requiring the advertisement to be literally first would fail against every
        // real client including ourselves.
        var beforeAdvertisement = captured
            .TakeWhile(static m => m.Id is not (5 or 14 or 15))
            .ToArray();

        Assert.True(
            captured.Any(static m => m.Id is 5 or 14 or 15),
            $"No piece advertisement was sent at all. Sequence: {Format(captured)}");

        Assert.True(
            beforeAdvertisement.All(static m => m.Id == 20),
            $"A BitTorrent message preceded our piece advertisement, so a strict peer will discard it and " +
            $"never request anything from us. Only the ltep handshake may come first. " +
            $"Sequence: {Format(captured)}");

        // 2. Every id must be one a strict peer recognises.
        foreach (var (id, _) in captured)
        {
            Assert.True(
                KnownIds.ContainsKey(id),
                $"Sent message id {id}, which Transmission treats as unrecognised and drops the connection for. " +
                $"Sequence: {Format(captured)}");
        }

        // 3. Fixed-length messages must be exactly the length other parsers demand.
        foreach (var (id, length) in captured)
        {
            int? expected = id switch
            {
                0 or 1 or 2 or 3 or 14 or 15 => 1,
                4 or 13 or 17 => 5,
                6 or 8 or 16 => 13,
                9 => 3,
                _ => null
            };

            if (expected is { } exact)
            {
                Assert.True(
                    length == exact,
                    $"'{Describe(id)}' was {length} bytes, but parsers require exactly {exact}. Sequence: {Format(captured)}");
            }
        }

        // 4. Ltep must carry at least the extended id plus payload.
        foreach (var (id, length) in captured.Where(static m => m.Id == 20))
        {
            Assert.True(length >= 2, $"Ltep message was {length} bytes; parsers require at least 2. Sequence: {Format(captured)}");
        }
    }

    [Fact(Timeout = 90000)]
    public async Task SeedingToAFreshPeer_AdvertisesEveryPiece()
    {
        // Advertising "have all" is what makes a leecher interested in us. Sending nothing, or an empty
        // bitfield, is the difference between being a useful seed and being ignored.
        var captured = await CaptureOpeningSequenceAsync();

        Assert.True(
            captured.Any(static m => m.Id is 14 or 5),
            $"A complete torrent advertised neither have-all nor a bitfield, so no leecher will ever ask us " +
            $"for a piece. Sequence: {Format(captured)}");

        // have-all rather than a full bitfield, since the stub advertised the fast extension.
        Assert.Contains(captured, static m => m.Id == 14);
    }

    private static string Describe(byte id) => KnownIds.TryGetValue(id, out var name) ? name : $"unknown({id})";

    private static string Format(IReadOnlyList<(byte Id, int Length)> messages)
    {
        return string.Join(" -> ", messages.Select(static m => $"{Describe(m.Id)}[{m.Length}]"));
    }

    /// <summary>
    /// Stands up a complete seeding engine, points it at a stub peer, and decodes everything it sends
    /// after the handshake.
    /// </summary>
    private async Task<IReadOnlyList<(byte Id, int Length)>> CaptureOpeningSequenceAsync()
    {
        const string fileName = "wire.bin";
        byte[] payload = new byte[256 * 1024];
        Random.Shared.NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(_path, fileName), payload, TestContext.Current.CancellationToken);

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(64 * 1024)
            .AddFile(fileName, payload)
            .Build();

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var messages = new List<(byte Id, int Length)>();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var stub = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();

            // Read their handshake, then answer with one advertising ltep, DHT and the fast extension,
            // so we exercise the same paths a modern client would.
            byte[] theirHandshake = new byte[68];
            await ReadExactlyAsync(stream, theirHandshake, 68);

            byte[] ours = new byte[68];
            ours[0] = 19;
            "BitTorrent protocol"u8.CopyTo(ours.AsSpan(1));
            ours[25] |= 0x10; // ltep
            ours[27] |= 0x01; // dht
            ours[27] |= 0x04; // fast extension
            theirHandshake.AsSpan(28, 20).CopyTo(ours.AsSpan(28)); // echo the info hash
            for (int i = 0; i < 20; i++)
            {
                ours[48 + i] = (byte)('A' + (i % 26));
            }

            await stream.WriteAsync(ours);
            await stream.FlushAsync();

            // Decode length-prefixed messages until the peer goes quiet.
            byte[] header = new byte[4];
            using var idle = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try
            {
                while (!idle.IsCancellationRequested)
                {
                    await ReadExactlyAsync(stream, header, 4, idle.Token);
                    int length = BinaryPrimitives.ReadInt32BigEndian(header);
                    if (length == 0)
                    {
                        continue; // keepalive
                    }

                    byte[] body = new byte[length];
                    await ReadExactlyAsync(stream, body, length, idle.Token);

                    lock (messages)
                    {
                        messages.Add((body[0], length));
                    }

                    // Three messages is enough to judge the opening sequence.
                    if (messages.Count >= 3)
                    {
                        done.TrySetResult(true);
                    }
                }
            }
            catch (OperationCanceledException) { /* Idle timeout ends the capture. */ }
            catch (IOException) { /* Peer closed. */ }

            done.TrySetResult(true);
        });

        var settings = new Settings
        {
            Files = { DefaultDownloadPath = _path },
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

        await using var engine = ClientEngine.Create(new TorrentClientOptions
        {
            LoggerFactory = _loggerFactory,
            Settings = settings
        });

        await engine.InitializeAsync();

        var torrent = await engine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });
        Assert.Equal(torrentFile.PieceCount, await torrent.ForceRecheckAsync());
        await torrent.StartAsync();

        // Dial the stub, exactly as the engine dials a peer it learned from a tracker.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!done.Task.IsCompleted && DateTime.UtcNow < deadline)
        {
            engine.OnPeersFound(torrent.Hash, [new IPEndPoint(IPAddress.Loopback, port)]);
            await Task.Delay(250);
        }

        await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        listener.Stop();
        try { await stub; } catch { /* Listener stopped underneath it. */ }

        lock (messages)
        {
            return [.. messages];
        }
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken ct = default)
    {
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (n == 0)
            {
                throw new IOException("Peer closed the connection.");
            }

            read += n;
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _loggerFactory.Dispose();
        try
        {
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, recursive: true);
            }
        }
        catch (IOException) { /* Best effort. */ }
        catch (UnauthorizedAccessException) { /* Best effort. */ }
    }
}
