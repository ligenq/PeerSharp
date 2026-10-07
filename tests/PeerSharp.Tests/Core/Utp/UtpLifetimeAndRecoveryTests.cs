using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Utp;
using System.Net;
using System.Reflection;

namespace PeerSharp.Tests.Core.Utp;

public class UtpLifetimeAndRecoveryTests
{
    private static readonly IPEndPoint Remote = new(IPAddress.Loopback, 12345);

    [Fact(Timeout = 10000)]
    public async Task IdleConnectionSendsKeepAliveAfterRetransmissionDeadlineExpires()
    {
        var (stream, manager, clock) = ConnectedStream();
        await using var cleanup = stream;
        manager.Packets.Clear();

        clock.Advance(TimeSpan.FromSeconds(30));
        stream.CheckTimeout();

        Assert.Equal(MessageType.ST_STATE, Type(Assert.Single(manager.Packets)));
        Assert.Equal(UtpState.Connected, stream.State);
        Reset(stream);
    }

    [Fact(Timeout = 10000)]
    public async Task ZeroWindowProbesAfterDeadlineAndResumesWriteOnWindowUpdate()
    {
        var (stream, manager, clock) = ConnectedStream();
        await using var cleanup = stream;
        Receive(stream, MessageType.ST_STATE, seq: 41, ack: 0, window: 0);
        manager.Packets.Clear();
        var write = stream.WriteAsync(new byte[] { 0x42 }, TestContext.Current.CancellationToken).AsTask();
        Assert.False(write.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(6));
        stream.CheckTimeout();

        Assert.Equal(MessageType.ST_STATE, Type(Assert.Single(manager.Packets)));
        Assert.False(write.IsCompleted);
        Receive(stream, MessageType.ST_STATE, seq: 41, ack: 0);
        await write.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Contains(manager.Packets, p => Type(p) == MessageType.ST_DATA && p[20] == 0x42);
        Reset(stream);
    }

    [Theory(Timeout = 10000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposedStreamRetriesFinAndTimesOutWhenPeerDisappears(bool asynchronously)
    {
        var (stream, manager, clock) = ConnectedStream();
        manager.Packets.Clear();
        if (asynchronously)
        {
            await stream.DisposeAsync();
        }
        else
        {
            stream.Dispose();
        }
        byte[] fin = Assert.Single(manager.Packets);
        Assert.Equal(MessageType.ST_FIN, Type(fin));

        clock.Advance(TimeSpan.FromSeconds(2));
        stream.CheckTimeout();

        Assert.Equal(2, manager.Packets.Count);
        Assert.Equal(Sequence(fin), Sequence(manager.Packets[1]));
        Assert.Equal(MessageType.ST_FIN, Type(manager.Packets[1]));
        clock.Advance(TimeSpan.FromMinutes(3));
        stream.CheckTimeout();
        Assert.Equal(UtpState.Closed, stream.State);
        Assert.Equal(0, stream.SentPacketsCount);
        Assert.True(manager.Removed);
    }

    [Fact(Timeout = 10000)]
    public async Task DisposedStreamAckClocksRemainingDataAndFinRetries()
    {
        var (stream, manager, clock) = ConnectedStream();
        await stream.WriteAsync(new byte[] { 1 }, TestContext.Current.CancellationToken);
        await stream.WriteAsync(new byte[] { 2 }, TestContext.Current.CancellationToken);
        await stream.DisposeAsync();
        var original = manager.Packets.Where(p => Type(p) != MessageType.ST_STATE).ToArray();
        Assert.Equal(3, original.Length);
        manager.Packets.Clear();

        clock.Advance(TimeSpan.FromSeconds(2));
        stream.CheckTimeout();
        Assert.Equal(Sequence(original[0]), Sequence(Assert.Single(manager.Packets)));
        Receive(stream, MessageType.ST_STATE, seq: 41, ack: Sequence(original[0]));

        Assert.Equal(original.Select(Sequence), manager.Packets.Select(Sequence));
        Reset(stream);
    }

    [Fact(Timeout = 10000)]
    public async Task DisposedStreamCompletesGracefulCloseOnPeerFin()
    {
        var (stream, manager, _) = ConnectedStream();
        await stream.DisposeAsync();
        byte[] fin = manager.Packets.Last();

        Receive(stream, MessageType.ST_FIN, seq: 41, ack: Sequence(fin));

        Assert.Equal(UtpState.Closed, stream.State);
        Assert.Equal(0, stream.SentPacketsCount);
        Assert.True(manager.Removed);
    }

    [Fact(Timeout = 10000)]
    public async Task PendingSendBytesStayUnchangedWhenRetryUpdatesAck()
    {
        var (stream, manager, clock) = ConnectedStream();
        await using var cleanup = stream;
        manager.DelayData = true;
        await stream.WriteAsync(new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);
        var original = Assert.Single(manager.Pending);
        byte[] originalBytes = original.Memory.ToArray();

        // Reverse-direction data advances our ACK while the first send is still pending.
        Receive(stream, MessageType.ST_DATA, seq: 41, ack: (ushort)(stream.SeqNr - 2), payload: [0x42]);
        clock.Advance(TimeSpan.FromSeconds(2));
        stream.CheckTimeout();

        Assert.Equal(2, manager.Pending.Count);
        Assert.Equal(originalBytes, original.Memory.ToArray());
        Assert.Equal((ushort)41, UtpManager.ReadUInt16BigEndian(manager.Pending[1].Memory.ToArray(), 18));
        Assert.Equal(originalBytes[20..], manager.Pending[1].Memory.ToArray()[20..]);
        Reset(stream);
        await manager.CompletePendingAsync();
    }

    [Fact(Timeout = 10000)]
    public async Task AckOfOriginalDoesNotRecyclePendingRetryStorage()
    {
        var (stream, manager, clock) = ConnectedStream();
        await using var cleanup = stream;
        await stream.WriteAsync(new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);
        ushort seq = Sequence(manager.Packets.Last());
        manager.DelayData = true;
        clock.Advance(TimeSpan.FromSeconds(2));
        stream.CheckTimeout();
        var retry = Assert.Single(manager.Pending);
        byte[] retryBytes = retry.Memory.ToArray();

        Receive(stream, MessageType.ST_STATE, seq: 41, ack: seq);
        await stream.WriteAsync(new byte[] { 4, 5, 6 }, TestContext.Current.CancellationToken);

        Assert.Equal(retryBytes, retry.Memory.ToArray());
        Reset(stream);
        Assert.Equal(retryBytes, retry.Memory.ToArray());
        await manager.CompletePendingAsync();
    }

    [Theory(Timeout = 10000)]
    [InlineData(100)]
    [InlineData(65535)]
    public async Task ManagerRepliesToDuplicateSynWithoutAcceptingAnotherStream(int connectionId)
    {
        await using var manager = new UtpManager(new FakeTimeProvider());
        var listener = new MockUdpListener();
        manager.Start(listener);
        var accepted = new List<UtpStream>();
        manager.OnNewConnection = accepted.Add;
        byte[] syn = Packet(MessageType.ST_SYN, (ushort)connectionId, seq: 40, ack: 0);
        manager.Receive(syn, Remote);
        byte[] firstReply = Assert.Single(listener.SentPackets).Data;
        listener.SentPackets.Clear();

        manager.Receive(syn, Remote);

        var stream = Assert.Single(accepted);
        byte[] reply = Assert.Single(listener.SentPackets).Data;
        Assert.Equal(MessageType.ST_STATE, Type(reply));
        Assert.Equal((ushort)connectionId, UtpManager.ReadUInt16BigEndian(reply, 2));
        Assert.Equal((ushort)40, UtpManager.ReadUInt16BigEndian(reply, 18));
        Assert.Equal(Sequence(firstReply), Sequence(reply));
        Assert.Equal(UtpState.SynRecv, stream.State);
        Reset(stream);
    }

    [Fact(Timeout = 10000)]
    public async Task DuplicateSynDoesNotApplyUndefinedAckToOutstandingData()
    {
        await using var manager = new UtpManager(new FakeTimeProvider());
        var listener = new MockUdpListener();
        manager.Start(listener);
        UtpStream? accepted = null;
        manager.OnNewConnection = s => accepted = s;
        byte[] syn = Packet(MessageType.ST_SYN, 100, seq: 40, ack: 0);
        manager.Receive(syn, Remote);
        var stream = Assert.IsType<UtpStream>(accepted);
        typeof(UtpStream).GetField("_seqNr", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(stream, (ushort)500);
        manager.Receive(Packet(MessageType.ST_STATE, 101, seq: 41, ack: 499), Remote);
        await stream.WriteAsync(new byte[] { 0x42 }, TestContext.Current.CancellationToken);
        listener.SentPackets.Clear();

        manager.Receive(syn, Remote);

        Assert.Equal(MessageType.ST_STATE, Type(Assert.Single(listener.SentPackets).Data));
        Assert.Equal(1, stream.SentPacketsCount);
        Assert.Equal(UtpState.Connected, stream.State);
        Reset(stream);
    }

    private static (UtpStream Stream, RecordingManager Manager, FakeTimeProvider Clock) ConnectedStream()
    {
        var clock = new FakeTimeProvider();
        var manager = new RecordingManager();
        var stream = new UtpStream(manager, Remote, 100, 101, clock);
        Receive(stream, MessageType.ST_SYN, seq: 40, ack: 0);
        Receive(stream, MessageType.ST_STATE, seq: 41, ack: (ushort)(stream.SeqNr - 1));
        return (stream, manager, clock);
    }

    private static void Reset(UtpStream stream) =>
        Receive(stream, MessageType.ST_RESET, seq: 41, ack: (ushort)(stream.SeqNr - 1));

    private static void Receive(UtpStream stream, MessageType type, ushort seq, ushort ack, uint window = 100000, byte[]? payload = null)
    {
        var packet = Packet(type, stream.ConnectionIdRecv, seq, ack, window, payload);
        var header = new MessageHeader
        {
            TypeVer = packet[0],
            ConnectionId = stream.ConnectionIdRecv,
            SeqNr = seq,
            AckNr = ack,
            WndSize = window
        };
        stream.ProcessPacketWithSack(header, packet, 20, null, null, Remote);
    }

    private static byte[] Packet(MessageType type, ushort connectionId, ushort seq, ushort ack, uint window = 100000, byte[]? payload = null)
    {
        byte[] packet = new byte[20 + (payload?.Length ?? 0)];
        packet[0] = (byte)(((byte)type << 4) | 1);
        UtpManager.WriteUInt16BigEndian(packet, 2, connectionId);
        UtpManager.WriteUInt32BigEndian(packet, 12, window);
        UtpManager.WriteUInt16BigEndian(packet, 16, seq);
        UtpManager.WriteUInt16BigEndian(packet, 18, ack);
        payload?.CopyTo(packet, 20);
        return packet;
    }

    private static MessageType Type(byte[] packet) => (MessageType)(packet[0] >> 4);
    private static ushort Sequence(byte[] packet) => UtpManager.ReadUInt16BigEndian(packet, 16);

    private sealed class RecordingManager : IUtpManager
    {
        public Action<UtpStream>? OnNewConnection { get; set; }
        public List<byte[]> Packets { get; } = [];
        public List<(ReadOnlyMemory<byte> Memory, TaskCompletionSource Completion)> Pending { get; } = [];
        public bool DelayData { get; set; }
        public bool Removed { get; private set; }

        public Task SendAsync(ReadOnlyMemory<byte> packet, IPEndPoint remote, CancellationToken ct)
        {
            Packets.Add(packet.ToArray());
            if (DelayData && (MessageType)(packet.Span[0] >> 4) == MessageType.ST_DATA)
            {
                var completion = new TaskCompletionSource();
                Pending.Add((packet, completion));
                return completion.Task;
            }
            return Task.CompletedTask;
        }

        public Task CompletePendingAsync()
        {
            foreach (var (_, completion) in Pending)
            {
                completion.SetResult();
            }
            return Task.WhenAll(Pending.Select(p => p.Completion.Task));
        }

        public void CloseStream(UtpStream stream) => Removed = true;
        public UtpStream CreateStream(IPEndPoint remote) => throw new NotSupportedException();
        public void Start(IUdpListener listener) { }
        public void Stop() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
