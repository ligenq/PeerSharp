using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using PeerSharp.Internals;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Integration.Synthetic;

/// <summary>
/// What PeerSharp does when the peer on the other end is not well behaved.
///
/// <para>
/// This is the half of interop no reference implementation can test. libtorrent will never send a
/// bitfield of the wrong length, a <c>have</c> for a piece that cannot exist, or a request for eight
/// megabytes in one block - it is a correct client, and correct clients only exercise the paths that
/// already work. Anything on a public swarm can send all of it.
/// </para>
///
/// <para>
/// The claim these tests share is not that PeerSharp answers each case in some particular way. Being
/// lenient and being strict are both defensible, and BEP 3 does not settle most of it. The claim is
/// that hostile input cannot make PeerSharp act against the torrent's interest - no request for a
/// piece that does not exist, no block served larger than a block, no buffer sized by a number the
/// peer chose - and that the engine is still working afterwards.
/// </para>
///
/// <para>
/// What these are worth, stated plainly, because it differs from the ported regressions next door.
/// Only the oversized-frame test has been shown to fail when its guard is removed. The others assert
/// properties that currently hold structurally rather than by a single check: the piece map is
/// allocated at the torrent's piece count, so an over-long bitfield cannot reach past it whatever the
/// clamp does, and an oversized request is refused by the length check, by the block-validity check,
/// and again below both - deleting any one of them changes nothing observable. So they are regression
/// guards on an invariant, not proof that anything today enforces it in one place. That is a good
/// thing to have found out, and a bad thing to leave unwritten: a test whose teeth have never been
/// checked reads exactly like one that has them.
/// </para>
/// </summary>
[Collection("Integration")]
public class SyntheticPeerHostileInputTests : IDisposable
{
    private const int PieceLength = 16 * 1024;
    private const int PieceCount = 8;

    private readonly ILoggerFactory _loggerFactory;
    private readonly string _path;

    public SyntheticPeerHostileInputTests()
    {
        _loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
        _path = Path.Combine(Path.GetTempPath(), "PeerSharpHostile_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_path);
    }

    /// <summary>
    /// An id belonging to no message in any BEP. Transmission drops the connection on one; being
    /// lenient instead is a legitimate choice, and either way the engine has to survive it.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task AnUnknownMessageIdDoesNotTakeTheEngineDown()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var peer = SyntheticPeer.Start(new SyntheticPeerOptions());
        await using var engine = CreateEngine();
        var torrent = await AddLeechingTorrentAsync(engine, cancellationToken);

        var connection = await DialAsync(engine, torrent, peer, cancellationToken);
        await connection.Ready;

        await connection.SendFrameAsync(250, new byte[] { 1, 2, 3, 4 }, cancellationToken);
        await connection.SendFrameAsync(99, ReadOnlyMemory<byte>.Empty, cancellationToken);

        await AssertEngineStillServesPeersAsync(engine, torrent, cancellationToken);
    }

    /// <summary>
    /// A length prefix is four bytes the peer chooses, so it must never be believed far enough to be
    /// allocated. This claims a hundred megabytes and then sends none of it.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task AFrameLargerThanTheProtocolAllowsIsRefusedRatherThanBuffered()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var peer = SyntheticPeer.Start(new SyntheticPeerOptions());
        await using var engine = CreateEngine();
        var torrent = await AddLeechingTorrentAsync(engine, cancellationToken);

        var connection = await DialAsync(engine, torrent, peer, cancellationToken);
        await connection.Ready;

        // A hundred megabytes: far past any real message, but not so large that adding the four byte
        // header overflows. int.MaxValue would, and the overflow is caught by a different check
        // entirely - so a test using it passes whether or not the size limit exists, which is how the
        // first version of this test managed to prove nothing.
        byte[] absurd = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(absurd, 100 * 1024 * 1024);
        await connection.SendRawAsync(absurd, cancellationToken);

        // Promptness is the whole assertion. A connection that sits there until some idle timeout
        // eventually reaps it looks identical to one refused on the spot if all you check is that it
        // ended - and an earlier version of this test passed with the size guard deleted for exactly
        // that reason. Refusing an impossible length needs no waiting: the four bytes are already
        // enough to know.
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        bool closed = await connection.WaitForCloseAsync(TimeSpan.FromSeconds(5), cancellationToken);
        elapsed.Stop();

        Assert.True(
            closed,
            $"The connection was still open {elapsed.Elapsed.TotalSeconds:0.#}s after claiming a hundred megabyte " +
            "message and sending none of it. A peer that can hold a connection open by promising bytes it " +
            "never sends can hold arbitrarily many of them open at once, and the length prefix is four bytes " +
            "it chose freely.");

        await AssertEngineStillServesPeersAsync(engine, torrent, cancellationToken);
    }

    /// <summary>
    /// A <c>have</c> naming a piece beyond the end of the torrent, mixed in with a truthful bitfield so
    /// that real requests still flow and the test has something to be wrong about.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task AHaveForAPieceThatCannotExistIsNeverRequestedBack()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var peer = SyntheticPeer.Start(new SyntheticPeerOptions());
        await using var engine = CreateEngine();
        var torrent = await AddLeechingTorrentAsync(engine, cancellationToken);

        var connection = await DialAsync(engine, torrent, peer, cancellationToken);
        await connection.Ready;

        await connection.SendFrameAsync(5, FullBitfield(), cancellationToken);

        byte[] impossible = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(impossible, int.MaxValue);
        await connection.SendFrameAsync(4, impossible, cancellationToken);

        BinaryPrimitives.WriteInt32BigEndian(impossible, -1);
        await connection.SendFrameAsync(4, impossible, cancellationToken);

        await connection.SendFrameAsync(1, ReadOnlyMemory<byte>.Empty, cancellationToken); // Unchoke.

        await AssertRequestsStayInsideTheTorrentAsync(connection, cancellationToken);
    }

    /// <summary>
    /// A bitfield four times longer than the torrent, every bit set. The decoder accepts any length
    /// here and the piece map clamps, so nothing throws - which is exactly why the interesting question
    /// is what gets requested afterwards rather than whether it was rejected.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task AnOverlongBitfieldDoesNotProduceRequestsForPiecesThatDoNotExist()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var peer = SyntheticPeer.Start(new SyntheticPeerOptions());
        await using var engine = CreateEngine();
        var torrent = await AddLeechingTorrentAsync(engine, cancellationToken);

        var connection = await DialAsync(engine, torrent, peer, cancellationToken);
        await connection.Ready;

        byte[] overlong = new byte[((PieceCount + 7) / 8) * 4];
        Array.Fill(overlong, (byte)0xFF);
        await connection.SendFrameAsync(5, overlong, cancellationToken);
        await connection.SendFrameAsync(1, ReadOnlyMemory<byte>.Empty, cancellationToken); // Unchoke.

        await AssertRequestsStayInsideTheTorrentAsync(connection, cancellationToken);
    }

    /// <summary>
    /// A request for far more than a block, sent alongside a legitimate one.
    ///
    /// <para>
    /// The legitimate request is what gives this teeth: it proves we were unchoked and serving, so a
    /// refusal of the oversized one is a refusal on its merits rather than the connection simply not
    /// having got that far. Honouring it would let any peer turn a sixteen kilobyte request into a
    /// megabyte read.
    /// </para>
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task ARequestForMoreThanOneBlockIsRefusedRatherThanServed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var peer = SyntheticPeer.Start(new SyntheticPeerOptions());
        await using var engine = CreateEngine();
        var torrent = await AddSeedingTorrentAsync(engine, cancellationToken);

        var connection = await DialAsync(engine, torrent, peer, cancellationToken);
        await connection.Ready;

        await connection.SendFrameAsync(2, ReadOnlyMemory<byte>.Empty, cancellationToken); // Interested.

        bool unchoked = await connection.WaitForFrameAsync(
            static frame => frame.Id == 1, TimeSpan.FromSeconds(60), cancellationToken);

        Assert.True(unchoked, $"Never unchoked, so nothing was going to be served either way. Traffic: {connection.Describe()}");

        await connection.SendFrameAsync(6, BuildRequest(0, 0, PieceLength), cancellationToken);
        await connection.SendFrameAsync(6, BuildRequest(0, 0, 1024 * 1024), cancellationToken);

        bool served = await connection.WaitForFrameAsync(
            static frame => frame.Id == 7, TimeSpan.FromSeconds(60), cancellationToken);

        Assert.True(served, $"The legitimate request went unanswered, so this proves nothing about the other. Traffic: {connection.Describe()}");

        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

        var oversized = connection.Frames
            .Where(static frame => frame.Id == 7 && frame.Payload.Length - 8 > PieceLength)
            .ToArray();

        Assert.True(
            oversized.Length == 0,
            $"A piece message carried {(oversized.Length > 0 ? oversized[0].Payload.Length - 8 : 0)} bytes for a " +
            $"block that may be at most {PieceLength}. A peer can then choose how much we read and send per " +
            $"request. Traffic: {connection.Describe()}");
    }

    /// <summary>
    /// The shared claim of the two availability tests: whatever the peer said it had, nothing may be
    /// asked for that is not a piece of this torrent.
    /// </summary>
    private static async Task AssertRequestsStayInsideTheTorrentAsync(
        SyntheticConnection connection, CancellationToken cancellationToken)
    {
        bool requested = await connection.WaitForFrameAsync(
            static frame => frame.Id == 6, TimeSpan.FromSeconds(60), cancellationToken);

        Assert.True(
            requested,
            $"Nothing was requested at all, so this test cannot tell a correctly ignored piece from a " +
            $"connection that never got going. Traffic: {connection.Describe()}");

        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

        var outside = connection.Frames
            .Where(static frame => frame.Id == 6 && frame.Payload.Length >= 4)
            .Select(static frame => BinaryPrimitives.ReadInt32BigEndian(frame.Payload))
            .Where(static index => index < 0 || index >= PieceCount)
            .ToArray();

        Assert.True(
            outside.Length == 0,
            $"Requested piece index(es) {string.Join(", ", outside.Distinct())} from a torrent with " +
            $"{PieceCount} pieces. The peer decided what we would ask for by claiming to have it.");
    }

    private static byte[] FullBitfield()
    {
        byte[] bitfield = new byte[(PieceCount + 7) / 8];
        Array.Fill(bitfield, (byte)0xFF);
        return bitfield;
    }

    private static byte[] BuildRequest(int piece, int offset, int length)
    {
        byte[] payload = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(payload, piece);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), offset);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(8), length);
        return payload;
    }

    private ClientEngine CreateEngine()
    {
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

        return ClientEngine.Create(new TorrentClientOptions { LoggerFactory = _loggerFactory, Settings = settings });
    }

    /// <summary>A torrent whose data is not on disk, so PeerSharp wants every piece of it.</summary>
    private async Task<ITorrent> AddLeechingTorrentAsync(ClientEngine engine, CancellationToken cancellationToken)
    {
        await engine.InitializeAsync(cancellationToken);

        byte[] payload = new byte[PieceLength * PieceCount];
        Random.Shared.NextBytes(payload);

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName("hostile.bin")
            .WithPieceLength(PieceLength)
            .AddFile("hostile.bin", payload)
            .Build();

        var torrent = await engine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = true });
        return torrent;
    }

    /// <summary>A torrent whose data is on disk, so PeerSharp can serve it.</summary>
    private async Task<ITorrent> AddSeedingTorrentAsync(ClientEngine engine, CancellationToken cancellationToken)
    {
        await engine.InitializeAsync(cancellationToken);

        const string fileName = "seed.bin";
        byte[] payload = new byte[PieceLength * PieceCount];
        Random.Shared.NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(_path, fileName), payload, cancellationToken);

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(PieceLength)
            .AddFile(fileName, payload)
            .Build();

        var torrent = await engine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });
        Assert.Equal(torrentFile.PieceCount, await torrent.ForceRecheckAsync());
        await torrent.StartAsync();
        return torrent;
    }

    /// <summary>
    /// Proves the engine outlived whatever was just done to it, by making it complete an ordinary
    /// handshake with a fresh peer. A crashed accept loop or a wedged torrent fails here.
    /// </summary>
    private static async Task AssertEngineStillServesPeersAsync(
        ClientEngine engine, ITorrent torrent, CancellationToken cancellationToken)
    {
        await using var survivor = SyntheticPeer.Start(new SyntheticPeerOptions());
        var connection = await DialAsync(engine, torrent, survivor, cancellationToken);

        bool handshook = await connection.WaitForFrameAsync(
            static frame => frame.IsExtended && frame.ExtendedId == 0,
            TimeSpan.FromSeconds(30),
            cancellationToken);

        Assert.True(
            handshook,
            $"A fresh peer connected but was never handshaken with, so the engine did not outlive what the " +
            $"test just did to it. Fresh peer saw: {connection.Describe()}");
    }

    private static async Task<SyntheticConnection> DialAsync(
        ClientEngine engine, ITorrent torrent, SyntheticPeer peer, CancellationToken cancellationToken)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (peer.ConnectionCount == 0 && deadline.Elapsed < TimeSpan.FromSeconds(45))
        {
            engine.OnPeersFound(torrent.Hash, [peer.EndPoint]);
            await Task.Delay(250, cancellationToken);
        }

        return await peer.WaitForConnectionAsync(0, TimeSpan.FromSeconds(10), cancellationToken);
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
