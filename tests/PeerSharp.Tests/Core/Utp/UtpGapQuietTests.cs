using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Utp;
using System.Net;

namespace PeerSharp.Tests.Core.Utp;

/// <summary>
/// A gap in what a peer sent that nothing fills. libtorrent resends a lost packet at once only the first
/// time, then waits for its retransmission timer - which every packet it receives restarts. So when a
/// gap has stood a while and the peer has gone silent, this side sends nothing for long enough for that
/// timer to run out, and the peer resends what is missing.
/// </summary>
public class UtpGapQuietTests
{
    private static readonly IPEndPoint Remote = new(IPAddress.Loopback, 12345);

    private readonly FakeTimeProvider _time = new();
    private readonly CountingManager _manager = new();
    private readonly UtpStream _stream;

    public UtpGapQuietTests()
    {
        _stream = new UtpStream(_manager, Remote, idRecv: 100, idSend: 101, timeProvider: _time);
        _stream.ProcessPacketWithSack(Header(MessageType.ST_SYN, 1), [], 0, null, null, Remote);
        Data(2);
    }

    [Fact]
    public void AGapThePeerLeavesUnfilled_SilencesThisSide_UntilItWouldHaveResent()
    {
        // Packet 3 lost; 4 arrives, then nothing.
        Data(4);
        _time.Advance(UtpStream.GapBeforeQuiet + TimeSpan.FromMilliseconds(100));
        _stream.CheckTimeout();
        int sent = _manager.Sent;

        // While quiet, even a packet arriving is not acknowledged.
        Data(5);
        _time.Advance(TimeSpan.FromMilliseconds(500));
        _stream.CheckTimeout();
        Assert.Equal(sent, _manager.Sent);

        // Once the peer's timer has had time to run out, this side says where it is again.
        _time.Advance(UtpStream.QuietPeriod);
        _stream.CheckTimeout();
        Assert.True(_manager.Sent > sent);
    }

    [Fact]
    public void TheGapFilled_EndsTheQuiet_AtOnce()
    {
        Data(4);
        _time.Advance(UtpStream.GapBeforeQuiet + TimeSpan.FromMilliseconds(100));
        _stream.CheckTimeout();
        int sent = _manager.Sent;

        Data(3);

        Assert.True(_manager.Sent > sent);
        Assert.Equal((ushort)4, _stream.AckNr);
    }

    [Fact]
    public void WithoutAGap_OrWhileDataIsArriving_ThisSideNeverGoesQuiet()
    {
        // No gap: time passes and every packet is acknowledged.
        _time.Advance(UtpStream.GapBeforeQuiet * 3);
        _stream.CheckTimeout();
        int sent = _manager.Sent;
        Data(3);
        Assert.True(_manager.Sent > sent);

        // A gap, but the peer is still sending: it will fill it, and must not be kept waiting.
        Data(5);
        for (int i = 0; i < 6; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(300));
            Data((ushort)(6 + i));
            _stream.CheckTimeout();
        }

        sent = _manager.Sent;
        Data(20);
        Assert.True(_manager.Sent > sent);
    }

    private void Data(ushort seq) =>
        _stream.ProcessPacketWithSack(Header(MessageType.ST_DATA, seq), [1, 2, 3], 0, null, null, Remote);

    private static MessageHeader Header(MessageType type, ushort seq) => new()
    {
        TypeVer = (byte)((byte)type << 4 | MessageHeader.CurrentVersion),
        WndSize = 65535,
        SeqNr = seq,
        AckNr = 0,
    };

    private sealed class CountingManager : IUtpManager
    {
        private int _sent;

        public int Sent => Volatile.Read(ref _sent);

        public Action<UtpStream>? OnNewConnection { get; set; }

        public Task SendAsync(ReadOnlyMemory<byte> packet, IPEndPoint remote, CancellationToken ct)
        {
            Interlocked.Increment(ref _sent);
            return Task.CompletedTask;
        }

        public void CloseStream(UtpStream stream) { }

        public UtpStream CreateStream(IPEndPoint remote) => throw new NotImplementedException();

        public void Start(IUdpListener listener) => throw new NotImplementedException();

        public void Stop() => throw new NotImplementedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
