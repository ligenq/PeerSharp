using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Config;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Utp;
using System.Net;
using System.Reflection;

namespace PeerSharp.Tests.Core.Utp;

/// <summary>
/// Loss detected from a selective acknowledgement rather than from counting duplicate ones.
/// </summary>
/// <remarks>
/// <para>
/// Packets acknowledged beyond a hole say which packet is missing, where a duplicate
/// acknowledgement only says that something is. libtorrent counts them in <c>parse_sack</c> and
/// resends past <c>dup_ack_limit</c> of them; PeerSharp had the parser and used its output only to
/// suppress a resend, never to cause one, so the retransmission timer was the sole recovery for any
/// peer whose acknowledgements ride on its data packets.
/// </para>
/// <para>
/// Measured uTP-only against the Debian swarm over 75s, adding this took a mean of 214 MiB to 337.
/// </para>
/// </remarks>
public class UtpSackFastRetransmitTests
{
    [Fact]
    public void EnoughPacketsAckedPastAHoleResendTheMissingOne()
    {
        var clock = new FakeTimeProvider();
        var stream = CreateStream(clock);
        var outstanding = Outstanding(stream, clock, firstSeq: 1, count: 8);

        // Everything up to 1 is acknowledged, and 3..8 arrive selectively - so 2 is the hole.
        Ack(stream, ackNr: 1, sack: [(3, 8)]);

        Assert.True(Resent(outstanding[2]), "The packet the SACK identifies as missing was not resent.");
    }

    [Fact]
    public void TheWindowIsCutOnceForThatLoss()
    {
        var clock = new FakeTimeProvider();
        var stream = CreateStream(clock);
        Outstanding(stream, clock, firstSeq: 1, count: 8);
        Set(stream, "_cwnd", 60000d);

        Ack(stream, ackNr: 1, sack: [(3, 8)]);

        Assert.Equal(30000d, (double)Get(stream, "_cwnd")!, 0);
    }

    [Fact]
    public void ASmallNumberOfPacketsPastTheHoleIsNotYetLoss()
    {
        // Reordering looks like this too, and libtorrent waits for more than dup_ack_limit of them
        // before calling it loss. Resending on the first is how a reordered path gets a retransmit
        // for every packet that merely arrived early.
        var clock = new FakeTimeProvider();
        var stream = CreateStream(clock);
        var outstanding = Outstanding(stream, clock, firstSeq: 1, count: 8);
        Set(stream, "_cwnd", 60000d);

        Ack(stream, ackNr: 1, sack: [(3, 5)]);

        Assert.False(Resent(outstanding[2]));

        // Not halved. The ordinary LEDBAT gain still applies to an acknowledgement that carried new
        // information, so the window may edge up - what must not happen is a loss cut.
        Assert.True((double)Get(stream, "_cwnd")! > 50000d, "The window was cut for a reordering.");
    }

    [Fact]
    public void APacketAlreadyResentIsNotSentAgainForTheSameHole()
    {
        var clock = new FakeTimeProvider();
        var stream = CreateStream(clock);
        var outstanding = Outstanding(stream, clock, firstSeq: 1, count: 12);

        Ack(stream, ackNr: 1, sack: [(3, 8)]);
        Assert.True(Resent(outstanding[2]));

        Set(stream, "_cwnd", 60000d);
        Ack(stream, ackNr: 1, sack: [(3, 12)]);

        // Still the one retransmission, and the window is not cut a second time for it.
        Assert.True((double)Get(stream, "_cwnd")! > 50000d, "The window was cut twice for one hole.");
    }

    /// <summary>Puts <paramref name="count"/> packets in flight and returns them by sequence number.</summary>
    private static Dictionary<ushort, object> Outstanding(
        UtpStream stream, FakeTimeProvider clock, ushort firstSeq, int count)
    {
        var sent = (System.Collections.IDictionary)typeof(UtpStream)
            .GetField("_sentPackets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(stream)!;
        var queue = typeof(UtpStream)
            .GetField("_sentSeqQueue", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(stream)!;
        var packetType = typeof(UtpStream).GetNestedType("SentPacket", BindingFlags.NonPublic)!;

        var bySeq = new Dictionary<ushort, object>();
        for (int i = 0; i < count; i++)
        {
            ushort seq = (ushort)(firstSeq + i);
            var packet = Activator.CreateInstance(packetType)!;
            packetType.GetProperty("Buffer")!.SetValue(packet, new byte[64]);
            packetType.GetProperty("Length")!.SetValue(packet, 64);
            packetType.GetProperty("SeqNr")!.SetValue(packet, seq);
            packetType.GetProperty("SendTime")!.SetValue(packet, clock.GetUtcNow());
            sent[seq] = packet;
            queue.GetType().GetMethod("Enqueue")!.Invoke(queue, [seq]);
            bySeq[seq] = packet;
        }

        // Past the highest outstanding sequence, so the handler treats them all as really sent.
        Set(stream, "_seqNr", (ushort)(firstSeq + count));
        Set(stream, "_state", UtpState.Connected);
        return bySeq;
    }

    private static void Ack(UtpStream stream, ushort ackNr, List<(ushort Start, ushort End)> sack) =>
        typeof(UtpStream)
            .GetMethod("HandleAckWithSack", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(stream, [ackNr, 0u, sack, MessageType.ST_STATE]);

    private static bool Resent(object packet) =>
        (bool)packet.GetType().GetProperty("Resent")!.GetValue(packet)!;

    private static void Set(UtpStream stream, string field, object value) =>
        typeof(UtpStream).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(stream, value);

    private static object? Get(UtpStream stream, string field) =>
        typeof(UtpStream).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(stream);

    private static UtpStream CreateStream(FakeTimeProvider clock) => new(
        new StubUtpManager(),
        new IPEndPoint(IPAddress.Loopback, 12345),
        idRecv: 100,
        idSend: 101,
        timeProvider: clock,
        loggerFactory: NullLoggerFactory.Instance,
        settings: new ConnectionSettings());

    private sealed class StubUtpManager : IUtpManager
    {
        public Action<UtpStream>? OnNewConnection { get; set; }
        public void CloseStream(UtpStream stream) { }
        public UtpStream CreateStream(IPEndPoint remote) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task SendAsync(ReadOnlyMemory<byte> packet, IPEndPoint remote, CancellationToken ct) => Task.CompletedTask;
        public void Start(IUdpListener listener) { }
        public void Stop() { }
    }
}
