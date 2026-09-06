using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals.Utp;
using System.Collections;
using System.Net;
using System.Reflection;

namespace PeerSharp.Tests.Core.Utp;

public class UtpAckWindowTests
{
    [Theory(Timeout = 30000)]
    [InlineData(100)]
    [InlineData(40000)]
    [InlineData(65530)]
    public async Task RepeatedSackAndHoleRecoveryRemainValidAfterSelectiveRemoval(int first)
    {
        await using var peer = new Sender((ushort)first);
        await peer.Send(10);
        ushort ack = (ushort)(first - 1);
        // Keep first and first+1 missing, acknowledge the other eight packets.
        byte[] mask = [0xFE, 0x01, 0, 0];
        peer.Receive(ack, mask);
        Assert.Equal(2, peer.Stream.SentPacketsCount);
        Assert.True(peer.Resent((ushort)first));
        Assert.True(peer.Resent((ushort)(first + 1)));

        // A second report still refers to the same sequence span, even though only two
        // buffers remain. Its DATA payload must be delivered as well as its ACK processed.
        peer.Receive(ack, mask, payload: [0x42], window: 12345);
        Assert.Equal(12345u, Get(peer.Stream, "_wndSize"));
        Assert.Equal((ushort)1000, peer.Stream.AckNr);
        byte[] received = new byte[1];
        await peer.Stream.ReadExactlyAsync(received, TestContext.Current.CancellationToken);
        Assert.Equal(0x42, received[0]);

        peer.Receive((ushort)(first + 1));
        Assert.Equal(0, peer.Stream.SentPacketsCount);
        Assert.Equal(0, Get(peer.Stream, "_sentBytesUnacked"));
    }

    [Theory(Timeout = 30000)]
    [InlineData(100)]
    [InlineData(65530)]
    public async Task CumulativeAckCrossesSelectivelyRemovedPacketsAndReleasesTheTail(int first)
    {
        await using var peer = new Sender((ushort)first);
        await peer.Send(20);
        // first, first+1 and first+19 remain; the seventeen in between were SACKed.
        peer.Receive((ushort)(first - 1), [0xFE, 0xFF, 0x03, 0]);
        Assert.Equal(3, peer.Stream.SentPacketsCount);
        peer.Receive((ushort)(first + 19));
        Assert.Equal(0, peer.Stream.SentPacketsCount);
        Assert.Equal(0, Get(peer.Stream, "_sentBytesUnacked"));
    }

    [Theory(Timeout = 30000)]
    [InlineData(100)]
    [InlineData(65530)]
    public async Task SparseWindowStillRejectsFutureAndStaleAcknowledgements(int first)
    {
        await using var peer = new Sender((ushort)first);
        await peer.Send(10);
        peer.Receive((ushort)(first - 1), [0xFE, 1, 0, 0]);
        peer.Receive((ushort)(first + 10), window: 12345); // Never sent.
        Assert.Equal(65535u, Get(peer.Stream, "_wndSize"));
        peer.Receive((ushort)(first - 5), window: 12345); // Before the allowed old-ACK margin.
        Assert.Equal(65535u, Get(peer.Stream, "_wndSize"));
        Assert.Equal(2, peer.Stream.SentPacketsCount);
    }

    [Fact(Timeout = 30000)]
    public async Task DataRepeatingAnAckIsNotLossButStateDuplicatesStillAre()
    {
        await using var peer = new Sender(100);
        await peer.Send(2);
        double before = (double)Get(peer.Stream, "_cwnd");
        for (int i = 0; i < 6; i++) peer.Receive(99, payload: [1]);
        Assert.Equal(0, Get(peer.Stream, "_duplicateAckCount"));
        Assert.False(peer.Resent(100));
        Assert.Equal(before, (double)Get(peer.Stream, "_cwnd"));

        for (int i = 0; i < 3; i++) peer.Receive(99);
        Assert.True(peer.Resent(100));
        Assert.True((double)Get(peer.Stream, "_cwnd") < before);
    }

    [Fact(Timeout = 30000)]
    public async Task SackOnDataStillDetectsLossWithoutCountingTheDataAsADuplicate()
    {
        await using var peer = new Sender(100);
        await peer.Send(10);
        peer.Receive(99, [0xFE, 1, 0, 0], payload: [1]);
        Assert.True(peer.Resent(100));
        Assert.Equal(0, Get(peer.Stream, "_duplicateAckCount"));
    }

    private static object Get(UtpStream stream, string name) => typeof(UtpStream)
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(stream)!;
    private static void Set(UtpStream stream, string name, object value) => typeof(UtpStream)
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(stream, value);

    private sealed class Sender : IAsyncDisposable
    {
        private readonly UtpManager _manager;
        private readonly IPEndPoint _remote = new(IPAddress.Loopback, 54321);
        private ushort _incomingSeq = 1000;
        public UtpStream Stream { get; }

        public Sender(ushort first)
        {
            _manager = new UtpManager(new FakeTimeProvider());
            _manager.Start(new MockUdpListener());
            Stream = _manager.CreateStream(_remote);
            Set(Stream, "_state", UtpState.Connected);
            Set(Stream, "_seqNr", first);
            Set(Stream, "_ackNr", (ushort)999);
            Set(Stream, "_lastAckedSeq", (ushort)(first - 1));
            Set(Stream, "_cwnd", 100000d);
        }

        public async Task Send(int count)
        {
            for (int i = 0; i < count; i++)
                await Stream.WriteAsync(new byte[] { (byte)i }, TestContext.Current.CancellationToken);
        }

        public bool Resent(ushort seq)
        {
            object packet = ((IDictionary)Get(Stream, "_sentPackets"))[seq]!;
            return (bool)packet.GetType().GetProperty("Resent")!.GetValue(packet)!;
        }

        public void Receive(ushort ack, byte[]? mask = null, byte[]? payload = null, uint window = 65535)
        {
            int headerLength = 20 + (mask == null ? 0 : 2 + mask.Length);
            byte[] data = new byte[headerLength + (payload?.Length ?? 0)];
            data[0] = payload == null ? (byte)0x21 : (byte)0x01;
            UtpManager.WriteUInt16BigEndian(data, 2, Stream.ConnectionIdRecv);
            UtpManager.WriteUInt32BigEndian(data, 12, window);
            UtpManager.WriteUInt16BigEndian(data, 16, _incomingSeq);
            UtpManager.WriteUInt16BigEndian(data, 18, ack);
            if (mask != null)
            {
                data[1] = 1;
                data[21] = (byte)mask.Length;
                mask.CopyTo(data, 22);
            }
            if (payload != null)
            {
                payload.CopyTo(data, headerLength);
                _incomingSeq++;
            }
            _manager.Receive(data, _remote);
        }

        public async ValueTask DisposeAsync()
        {
            Stream.ProcessPacketWithSack(new MessageHeader { TypeVer = 0x31, AckNr = (ushort)(Stream.SeqNr - 1) },
                [], 0, null, null, _remote);
            await Stream.DisposeAsync();
            await _manager.DisposeAsync();
        }
    }
}
