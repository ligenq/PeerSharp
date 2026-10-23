using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using System.Text.Json;
using PeerSharp.Internals;
using PeerSharp.PiecePicking;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Integration.Synthetic;

/// <summary>Real TCP traffic checked against independently tracked piece and request state.</summary>
[Collection("Integration")]
public sealed class SyntheticPeerTrafficAuditTests : IDisposable
{
    // A dedicated loopback address lets passive capture exclude other applications' local traffic.
    private static readonly IPAddress AuditAddress = IPAddress.Parse("127.0.0.42");
    private const int BlockSize = 16 * 1024;
    private readonly string _path;
    private readonly byte[] _data = new byte[4 * BlockSize];
    private readonly List<WireFrame> _sent = [];
    private readonly List<object> _observations = [];
    private readonly bool _retainArtifacts;
    private SyntheticConnection? _connection;

    public SyntheticPeerTrafficAuditTests()
    {
        string? configured = Environment.GetEnvironmentVariable("PEERSHARP_WIRE_AUDIT_DIR");
        _retainArtifacts = !string.IsNullOrEmpty(configured);
        _path = Path.Combine(configured ?? Path.Combine(Path.GetTempPath(), "PeerSharpTrafficAudit"),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_path);
        new Random(1741).NextBytes(_data);
    }

    [Theory(Timeout = 60000)]
    [InlineData(15)] // have-none, followed by HAVE
    [InlineData(5)]  // bitfield already contains the piece
    [InlineData(14)] // have-all already contains the piece
    public async Task DuplicateHaveDoesNotInventAdditionalSources(byte opening)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var peer = SyntheticPeer.Start(new SyntheticPeerOptions(), AuditAddress);
        await using var engine = CreateEngine();
        var torrent = await ConnectAsync(engine, peer, seed: false, ct);
        await SendAsync(opening, opening == 5 ? new byte[] { 0x80 } : [], ct);
        for (int i = 0; i < 4; i++) await SendAsync(4, Integer(0), ct);
        await BarrierAsync(torrent, 3, ct);

        CheckAvailability(torrent, 0, 1, "after repeated HAVE");
        await peer.DisposeAsync();
        Assert.True(await SyntheticPeer.WaitForAsync(() => torrent.Peers.ConnectedCount == 0,
            TimeSpan.FromSeconds(10), ct));
        CheckAvailability(torrent, 0, 0, "after disconnect");
    }

    [Fact(Timeout = 60000)]
    public async Task RetractionAndReadvertisementKeepSourceCountsAccurate()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var peer = SyntheticPeer.Start(new SyntheticPeerOptions(), AuditAddress);
        await using var engine = CreateEngine();
        var torrent = await ConnectAsync(engine, peer, seed: false, ct);
        var handshake = await _connection!.WaitForExtensionHandshakeAsync(TimeSpan.FromSeconds(10), ct);
        var ids = SyntheticBencode.TryGetDictionary(handshake, "m")!;
        byte retractId = checked((byte)SyntheticBencode.TryGetInteger(ids, "lt_donthave")!.Value);
        await SendAsync(5, new byte[] { 0x80 }, ct);
        await BarrierAsync(torrent, 3, ct);
        CheckAvailability(torrent, 0, 1, "initial bitfield");

        byte[] retraction = new byte[5];
        retraction[0] = retractId; // piece zero is encoded by the remaining zero bytes
        await SendAsync(20, retraction, ct);
        await SendAsync(20, retraction, ct);
        await BarrierAsync(torrent, 2, ct);
        CheckAvailability(torrent, 0, 0, "after repeated lt_donthave");

        await SendAsync(4, Integer(0), ct);
        await BarrierAsync(torrent, 1, ct);
        CheckAvailability(torrent, 0, 1, "after readvertisement");
        await peer.DisposeAsync();
        Assert.True(await SyntheticPeer.WaitForAsync(() => torrent.Peers.ConnectedCount == 0,
            TimeSpan.FromSeconds(10), ct));
        CheckAvailability(torrent, 0, 0, "after disconnect");
    }

    [Theory(Timeout = 60000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UploadResponsesMatchRequestsAndBytes(bool fast)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var peer = SyntheticPeer.Start(new SyntheticPeerOptions { AdvertiseFastExtension = fast }, AuditAddress);
        await using var engine = CreateEngine();
        await ConnectAsync(engine, peer, seed: true, ct);
        await SendAsync(fast ? (byte)15 : (byte)5, fast ? [] : new byte[] { 0 }, ct);
        await SendAsync(2, [], ct);
        Assert.True(await _connection!.WaitForFrameAsync(f => f.Id == 1, TimeSpan.FromSeconds(20), ct));

        (int Piece, int Offset, int Length)[] requests = [(0, 0, BlockSize), (0, 128, 1024), (3, 0, BlockSize)];
        foreach (var request in requests)
        {
            byte[] body = new byte[12];
            BinaryPrimitives.WriteInt32BigEndian(body, request.Piece);
            BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(4), request.Offset);
            BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(8), request.Length);
            // Fragment the length prefix and body across writes, exercising stream reassembly.
            byte[] framed = new byte[17];
            BinaryPrimitives.WriteInt32BigEndian(framed, 13);
            framed[4] = 6;
            body.CopyTo(framed, 5);
            _sent.Add(new WireFrame(6, body));
            await _connection.SendRawAsync(framed.AsMemory(0, 1), ct);
            await _connection.SendRawAsync(framed.AsMemory(1, 5), ct);
            await _connection.SendRawAsync(framed.AsMemory(6), ct);
        }

        Assert.True(await SyntheticPeer.WaitForAsync(() => _connection.Frames.Count(f => f.Id == 7) >= requests.Length,
            TimeSpan.FromSeconds(20), ct), _connection.Describe());
        // A bounded observation interval catches duplicate responses after the last legitimate one.
        await Task.Delay(200, ct);
        var responses = _connection.Frames.Where(f => f.Id == 7).ToArray();
        Assert.Equal(requests.Length, responses.Length);
        foreach (var request in requests)
        {
            var response = Assert.Single(responses, f => f.Payload.Length == 8 + request.Length
                && BinaryPrimitives.ReadInt32BigEndian(f.Payload) == request.Piece
                && BinaryPrimitives.ReadInt32BigEndian(f.Payload.AsSpan(4)) == request.Offset);
            Assert.Equal(_data.AsSpan(request.Piece * BlockSize + request.Offset, request.Length).ToArray(), response.Payload[8..]);
        }
        if (!fast) Assert.DoesNotContain(_connection.Frames, f => f.Id is >= 13 and <= 17);
        _observations.Add(new { fast, verifiedResponses = responses.Length });
    }

    private ClientEngine CreateEngine() => ClientEngine.Create(new TorrentClientOptions
    {
        Settings = new Settings
        {
            Files = { DefaultDownloadPath = _path },
            Connection =
            {
                BindAddress = AuditAddress, TcpPort = 0, UdpPort = 0,
                EnableLsd = false, EnableUtpIn = false, EnableUtpOut = false,
                PreferUtp = false, UpnpPortMapping = false, NatPmpPortMapping = false,
                Encryption = Encryption.Refuse
            },
            Dht = { Enabled = false }
        }
    });

    private async Task<Torrent> ConnectAsync(ClientEngine engine, SyntheticPeer peer, bool seed, CancellationToken ct)
    {
        await engine.InitializeAsync(ct);
        if (seed) await File.WriteAllBytesAsync(Path.Combine(_path, "audit.bin"), _data, ct);
        var metadata = new ApiTorrentFileBuilder().WithName("audit.bin").WithPieceLength(BlockSize)
            .AddFile("audit.bin", _data).Build();
        var torrent = (Torrent)await engine.AddTorrentAsync(metadata, new AddTorrentOptions { StartImmediately = false });
        if (seed) Assert.Equal(4, await torrent.ForceRecheckAsync());
        await torrent.StartAsync();
        engine.OnPeersFound(torrent.Hash, [peer.EndPoint]);
        _connection = await peer.WaitForConnectionAsync(0, TimeSpan.FromSeconds(20), ct);
        await _connection.Ready.WaitAsync(ct);
        _observations.Add(new { stage = "connection", peerAddress = peer.EndPoint.Address.ToString(), peerPort = peer.Port });
        return torrent;
    }

    private async Task SendAsync(byte id, byte[] payload, CancellationToken ct)
    {
        _sent.Add(new WireFrame(id, payload));
        await _connection!.SendFrameAsync(id, payload, ct);
    }

    private async Task BarrierAsync(Torrent torrent, int piece, CancellationToken ct)
    {
        // A wire response is a barrier even after HAVE_ALL, when a bit test would already be
        // true before the preceding messages arrived. This downloader cannot serve the request.
        Assert.False(torrent.Finished);
        byte[] request = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(request, piece);
        BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(8), BlockSize);
        await SendAsync(6, request, ct);
        Assert.True(await _connection!.WaitForFrameAsync(f => f.Id == 16 && f.Payload.SequenceEqual(request),
            TimeSpan.FromSeconds(10), ct), "The peer did not answer the trailing request barrier.");
    }

    private void CheckAvailability(Torrent torrent, int piece, int expected, string stage)
    {
        // Compare the live peer map with the actual rarest-first counter, not a second view
        // computed from the same map. The two used to disagree while the public view looked fine.
        var picker = (PiecePicker)typeof(FileTransfer).GetField("_piecePicker", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(torrent.FileTransferInternal)!;
        int actual = picker.GetAvailability(piece);
        int publicCount = torrent.Peers.GetPieceAvailability()[piece];
        _observations.Add(new { stage, piece, expected, actual, publicCount });
        Assert.Equal(expected, publicCount);
        Assert.Equal(expected, actual);
    }

    private static byte[] Integer(int value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    public void Dispose()
    {
        static object DescribeFrame(WireFrame f) => new { id = f.Id, length = f.Payload.Length + 1, payloadHex = Convert.ToHexString(f.Payload) };
        string file = Path.Combine(_path, "traffic.json");
        File.WriteAllText(file, JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            captureKind = "Application messages from real plaintext TCP sockets, not a packet capture",
            sentByTest = _sent.Select(DescribeFrame),
            receivedFromEngine = _connection?.Frames.Select(DescribeFrame),
            observations = _observations
        }, new JsonSerializerOptions { WriteIndented = true }));

        // The transcript and the seeded payload are artifacts of an audit run, not of every test
        // run. Keep them when PEERSHARP_WIRE_AUDIT_DIR asked for them or the case failed, so an
        // ordinary suite run does not accumulate a directory per case forever.
        bool failed = TestContext.Current.TestState?.Result == TestResult.Failed;
        if (_retainArtifacts || failed)
        {
            TestContext.Current.TestOutputHelper!.WriteLine($"Traffic audit: {file}");
            return;
        }

        TestContext.Current.TestOutputHelper!.WriteLine(
            "Traffic audit capture discarded; set PEERSHARP_WIRE_AUDIT_DIR to retain it.");
        try
        {
            Directory.Delete(_path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A retained handle is not worth failing a passing test over.
        }
    }
}
