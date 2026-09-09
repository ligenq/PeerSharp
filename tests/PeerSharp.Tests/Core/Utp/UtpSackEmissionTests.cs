using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals.Utp;
using System.Collections;
using System.Net;
using System.Reflection;

namespace PeerSharp.Tests.Core.Utp;

public class UtpSackEmissionTests
{
    [Theory(Timeout = 30000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PiggybackedSackRecoversLostRequestWithoutStateAcksOrTimer(bool fin)
    {
        await using var requester = new Peer();
        await using var responder = new Peer(seq: 101, ack: 499);
        byte[] requests = [10, 11, 12, 13, 14, 15];
        foreach (byte request in requests)
        {
            await requester.Stream.WriteAsync(new byte[] { request }, TestContext.Current.CancellationToken);
        }

        // Drop request 500. Later requests give the responder positive evidence of
        // the hole, but deliberately deliver NONE of its standalone STATE ACKs.
        foreach (byte[] packet in requester.Packets(MessageType.ST_DATA).Skip(1))
        {
            responder.Receive(packet);
        }
        if (fin)
        {
            responder.Stream.Close();
        }
        else
        {
            await responder.Stream.WriteAsync(new byte[] { 0x42 }, TestContext.Current.CancellationToken);
        }
        byte[] report = responder.Last(fin ? MessageType.ST_FIN : MessageType.ST_DATA);
        Assert.Equal(new byte[] { 0x1F, 0, 0, 0 }, Mask(report));
        requester.Receive(report);

        // No fake time has advanced: this must be fast recovery from the actual
        // emitted extension, through the production parser, not timeout recovery.
        Assert.Equal(7, requester.Packets(MessageType.ST_DATA).Length);
        byte[] retry = requester.Last(MessageType.ST_DATA);
        Assert.Equal((ushort)500, Read(retry, 16));
        Assert.Equal(new byte[] { requests[0] }, Payload(retry));
        Assert.Equal(0, Get(requester.Stream, "_timeoutCount"));
        responder.Receive(retry);
        byte[] received = new byte[requests.Length];
        await responder.Stream.ReadExactlyAsync(received, TestContext.Current.CancellationToken);
        Assert.Equal(requests, received);
        requester.Receive(responder.Last(MessageType.ST_STATE));
        Assert.Equal(0, requester.Stream.SentPacketsCount);
    }

    [Fact(Timeout = 30000)]
    public async Task LostTailRequestNeedsTimerDespiteWorkingPiggybackEmission()
    {
        await using var requester = new Peer();
        await using var responder = new Peer(seq: 101, ack: 499);
        await requester.Stream.WriteAsync(new byte[] { 0x13 }, TestContext.Current.CancellationToken);
        // Drop the only request. The responder has no later request to expose a
        // hole, but can continue sending previously requested data in reverse.
        DateTimeOffset deadline = (DateTimeOffset)Get(requester.Stream, "_nextTimeout");
        for (int i = 0; i < 9; i++)
        {
            requester.Advance(TimeSpan.FromMilliseconds(100));
            await responder.Stream.WriteAsync(new byte[] { (byte)i }, TestContext.Current.CancellationToken);
            byte[] data = responder.Last(MessageType.ST_DATA);
            Assert.Empty(Mask(data)); // Correct: there is no out-of-order request.
            requester.Receive(data);
            responder.Receive(requester.Last(MessageType.ST_STATE));
            Assert.Equal(deadline, Get(requester.Stream, "_nextTimeout"));
            Assert.Single(requester.Packets(MessageType.ST_DATA));
        }

        // The manager's 500 ms timer uses a strict deadline comparison; 1.5 s is
        // its first tick after the initial one-second retransmission deadline.
        requester.Advance(TimeSpan.FromMilliseconds(600));
        Assert.Equal(2, requester.Packets(MessageType.ST_DATA).Length);
        byte[] retry = requester.Last(MessageType.ST_DATA);
        Assert.Equal((ushort)500, Read(retry, 16));
        responder.Receive(retry);
        byte[] received = new byte[1];
        await responder.Stream.ReadExactlyAsync(received, TestContext.Current.CancellationToken);
        Assert.Equal(0x13, received[0]);
        requester.Receive(responder.Last(MessageType.ST_STATE));
        Assert.Equal(0, requester.Stream.SentPacketsCount);
    }

    [Theory(Timeout = 30000)]
    [InlineData(100, 0)]
    [InlineData(100, 7)]
    [InlineData(100, 8)]
    [InlineData(100, 31)]
    [InlineData(100, 32)]
    [InlineData(100, 64)]
    [InlineData(100, 255)]
    [InlineData(100, 256)]
    [InlineData(65534, 0)]
    [InlineData(65534, 8)]
    [InlineData(65534, 255)]
    public async Task StateDataAndFinReportEveryRepresentableBit(int ack, int offset)
    {
        await using var peer = new Peer(ack: (ushort)ack);
        peer.ReceiveData((ushort)(ack + 2 + offset));
        byte[] state = peer.Last(MessageType.ST_STATE);

        byte[] payload = [0, 1, 4, 0, 0xFF, 0x13, 0x42];
        await peer.Stream.WriteAsync(payload, TestContext.Current.CancellationToken);
        byte[] data = peer.Last(MessageType.ST_DATA);
        peer.Stream.Close();
        byte[] fin = peer.Last(MessageType.ST_FIN);

        byte[] expectedMask = offset < 256 ? new byte[(offset / 32 + 1) * 4] : [];
        if (offset < 256)
        {
            expectedMask[offset / 8] = (byte)(1 << (offset % 8));
        }

        foreach (byte[] packet in new[] { state, data, fin })
        {
            Assert.Equal((ushort)ack, Read(packet, 18));
            Assert.Equal(expectedMask, Mask(packet));
        }
        Assert.Equal(payload, Payload(data));
        Assert.Empty(Payload(state));
        Assert.Empty(Payload(fin));
    }

    [Theory(Timeout = 30000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullSizedWritesReserveExtensionSpaceAndPreservePayload(bool ipv6)
    {
        await using var sender = new Peer(ipv6: ipv6);
        await using var receiver = new Peer(seq: 101, ack: 499, ipv6: ipv6);
        sender.ReceiveData(357); // The last representable bit requires all 32 mask bytes.
        int mtu = (int)Get(sender.Stream, "_mtuLast");
        byte[] payload = Enumerable.Range(0, mtu * 3 + 17).Select(i => (byte)i).ToArray();

        await sender.Stream.WriteAsync(payload, TestContext.Current.CancellationToken);
        var packets = sender.Packets(MessageType.ST_DATA);
        Assert.True(packets.Length > 1);
        Assert.Equal(mtu, packets[0].Length);
        foreach (byte[] packet in packets)
        {
            Assert.InRange(packet.Length, 55, mtu);
            Assert.Equal(32, Mask(packet).Length);
            Assert.Equal(0x80, Mask(packet)[31]);
            receiver.Receive(packet);
        }

        byte[] received = new byte[payload.Length];
        await receiver.Stream.ReadExactlyAsync(received, TestContext.Current.CancellationToken);
        Assert.Equal(payload, received);
        Assert.Equal(payload, packets.SelectMany(Payload).ToArray());
    }

    [Theory(Timeout = 30000)]
    [InlineData(100, false)]
    [InlineData(100, true)]
    [InlineData(65534, true)]
    public async Task RetryRebuildsMaskWithCurrentAckAndReorderBuffer(int ack, bool advanceAck)
    {
        await using var peer = new Peer(ack: (ushort)ack);
        peer.ReceiveData((ushort)(ack + 2));
        peer.ReceiveData((ushort)(ack + 8));
        byte[] payload = [0x13, 0x42, 0x69];
        await peer.Stream.WriteAsync(payload, TestContext.Current.CancellationToken);
        byte[] original = peer.Last(MessageType.ST_DATA);
        Assert.Equal(new byte[] { 0x41, 0, 0, 0 }, Mask(original));

        if (advanceAck)
        {
            peer.ReceiveData((ushort)(ack + 1), acknowledgeWrites: false);
        }
        else
        {
            peer.ReceiveData((ushort)(ack + 3), acknowledgeWrites: false);
        }
        // New information outside the retained mask's capacity must not grow the retry.
        peer.ReceiveData((ushort)(ack + 50), acknowledgeWrites: false);
        Assert.Equal(8, Mask(peer.Last(MessageType.ST_STATE)).Length);
        peer.Retry();
        byte[] retry = peer.Last(MessageType.ST_DATA);

        Assert.Equal((ushort)(advanceAck ? ack + 2 : ack), Read(retry, 18));
        Assert.Equal(new byte[] { advanceAck ? (byte)0x10 : (byte)0x43, 0, 0, 0 }, Mask(retry));
        Assert.Equal(original.Length, retry.Length);
        Assert.Equal(payload, Payload(retry));
        Assert.Equal(retry.Length, Get(peer.Stream, "_sentBytesUnacked"));
    }

    [Fact(Timeout = 30000)]
    public async Task RetryWithoutAnOriginalExtensionDoesNotGrowTheDatagram()
    {
        await using var peer = new Peer();
        int mtu = (int)Get(peer.Stream, "_mtuLast");
        byte[] payload = new byte[mtu - 20];
        await peer.Stream.WriteAsync(payload, TestContext.Current.CancellationToken);
        peer.ReceiveData(102, acknowledgeWrites: false);
        peer.Retry();

        byte[] retry = peer.Last(MessageType.ST_DATA);
        Assert.Empty(Mask(retry));
        Assert.Equal(mtu, retry.Length);
        Assert.Equal(payload, Payload(retry));
    }

    [Theory(Timeout = 30000)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RetryRemovesEmptyReportFromDataOrFinWithoutCorruptingAccounting(bool fin, bool outsideCapacity)
    {
        await using var peer = new Peer();
        peer.ReceiveData(102);
        byte[] payload = fin ? [] : [0, 1, 2, 3, 4, 5, 6, 7];
        if (fin)
        {
            peer.Stream.Close();
        }
        else
        {
            await peer.Stream.WriteAsync(payload, TestContext.Current.CancellationToken);
        }
        MessageType type = fin ? MessageType.ST_FIN : MessageType.ST_DATA;
        byte[] original = peer.Last(type);
        Assert.Equal(4, Mask(original).Length);
        peer.ReceiveData(101, acknowledgeWrites: false); // Drains 102; nothing left to SACK.
        if (outsideCapacity)
        {
            peer.ReceiveData(150, acknowledgeWrites: false);
            Assert.Equal(8, Mask(peer.Last(MessageType.ST_STATE)).Length);
        }
        peer.Retry();
        byte[] retry = peer.Last(type);

        Assert.Empty(Mask(retry));
        Assert.Equal((ushort)102, Read(retry, 18));
        Assert.Equal(original.Length - 6, retry.Length);
        Assert.Equal(payload, Payload(retry));
        Assert.Equal(retry.Length, Get(peer.Stream, "_sentBytesUnacked"));
        peer.Retry(); // Removing the header twice would eat payload and subtract bytes twice.
        Assert.Equal(retry.Length, peer.Last(type).Length);
        Assert.Equal(payload, Payload(peer.Last(type)));
        peer.ReceiveAck(Read(retry, 16));
        Assert.Equal(0, peer.Stream.SentPacketsCount);
        Assert.Equal(0, Get(peer.Stream, "_sentBytesUnacked"));
    }

    [Fact(Timeout = 30000)]
    public async Task LostDataRemainsRecoverableAfterOppositeDirectionSackIsRetransmitted()
    {
        await using var a = new Peer();
        await using var b = new Peer(seq: 101, ack: 499);
        byte[] expected = Enumerable.Range(101, 8).Select(i => (byte)i).ToArray();
        foreach (byte value in expected)
        {
            await b.Stream.WriteAsync(new byte[] { value }, TestContext.Current.CancellationToken);
        }
        var sent = b.Packets(MessageType.ST_DATA).ToDictionary(p => Read(p, 16));
        a.Receive(sent[102]);
        byte[] request = [0x13, 0x42, 0x69];
        await a.Stream.WriteAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, Mask(a.Last(MessageType.ST_DATA)));
        // Lose A's DATA 500 and its pure ACKs, while B's data continues to arrive.
        foreach (ushort seq in new ushort[] { 101, 103, 104 }) a.Receive(sent[seq]);
        a.Retry();
        b.Receive(a.Last(MessageType.ST_DATA));
        Assert.Equal((ushort)500, Read(b.Last(MessageType.ST_STATE), 18));
        a.Receive(b.Last(MessageType.ST_STATE));
        byte[] receivedRequest = new byte[request.Length];
        await b.Stream.ReadExactlyAsync(receivedRequest, TestContext.Current.CancellationToken);
        Assert.Equal(request, receivedRequest);

        // Before the fix the old bit for 102 was rebased to 106 and deleted B's only copy.
        Assert.True(((IDictionary)Get(b.Stream, "_sentPackets")).Contains((ushort)106));
        foreach (ushort seq in new ushort[] { 105, 107, 108 }) a.Receive(sent[seq]);
        Assert.Equal((ushort)105, a.Stream.AckNr);
        b.Receive(a.Last(MessageType.ST_STATE));
        b.Retry();
        byte[] recovery = b.Last(MessageType.ST_DATA);
        Assert.Equal((ushort)106, Read(recovery, 16));
        a.Receive(recovery);
        Assert.Equal((ushort)108, a.Stream.AckNr);
        byte[] received = new byte[expected.Length];
        await a.Stream.ReadExactlyAsync(received, TestContext.Current.CancellationToken);
        Assert.Equal(expected, received);
        b.Receive(a.Last(MessageType.ST_STATE));
        Assert.Equal(0, b.Stream.SentPacketsCount);
        Assert.Equal(0, Get(b.Stream, "_sentBytesUnacked"));
    }

    private static byte[] Mask(byte[] packet)
    {
        if (packet[1] == 0) return [];
        Assert.Equal(1, packet[1]);
        Assert.Equal(0, packet[20]);
        Assert.InRange(packet[21], 4, 32);
        Assert.Equal(0, packet[21] % 4);
        return packet.AsSpan(22, packet[21]).ToArray();
    }

    private static byte[] Payload(byte[] packet) => packet[(packet[1] == 0 ? 20 : 22 + packet[21])..];
    private static ushort Read(byte[] packet, int offset) => UtpManager.ReadUInt16BigEndian(packet, offset);
    private static object Get(UtpStream stream, string field) => typeof(UtpStream)
        .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(stream)!;
    private static void Set(UtpStream stream, string field, object value) => typeof(UtpStream)
        .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(stream, value);

    private sealed class Peer : IAsyncDisposable
    {
        private readonly FakeTimeProvider _time = new();
        private readonly MockUdpListener _listener = new();
        private readonly UtpManager _manager;
        private readonly IPEndPoint _remote;
        private readonly ushort _initialAck;

        public Peer(ushort seq = 500, ushort ack = 100, bool ipv6 = false)
        {
            _remote = new IPEndPoint(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, 54321);
            _manager = new UtpManager(_time);
            _manager.Start(_listener);
            Stream = _manager.CreateStream(_remote);
            Set(Stream, "_state", UtpState.Connected);
            Set(Stream, "_seqNr", seq);
            Set(Stream, "_ackNr", ack);
            Set(Stream, "_cwnd", 1024d * 1024);
            _initialAck = (ushort)(seq - 1);
        }

        public UtpStream Stream { get; }
        public byte[][] Packets(MessageType type) => _listener.SentPackets
            .Where(p => p.Data[0] >> 4 == (byte)type).Select(p => p.Data).ToArray();
        public byte[] Last(MessageType type) => Packets(type).Last();

        public void Receive(byte[] packet)
        {
            packet = (byte[])packet.Clone();
            UtpManager.WriteUInt16BigEndian(packet, 2, Stream.ConnectionIdRecv);
            _manager.Receive(packet, _remote);
        }

        public void ReceiveData(ushort seq, bool acknowledgeWrites = true) => Receive(Packet(
            MessageType.ST_DATA, seq, acknowledgeWrites ? (ushort)(Stream.SeqNr - 1) : _initialAck, [1]));
        public void ReceiveAck(ushort ack) => Receive(Packet(MessageType.ST_STATE, (ushort)(Stream.AckNr + 1), ack, []));
        public void Retry() => _time.Advance(TimeSpan.FromSeconds(10));
        public void Advance(TimeSpan elapsed) => _time.Advance(elapsed);

        public async ValueTask DisposeAsync()
        {
            Stream.ProcessPacketWithSack(new MessageHeader
            {
                TypeVer = 0x31,
                AckNr = (ushort)(Stream.SeqNr - 1)
            }, [], 0, null, null, _remote);
            await Stream.DisposeAsync();
            await _manager.DisposeAsync();
        }

        private static byte[] Packet(MessageType type, ushort seq, ushort ack, byte[] payload)
        {
            byte[] packet = new byte[20 + payload.Length];
            packet[0] = (byte)(((byte)type << 4) | 1);
            UtpManager.WriteUInt16BigEndian(packet, 16, seq);
            UtpManager.WriteUInt16BigEndian(packet, 18, ack);
            UtpManager.WriteUInt32BigEndian(packet, 12, 65535);
            payload.CopyTo(packet, 20);
            return packet;
        }
    }
}
