using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Utp;
using System.Buffers.Binary;
using System.Net;

namespace PeerSharp.Tests.Core.Utp;

/// <summary>
/// Data that arrives while the reader of a uTP connection is behind. Against real swarms about half of
/// all uTP connections used to stop for up to a minute: a packet that found the reader behind was
/// dropped for the peer to send again, and a copy sent again of one already waiting stopped everything
/// queued behind it for good.
/// </summary>
public class UtpReorderDrainTests
{
    private const int Payload = 1400;

    /// <summary>More than the pipe and the channel feeding it hold together, so the reader falls behind.</summary>
    private const int Packets = 1150;

    private static readonly IPEndPoint Remote = new(IPAddress.Loopback, 12345);

    [Fact(Timeout = 60000)]
    public async Task PacketsArrivingWhileTheReaderIsBehind_AreAllDelivered_WithoutBeingSentAgain()
    {
        var (stream, manager) = Connected();

        for (int i = 0; i < Packets; i++)
        {
            Data(stream, Seq(i));
        }

        await AssertDeliveredInOrderAsync(stream, Packets);
        Assert.Equal(Seq(Packets - 1), stream.AckNr);
        Assert.True(manager.Sent > 0);
    }

    [Fact(Timeout = 60000)]
    public async Task ACopySentAgainOfAPacketAlreadyWaiting_DoesNotStopWhatFollows()
    {
        var (stream, _) = Connected();
        for (int i = 0; i < Packets; i++)
        {
            Data(stream, Seq(i));
        }

        // The peer, hearing nothing, sends the next packet the reader is owed again. Some room is made
        // first, so it arrives in order and is taken, while its first copy is still waiting.
        var some = new byte[64 * 1024];
        await stream.ReadExactlyAsync(some, TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Data(stream, (ushort)(stream.AckNr + 1));

        // Everything is still delivered, once each and in order, after what was read already.
        await AssertDeliveredInOrderAsync(stream, Packets, readAlready: some);
    }

    private static ushort Seq(int index) => (ushort)(2 + index);

    private static (UtpStream Stream, CountingManager Manager) Connected()
    {
        var manager = new CountingManager();
        var stream = new UtpStream(manager, Remote, idRecv: 100, idSend: 101, timeProvider: new FakeTimeProvider());
        stream.ProcessPacketWithSack(Header(MessageType.ST_SYN, 1), [], 0, null, null, Remote);
        return (stream, manager);
    }

    /// <summary>A data packet whose payload says which it is: its sequence number, then filler.</summary>
    private static void Data(UtpStream stream, ushort seq)
    {
        var payload = new byte[Payload];
        BinaryPrimitives.WriteUInt16BigEndian(payload, seq);
        payload.AsSpan(2).Fill((byte)seq);
        stream.ProcessPacketWithSack(Header(MessageType.ST_DATA, seq), payload, 0, null, null, Remote);
    }

    /// <summary>Reads the rest of the <paramref name="packets"/> packets' data, after <paramref name="readAlready"/>, and checks every packet came once and in order.</summary>
    private static async Task AssertDeliveredInOrderAsync(UtpStream stream, int packets, byte[]? readAlready = null)
    {
        readAlready ??= [];
        var rest = new byte[packets * Payload - readAlready.Length];
        await stream.ReadExactlyAsync(rest, TestContext.Current.CancellationToken);
        byte[] all = [.. readAlready, .. rest];

        for (int index = 0; index < packets; index++)
        {
            Assert.Equal(Seq(index), BinaryPrimitives.ReadUInt16BigEndian(all.AsSpan(index * Payload)));
        }
    }

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
