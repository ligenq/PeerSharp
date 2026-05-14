using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Utp;
using System.Net;

namespace PeerSharp.Tests.Core.Utp;

public class UtpStreamStateTests
{
    private class MockUtpManager : IUtpManager
    {
        public Action<UtpStream>? OnNewConnection { get; set; }

        public List<byte[]> SentPackets { get; } = [];
        public int SendCallCount => SentPackets.Count;

        public bool CloseStreamCalled { get; private set; }
        public int CloseStreamCallCount { get; private set; }

        public Task SendAsync(ReadOnlyMemory<byte> packet, IPEndPoint remote, CancellationToken ct)
        {
            SentPackets.Add(packet.ToArray());
            return Task.CompletedTask;
        }

        public void CloseStream(UtpStream stream)
        {
            CloseStreamCalled = true;
            CloseStreamCallCount++;
        }

        public UtpStream CreateStream(IPEndPoint remote) => throw new NotImplementedException();
        public void Start(IUdpListener listener) => throw new NotImplementedException();
        public void Stop() => throw new NotImplementedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static MessageHeader MakeHeader(MessageType type, ushort seqNr = 1, ushort ackNr = 0)
    {
        return new MessageHeader
        {
            TypeVer = (byte)((byte)type << 4 | MessageHeader.CurrentVersion),
            WndSize = 65535,
            SeqNr = seqNr,
            AckNr = ackNr
        };
    }

    private static (UtpStream stream, MockUtpManager manager, FakeTimeProvider time) CreateStream()
    {
        var manager = new MockUtpManager();
        var time = new FakeTimeProvider();
        var remote = new IPEndPoint(IPAddress.Loopback, 12345);
        var stream = new UtpStream(manager, remote, idRecv: 100, idSend: 101, timeProvider: time);
        return (stream, manager, time);
    }

    private static IPEndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 12345);
    private static IPEndPoint OtherEndPoint => new IPEndPoint(IPAddress.Loopback, 54321);

    // --- State machine tests ---

    [Fact]
    public void ProcessPacketWithSack_SynInNoneState_TransitionsToSynRecv()
    {
        var (stream, _, _) = CreateStream();
        var header = MakeHeader(MessageType.ST_SYN, seqNr: 1, ackNr: 0);

        stream.ProcessPacketWithSack(header, Array.Empty<byte>(), 0, null, null, RemoteEndPoint);

        Assert.Equal(UtpState.SynRecv, stream.State);
    }

    [Fact]
    public void ProcessPacketWithSack_SynInNoneState_SendsStateAck()
    {
        var (stream, manager, _) = CreateStream();
        var header = MakeHeader(MessageType.ST_SYN, seqNr: 1, ackNr: 0);

        stream.ProcessPacketWithSack(header, Array.Empty<byte>(), 0, null, null, RemoteEndPoint);

        Assert.True(manager.SendCallCount >= 1);
    }

    [Fact]
    public void ProcessPacketWithSack_DuplicateSynInSynRecv_StaysSynRecv()
    {
        var (stream, manager, _) = CreateStream();

        // First SYN transitions to SynRecv
        var syn1 = MakeHeader(MessageType.ST_SYN, seqNr: 42, ackNr: 0);
        stream.ProcessPacketWithSack(syn1, Array.Empty<byte>(), 0, null, null, RemoteEndPoint);

        Assert.Equal(UtpState.SynRecv, stream.State);
        int sendCountAfterFirst = manager.SendCallCount;

        // Duplicate SYN with same SeqNr (= stream.AckNr after first SYN)
        var syn2 = MakeHeader(MessageType.ST_SYN, seqNr: stream.AckNr, ackNr: 0);
        stream.ProcessPacketWithSack(syn2, Array.Empty<byte>(), 0, null, null, RemoteEndPoint);

        Assert.Equal(UtpState.SynRecv, stream.State);
        Assert.True(manager.SendCallCount > sendCountAfterFirst, "Expected SendAsync to be called again for duplicate SYN");
    }

    [Fact]
    public void ProcessPacketWithSack_PacketFromWrongEndpoint_Ignored()
    {
        var (stream, _, _) = CreateStream();
        var header = MakeHeader(MessageType.ST_SYN, seqNr: 1, ackNr: 0);

        stream.ProcessPacketWithSack(header, Array.Empty<byte>(), 0, null, null, OtherEndPoint);

        Assert.Equal(UtpState.None, stream.State);
    }

    [Fact]
    public async Task ProcessPacketWithSack_StateInSynSend_TransitionsToConnected()
    {
        var (stream, _, _) = CreateStream();

        // Start connecting (don't await yet)
        var connectTask = stream.ConnectAsync();

        // AckNr = seqNr - 1 equals lastSent (the SYN packet seq)
        ushort ackNr = (ushort)(stream.SeqNr - 1);
        var stateHeader = MakeHeader(MessageType.ST_STATE, seqNr: 10, ackNr: ackNr);
        stream.ProcessPacketWithSack(stateHeader, Array.Empty<byte>(), 0, null, null, RemoteEndPoint);

        await connectTask;

        Assert.Equal(UtpState.Connected, stream.State);
    }

    [Fact]
    public async Task ProcessPacketWithSack_ResetInSynSend_ClosesConnection()
    {
        var (stream, _, _) = CreateStream();

        var connectTask = stream.ConnectAsync();

        ushort ackNr = (ushort)(stream.SeqNr - 1);
        var resetHeader = MakeHeader(MessageType.ST_RESET, seqNr: 1, ackNr: ackNr);
        stream.ProcessPacketWithSack(resetHeader, Array.Empty<byte>(), 0, null, null, RemoteEndPoint);

        Assert.Equal(UtpState.Closed, stream.State);

        var ex = await Assert.ThrowsAsync<IOException>(() => connectTask);
        Assert.NotNull(ex);
    }

    [Fact]
    public void ProcessPacketWithSack_StateInSynRecv_TransitionsToConnected()
    {
        var (stream, _, _) = CreateStream();

        // First get to SynRecv via ST_SYN
        var syn = MakeHeader(MessageType.ST_SYN, seqNr: 5, ackNr: 0);
        stream.ProcessPacketWithSack(syn, Array.Empty<byte>(), 0, null, null, RemoteEndPoint);
        Assert.Equal(UtpState.SynRecv, stream.State);

        // SentPacketsCount == 0 in SynRecv (ST_STATE is not a reliability packet), so any AckNr is valid
        var stateHeader = MakeHeader(MessageType.ST_STATE, seqNr: 20, ackNr: 0);
        stream.ProcessPacketWithSack(stateHeader, Array.Empty<byte>(), 0, null, null, RemoteEndPoint);

        Assert.Equal(UtpState.Connected, stream.State);
    }

    [Fact]
    public async Task ProcessPacketWithSack_ResetInConnected_ClosesConnection()
    {
        var (stream, _, _) = CreateStream();

        // Get to SynRecv
        var syn = MakeHeader(MessageType.ST_SYN, seqNr: 5, ackNr: 0);
        stream.ProcessPacketWithSack(syn, Array.Empty<byte>(), 0, null, null, RemoteEndPoint);

        // Get to Connected
        var stateHeader = MakeHeader(MessageType.ST_STATE, seqNr: 20, ackNr: 0);
        stream.ProcessPacketWithSack(stateHeader, Array.Empty<byte>(), 0, null, null, RemoteEndPoint);
        Assert.Equal(UtpState.Connected, stream.State);

        // Now send RESET
        var resetHeader = MakeHeader(MessageType.ST_RESET, seqNr: 1, ackNr: 0);
        stream.ProcessPacketWithSack(resetHeader, Array.Empty<byte>(), 0, null, null, RemoteEndPoint);

        Assert.Equal(UtpState.Closed, stream.State);

        // Allow the stream to be disposed cleanly
        await stream.DisposeAsync();
    }

    // --- CheckTimeout tests ---

    [Fact]
    public async Task CheckTimeout_SynSendBeforeRetries_ResendsSyn()
    {
        var (stream, manager, time) = CreateStream();

        var connectTask = stream.ConnectAsync();
        int sendCountAfterConnect = manager.SendCallCount;

        // Advance past the initial 3s timeout
        time.Advance(TimeSpan.FromSeconds(4));
        stream.CheckTimeout();

        Assert.True(manager.SendCallCount > sendCountAfterConnect, "Expected SYN to be resent after timeout");

        // Clean up: send a state response to complete the connect task
        ushort ackNr = (ushort)(stream.SeqNr - 1);
        var stateHeader = MakeHeader(MessageType.ST_STATE, seqNr: 10, ackNr: ackNr);
        stream.ProcessPacketWithSack(stateHeader, Array.Empty<byte>(), 0, null, null, RemoteEndPoint);
        await connectTask;
    }

    [Fact]
    public async Task CheckTimeout_SynSendExceedsMaxRetries_ClosesConnection()
    {
        // MaxSynRetries = 2, so after 3 timeout triggers the connection closes
        var (stream, _, time) = CreateStream();

        var connectTask = stream.ConnectAsync();

        // Trigger timeout MaxSynRetries+1 = 3 times, advancing time past _nextTimeout each time
        for (int i = 0; i <= 2; i++)
        {
            time.Advance(TimeSpan.FromSeconds(31)); // well past any _nextTimeout
            stream.CheckTimeout();
        }

        Assert.Equal(UtpState.Closed, stream.State);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => connectTask);
        Assert.NotNull(ex);
    }

    [Fact]
    public async Task CheckTimeout_InactivityTimeout_ClosesConnection()
    {
        var (stream, _, time) = CreateStream();

        // Get to Connected state via SYN->SynRecv->Connected
        var syn = MakeHeader(MessageType.ST_SYN, seqNr: 5, ackNr: 0);
        stream.ProcessPacketWithSack(syn, Array.Empty<byte>(), 0, null, null, RemoteEndPoint);

        var stateHeader = MakeHeader(MessageType.ST_STATE, seqNr: 20, ackNr: 0);
        stream.ProcessPacketWithSack(stateHeader, Array.Empty<byte>(), 0, null, null, RemoteEndPoint);
        Assert.Equal(UtpState.Connected, stream.State);

        // Advance time past 60s inactivity threshold without any received packets
        time.Advance(TimeSpan.FromSeconds(61));
        stream.CheckTimeout();

        Assert.Equal(UtpState.Closed, stream.State);

        await stream.DisposeAsync();
    }

    // --- Utils tests ---

    [Fact]
    public void Utils_CompareSeq_ZeroWhenEqual()
    {
        Assert.Equal(0, Utils.CompareSeq(5, 5));
    }

    [Fact]
    public void Utils_CompareSeq_NegativeWhenLess()
    {
        Assert.True(Utils.CompareSeq(3, 5) < 0);
    }

    [Fact]
    public void Utils_CompareSeq_PositiveWhenGreater()
    {
        Assert.True(Utils.CompareSeq(7, 5) > 0);
    }

    [Fact]
    public void Utils_CompareSeq_WraparoundHandled()
    {
        // 1 is "after" 65535 in circular sequence space
        Assert.True(Utils.CompareSeq(1, 65535) > 0);
    }
}
