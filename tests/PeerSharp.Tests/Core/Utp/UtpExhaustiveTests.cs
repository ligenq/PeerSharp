using System.Net;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Utp;
using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Time.Testing;

namespace PeerSharp.Tests.Core.Utp;

internal class MockUdpListener : IUdpListener, IDisposable
{
    public int Port { get; } = 12345;
    public IUdpReceiver? Receiver { get; private set; }
    public ConcurrentQueue<(byte[] Data, IPEndPoint Remote)> SentPackets { get; } = new();

    public void RegisterReceiver(IUdpReceiver receiver)
    {
        Receiver = receiver;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public void Stop() { }

    public Task StopAsync()
    {
        return Task.CompletedTask;
    }

    public Task SendAsync(ReadOnlyMemory<byte> data, IPEndPoint endpoint, CancellationToken ct = default)
    {
        SentPackets.Enqueue((data.ToArray(), endpoint));
        return Task.CompletedTask;
    }

    public void SimulateReceive(byte[] data, IPEndPoint sender)
    {
        Receiver?.Receive(data, sender);
    }

    public void Dispose() { }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

public class UtpExhaustiveTests
{
    private readonly FakeTimeProvider _time;
    private readonly MockUdpListener _listener;
    private readonly UtpManager _manager;
    private readonly IPEndPoint _remoteParams = new IPEndPoint(IPAddress.Loopback, 50000);

    public UtpExhaustiveTests()
    {
        _time = new FakeTimeProvider();
        _listener = new MockUdpListener();
        _manager = new UtpManager(_time);
        _manager.Start(_listener);
    }

    #region Utils Tests

    [Fact]
    public void TestCompareSeq()
    {
        Assert.Equal(0, Utils.CompareSeq(100, 100));
        Assert.Equal(-1, Utils.CompareSeq(100, 101));
        Assert.Equal(-1, Utils.CompareSeq(65535, 0));
        Assert.Equal(1, Utils.CompareSeq(101, 100));
        Assert.Equal(1, Utils.CompareSeq(0, 65535));
    }

    #endregion

    #region Connection Tests

    [Fact(Timeout = 30000)]
    public async Task TestOutgoingConnection_Handshake()
    {
        var stream = _manager.CreateStream(_remoteParams);
        var connectTask = stream.ConnectAsync();

        Assert.True(_listener.SentPackets.TryDequeue(out var synPacket));
        var synHeader = ParseHeader(synPacket.Data);
        Assert.Equal(4, synHeader.Type); // ST_SYN

        ushort ourRecvId = synHeader.ConnectionId;
        byte[] synAck = CreatePacket(2, 0, ourRecvId, (ushort)(synHeader.SeqNr + 1), synHeader.SeqNr);
        _listener.SimulateReceive(synAck, _remoteParams);

        await connectTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(stream.CanWrite);
    }

    [Fact(Timeout = 30000)]
    public async Task TestPacketLoss_Retransmission()
    {
        var stream = await ConnectStream();

        byte[] data = { 1, 2, 3, 4 };
        await stream.WriteAsync(data);

        Assert.True(_listener.SentPackets.TryDequeue(out var dataPacket));
        var header = ParseHeader(dataPacket.Data);
        Assert.Equal(0, header.Type); // ST_DATA

        _listener.SentPackets.Clear();

        // Advance time to trigger timeout (managed by UtpManager's timer via TimeProvider)
        _time.Advance(TimeSpan.FromSeconds(2));

        // Timeout check happens on timer tick, so this should trigger it.

        Assert.True(_listener.SentPackets.TryDequeue(out var resentPacket));
        var resendHeader = ParseHeader(resentPacket.Data);
        Assert.Equal(header.SeqNr, resendHeader.SeqNr);
        Assert.Equal(0, resendHeader.Type); // ST_DATA
    }

    [Fact(Timeout = 30000)]
    public async Task TestDuplicateAcks_ResendMultiple()
    {
        var stream = await ConnectStream();
        byte[] data = new byte[100];

        await stream.WriteAsync(data);
        await stream.WriteAsync(data);
        await stream.WriteAsync(data);

        Assert.True(_listener.SentPackets.TryDequeue(out var pkt1));
        Assert.True(_listener.SentPackets.TryDequeue(out var pkt2));
        Assert.True(_listener.SentPackets.TryDequeue(out var pkt3));
        _listener.SentPackets.Clear();

        var h1 = ParseHeader(pkt1.Data);
        byte[] ack = CreatePacket(2, 0, GetRecvId(stream), 1, h1.SeqNr);
        _listener.SimulateReceive(ack, _remoteParams);
        _listener.SentPackets.Clear();

        for (int i = 0; i < 3; i++)
        {
            _listener.SimulateReceive(ack, _remoteParams);
        }

        int resendCount = 0;
        while (_listener.SentPackets.TryDequeue(out _))
        {
            resendCount++;
        }

        Assert.True(resendCount >= 2, $"Expected multiple resends, got {resendCount}");
    }

    [Fact(Timeout = 30000)]
    public async Task TestOutOfOrder_Buffered()
    {
        var stream = await ConnectStream();
        ushort startSeq = GetNextSeq(stream);
        ushort ackNr = GetLastSentSeq(stream);

        // Send Seq+2 (Seq+1 missing)
        byte[] payload = { 0xBB };
        var packet2 = CreatePacket(0, 0, GetRecvId(stream), (ushort)(startSeq + 2), ackNr, payload);
        _listener.SimulateReceive(packet2, _remoteParams);

        byte[] buffer = new byte[10];
        var readTask = stream.ReadAsync(buffer).AsTask();

        await Task.Delay(100); // Real delay for task scheduling
        Assert.False(readTask.IsCompleted);

        // Send Seq+1
        byte[] payload1 = { 0xAA };
        var packet1 = CreatePacket(0, 0, GetRecvId(stream), (ushort)(startSeq + 1), ackNr, payload1);
        _listener.SimulateReceive(packet1, _remoteParams);

        // Await the first read task
        int totalRead = await readTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Read loop with timeout for remaining bytes
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (totalRead < 2)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead, 2 - totalRead), cts.Token);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }
        Assert.Equal(2, totalRead);
        Assert.Equal(0xAA, buffer[0]);
        Assert.Equal(0xBB, buffer[1]);
    }

    [Fact(Timeout = 30000)]
    public async Task TestSack_Generation()
    {
        var stream = await ConnectStream();
        ushort startSeq = GetNextSeq(stream);
        ushort connId = GetRecvId(stream);
        ushort ackNr = GetLastSentSeq(stream);

        var pkt1 = CreatePacket(0, 0, connId, (ushort)(startSeq + 1), ackNr, new byte[] { 1 });
        _listener.SimulateReceive(pkt1, _remoteParams);

        _listener.SentPackets.Clear();

        var pkt3 = CreatePacket(0, 0, connId, (ushort)(startSeq + 3), ackNr, new byte[] { 3 });
        _listener.SimulateReceive(pkt3, _remoteParams);

        Assert.True(_listener.SentPackets.TryDequeue(out var sackAck));
        var header = ParseHeader(sackAck.Data);
        Assert.Equal(2, header.Type); // ST_STATE
        Assert.Equal(startSeq + 1, header.AckNr);
        Assert.Equal(1, header.Extension);

        byte bitmask = sackAck.Data[22];
        Assert.Equal(1, bitmask & 1);
    }

    [Fact(Timeout = 30000)]
    public async Task TestExtensionBits_Stored()
    {
        var stream = await ConnectStream();
        ushort connId = GetRecvId(stream);
        ushort ackNr = GetLastSentSeq(stream);
        ushort seq = 1;

        byte[] extBits = new byte[] { 0x01, 0x02, 0x10, 0x20, 0x40, 0x80, 0xAA, 0x55 };
        var packet = CreatePacketWithExtensionBits(2, connId, seq, ackNr, extBits);
        _listener.SimulateReceive(packet, _remoteParams);

        var field = typeof(UtpStream).GetField("_peerExtensionBits", BindingFlags.NonPublic | BindingFlags.Instance);
        var stored = (byte[]?)field!.GetValue(stream);
        Assert.NotNull(stored);
        Assert.Equal(extBits, stored);
    }

    [Fact(Timeout = 30000)]
    public async Task TestDuplicatePacket_Ignored()
    {
        var stream = await ConnectStream();
        ushort startSeq = GetNextSeq(stream);
        ushort connId = GetRecvId(stream);
        ushort ackNr = GetLastSentSeq(stream);

        byte[] payload = { 0xAA };
        var packet = CreatePacket(0, 0, connId, (ushort)(startSeq + 1), ackNr, payload);

        _listener.SimulateReceive(packet, _remoteParams);
        _listener.SimulateReceive(packet, _remoteParams);

        byte[] buffer = new byte[10];
        int read = await stream.ReadAsync(buffer);
        Assert.Equal(1, read);
        Assert.Equal(0xAA, buffer[0]);

        var readTask = stream.ReadAsync(buffer).AsTask();
        await Task.Delay(100);
        Assert.False(readTask.IsCompleted);
    }

    [Fact(Timeout = 30000)]
    public async Task TestReset_ClosesStream()
    {
        var stream = await ConnectStream();
        ushort ackNr = GetLastSentSeq(stream);
        ushort sendId = GetSendId(stream);

        var resetPacket = CreatePacket(3, 0, sendId, 0, ackNr);
        _listener.SimulateReceive(resetPacket, _remoteParams);

        await Task.Delay(50);

        // Write should throw InvalidOperationException (State is Closed)
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await stream.WriteAsync(new byte[1]));

        // Read should throw IOException (Connection Reset)
        // Currently this fails (returns 0) until we fix UtpStream to propagate the error
        await Assert.ThrowsAsync<IOException>(async () => _ = await stream.ReadAsync(new byte[10]));
    }

    [Fact(Timeout = 30000)]
    public async Task TestFin_ClosesRead()
    {
        var stream = await ConnectStream();
        ushort startSeq = GetNextSeq(stream);
        ushort connId = GetRecvId(stream);
        ushort ackNr = GetLastSentSeq(stream);

        var finPacket = CreatePacket(1, 0, connId, (ushort)(startSeq + 1), ackNr);
        _listener.SimulateReceive(finPacket, _remoteParams);

        Assert.Equal(0, await stream.ReadAsync(new byte[10]));
    }

    [Fact(Timeout = 30000)]
    public async Task TestCongestionControl_DelayReducesWindow()
    {
        var stream = await ConnectStream();
        byte[] data = new byte[1000];

        // 1. Send Pkt1 with Low Delay (50ms) to establish baseline (baseDelay)
        await SendAndAck(stream, data, 50000);

        // 2, 3. Send Pkt2, Pkt3 with High Delay (200ms).
        // Current Delay history is size 3 (Min filter).
        // Hist: [50, 200, 200]. Min is still 50.
        await SendAndAck(stream, data, 200000);
        await SendAndAck(stream, data, 200000);

        // 4. Send Pkt4 with High Delay.
        // Hist: [200, 200, 200]. Min is 200.
        // ourDelay = 200 - 50 = 150.
        // 150 > 90 (Target*0.9). Should Exit Slow Start.
        await SendAndAck(stream, data, 200000);

        var ssField = typeof(UtpStream).GetField("_slowStart", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.False((bool)ssField!.GetValue(stream)!, "Should have exited slow start");

        // 5. Send Pkt5 with High Delay - Congestion Avoidance
        var cwndField = typeof(UtpStream).GetField("_cwnd", BindingFlags.NonPublic | BindingFlags.Instance);
        double cwndBefore = (double)cwndField!.GetValue(stream)!;

        await SendAndAck(stream, data, 200000);

        double cwndFinal = (double)cwndField.GetValue(stream)!;

        Assert.True(cwndFinal < cwndBefore, $"cwnd should decrease in CA. Before: {cwndBefore}, Final: {cwndFinal}");
    }

    private async Task SendAndAck(UtpStream stream, byte[] data, uint delayMicro)
    {
        await stream.WriteAsync(data);
        Assert.True(_listener.SentPackets.TryDequeue(out var pkt));
        var h = ParseHeader(pkt.Data);
        byte[] ack = CreatePacket(2, 0, GetRecvId(stream), (ushort)(h.SeqNr + 1), h.SeqNr);
        UtpManager.WriteUInt32BigEndian(ack, 8, delayMicro);
        _listener.SimulateReceive(ack, _remoteParams);
    }

    [Fact(Timeout = 30000)]
    public async Task TestFlowControl_RemoteWindowFull()
    {
        var stream = await ConnectStream();
        ushort connId = GetRecvId(stream);
        ushort nextSeq = GetNextSeq(stream);
        ushort ackNr = GetLastSentSeq(stream);

        // 1. Send ACK with 0 Window
        var ackZeroWnd = CreatePacket(2, 0, connId, nextSeq, ackNr);
        UtpManager.WriteUInt32BigEndian(ackZeroWnd, 12, 0); // Window Size = 0
        _listener.SimulateReceive(ackZeroWnd, _remoteParams);

        // 2. Write Data - Should Block or buffer without sending
        // The WriteAsync implementation waits on a Semaphore if window is full.
        // So this task should NOT complete.
        var writeTask = stream.WriteAsync(new byte[100]).AsTask();

        await Task.Delay(100);
        Assert.False(writeTask.IsCompleted);
        Assert.Empty(_listener.SentPackets); // Nothing sent

        // 3. Send ACK with Large Window
        var ackWnd = CreatePacket(2, 0, connId, nextSeq, ackNr);
        UtpManager.WriteUInt32BigEndian(ackWnd, 12, 65535); // Window Size = 65k
        _listener.SimulateReceive(ackWnd, _remoteParams);

        // 4. Verify Write Completes and Packet Sent
        await writeTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(_listener.SentPackets.TryDequeue(out _));
    }

    [Fact(Timeout = 30000)]
    public async Task TestTimeout_ResendMultiple()
    {
        var stream = await ConnectStream();
        byte[] data = new byte[100];

        await stream.WriteAsync(data);
        await stream.WriteAsync(data);

        _listener.SentPackets.Clear();

        _time.Advance(TimeSpan.FromSeconds(2));

        int resendCount = 0;
        while (_listener.SentPackets.TryDequeue(out _))
        {
            resendCount++;
        }

        Assert.True(resendCount >= 2, $"Expected multiple resends, got {resendCount}");
    }

    [Fact(Timeout = 30000)]
    public async Task TestAppLimited_NoCwndGrowth()
    {
        var stream = await ConnectStream();

        var cwndField = typeof(UtpStream).GetField("_cwnd", BindingFlags.NonPublic | BindingFlags.Instance);
        var slowStartField = typeof(UtpStream).GetField("_slowStart", BindingFlags.NonPublic | BindingFlags.Instance);
        var lastMaxedField = typeof(UtpStream).GetField("_lastMaxedOutWindow", BindingFlags.NonPublic | BindingFlags.Instance);

        slowStartField!.SetValue(stream, false);
        cwndField!.SetValue(stream, 4000.0);
        lastMaxedField!.SetValue(stream, _time.GetUtcNow().AddSeconds(-5));

        byte[] data = new byte[500];
        await stream.WriteAsync(data);
        Assert.True(_listener.SentPackets.TryDequeue(out var pkt));
        var h = ParseHeader(pkt.Data);

        byte[] ack = CreatePacket(2, 0, GetRecvId(stream), 1, h.SeqNr);
        UtpManager.WriteUInt32BigEndian(ack, 8, 20000);
        _listener.SimulateReceive(ack, _remoteParams);

        double cwndAfter = (double)cwndField.GetValue(stream)!;
        Assert.Equal(4000.0, cwndAfter);
    }

    [Fact(Timeout = 30000)]
    public async Task TestInactivityTimeout_ClosesConnection()
    {
        var stream = await ConnectStream();

        // 60 seconds inactivity timeout
        _time.Advance(TimeSpan.FromSeconds(61));

        Assert.Equal(UtpState.Closed, stream.State);
    }

    [Fact]
    public void TestOldestUnackedSeq_Wrap()
    {
        var stream = _manager.CreateStream(_remoteParams);

        var sentPacketsField = typeof(UtpStream).GetField("_sentPackets", BindingFlags.NonPublic | BindingFlags.Instance);
        var sentQueueField = typeof(UtpStream).GetField("_sentSeqQueue", BindingFlags.NonPublic | BindingFlags.Instance);
        var advanceMethod = typeof(UtpStream).GetMethod("AdvanceSentQueue", BindingFlags.NonPublic | BindingFlags.Instance);
        var sentPacketType = typeof(UtpStream).GetNestedType("SentPacket", BindingFlags.NonPublic);
        var seqNrField = typeof(UtpStream).GetField("_seqNr", BindingFlags.NonPublic | BindingFlags.Instance);
        var oldestField = typeof(UtpStream).GetField("_oldestUnackedSeq", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(sentPacketsField);
        Assert.NotNull(sentQueueField);
        Assert.NotNull(advanceMethod);
        Assert.NotNull(sentPacketType);

        var sentPackets = sentPacketsField!.GetValue(stream)!;
        var sentQueue = (Queue<ushort>)sentQueueField!.GetValue(stream)!;
        seqNrField!.SetValue(stream, (ushort)2);
        oldestField!.SetValue(stream, (ushort)65534);

        AddSentPacket(sentPackets, sentPacketType!, 65534);
        AddSentPacket(sentPackets, sentPacketType!, 65535);
        AddSentPacket(sentPackets, sentPacketType!, 0);
        AddSentPacket(sentPackets, sentPacketType!, 1);
        sentQueue.Enqueue(65534);
        sentQueue.Enqueue(65535);
        sentQueue.Enqueue(0);
        sentQueue.Enqueue(1);

        advanceMethod!.Invoke(stream, Array.Empty<object>());
        ushort oldest = (ushort)oldestField.GetValue(stream)!;
        Assert.Equal((ushort)65534, oldest);
    }

    [Fact]
    public async Task TestSynRecv_TimeoutCloses()
    {
        var tcs = new TaskCompletionSource<UtpStream>();
        _manager.OnNewConnection = stream => tcs.TrySetResult(stream);

        var syn = CreatePacket(4, 0, 40000, 1, 0);
        _listener.SimulateReceive(syn, _remoteParams);

        var stream = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var timeoutField = typeof(UtpStream).GetField("_packetTimeout", BindingFlags.NonPublic | BindingFlags.Instance);
        var nextTimeoutField = typeof(UtpStream).GetField("_nextTimeout", BindingFlags.NonPublic | BindingFlags.Instance);

        timeoutField!.SetValue(stream, 1);
        nextTimeoutField!.SetValue(stream, _time.GetUtcNow().AddMilliseconds(-1));

        for (int i = 0; i < 3; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(2));
            stream.CheckTimeout();
            nextTimeoutField.SetValue(stream, _time.GetUtcNow().AddMilliseconds(-1));
        }

        Assert.Equal(UtpState.Closed, stream.State);
    }

    [Fact(Timeout = 30000)]
    public async Task TestMtuProbe_AckAdvancesFloor()
    {
        var stream = await ConnectStream();

        var floorField = typeof(UtpStream).GetField("_mtuFloor", BindingFlags.NonPublic | BindingFlags.Instance);
        var probeSeqField = typeof(UtpStream).GetField("_mtuProbeSeq", BindingFlags.NonPublic | BindingFlags.Instance);
        var probeSizeField = typeof(UtpStream).GetField("_mtuProbeSize", BindingFlags.NonPublic | BindingFlags.Instance);

        int oldFloor = (int)floorField!.GetValue(stream)!;

        byte[] payload = new byte[800];
        await stream.WriteAsync(payload);

        Assert.True(_listener.SentPackets.TryDequeue(out var pkt));
        var h = ParseHeader(pkt.Data);

        ushort probeSeq = (ushort)probeSeqField!.GetValue(stream)!;
        int probeSize = (int)probeSizeField!.GetValue(stream)!;
        Assert.Equal(h.SeqNr, probeSeq);
        Assert.Equal(pkt.Data.Length, probeSize);

        byte[] ack = CreatePacket(2, 0, GetRecvId(stream), 1, h.SeqNr);
        _listener.SimulateReceive(ack, _remoteParams);

        int newFloor = (int)floorField.GetValue(stream)!;
        ushort clearedProbe = (ushort)probeSeqField.GetValue(stream)!;

        Assert.True(newFloor >= oldFloor);
        Assert.Equal(0, clearedProbe);
    }

    [Fact(Timeout = 30000)]
    public async Task TestMtuProbe_DuplicateAckLowersCeiling()
    {
        var stream = await ConnectStream();

        var ceilingField = typeof(UtpStream).GetField("_mtuCeiling", BindingFlags.NonPublic | BindingFlags.Instance);
        var probeSeqField = typeof(UtpStream).GetField("_mtuProbeSeq", BindingFlags.NonPublic | BindingFlags.Instance);

        int oldCeiling = (int)ceilingField!.GetValue(stream)!;

        byte[] payload = new byte[800];
        await stream.WriteAsync(payload);

        Assert.True(_listener.SentPackets.TryDequeue(out var pkt));
        var h = ParseHeader(pkt.Data);

        ushort probeSeq = (ushort)probeSeqField!.GetValue(stream)!;
        Assert.Equal(h.SeqNr, probeSeq);

        ushort dupAckNr = (ushort)(probeSeq - 1);
        for (int i = 0; i < 3; i++)
        {
            byte[] ack = CreatePacket(2, 0, GetRecvId(stream), 1, dupAckNr);
            _listener.SimulateReceive(ack, _remoteParams);
        }

        int newCeiling = (int)ceilingField.GetValue(stream)!;
        Assert.True(newCeiling <= oldCeiling);
    }



    [Fact(Timeout = 10000)]
    public async Task ConnectAsync_PreCancelledToken_ThrowsOperationCancelled()
    {
        var stream = _manager.CreateStream(_remoteParams);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.ConnectAsync(cts.Token));
    }

    [Fact(Timeout = 10000)]
    public async Task ConnectAsync_CancelledDuringHandshake_ThrowsOperationCancelled()
    {
        var stream = _manager.CreateStream(_remoteParams);
        using var cts = new CancellationTokenSource();

        var connectTask = stream.ConnectAsync(cts.Token);
        // SYN is sent but no SYN-ACK comes; cancel instead
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connectTask);
    }

    [Fact(Timeout = 10000)]
    public async Task DisposeAsync_WhenConnected_CleansUpResources()
    {
        var stream = await ConnectStream();

        await stream.DisposeAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => stream.WriteAsync(new byte[1]).AsTask());
    }

    [Fact(Timeout = 10000)]
    public async Task CheckTimeout_ConnectedIdle_SendsKeepAlive()
    {
        var stream = await ConnectStream();

        // Move last-send timestamp 30s into the past so the keep-alive threshold is met.
        // _nextTimeout is already t_0+1000ms (set during handshake), so now=t_0 < _nextTimeout:
        // CheckTimeout() goes to the else-if(Connected) branch, not the packet-timeout branch.
        var lastSendField = typeof(UtpStream).GetField("_lastSendTime", BindingFlags.NonPublic | BindingFlags.Instance);
        lastSendField!.SetValue(stream, _time.GetUtcNow().AddSeconds(-30));

        _listener.SentPackets.Clear();
        stream.CheckTimeout(); // call directly; (now - _lastSendTime) = 30s > 29s threshold

        Assert.True(_listener.SentPackets.TryDequeue(out var keepAlive));
        var header = ParseHeader(keepAlive.Data);
        Assert.Equal(2, header.Type); // ST_STATE
    }

    [Fact(Timeout = 10000)]
    public async Task CheckTimeout_SynSendMaxRetries_ClosesStreamWithTimeout()
    {
        var stream = _manager.CreateStream(_remoteParams);

        // Set up SynSend state via reflection WITHOUT calling ConnectAsync, so _sentPackets stays
        // empty. This lets CheckTimeout enter the SynSend-specific branch (not the generic resend branch).
        var stateField = typeof(UtpStream).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
        var connectTcsField = typeof(UtpStream).GetField("_connectTcs", BindingFlags.NonPublic | BindingFlags.Instance);
        var nextTimeoutField = typeof(UtpStream).GetField("_nextTimeout", BindingFlags.NonPublic | BindingFlags.Instance);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        stateField!.SetValue(stream, UtpState.SynSend);
        connectTcsField!.SetValue(stream, tcs);
        nextTimeoutField!.SetValue(stream, _time.GetUtcNow().AddMilliseconds(-1));

        // MaxSynRetries = 2; calls 1 and 2 increment _timeoutCount, call 3 triggers close
        for (int i = 0; i < 3; i++)
        {
            stream.CheckTimeout();
            nextTimeoutField.SetValue(stream, _time.GetUtcNow().AddMilliseconds(-1));
        }

        Assert.Equal(UtpState.Closed, stream.State);
        await Assert.ThrowsAnyAsync<Exception>(() => tcs.Task);
    }

    [Fact(Timeout = 10000)]
    public async Task SackAck_SelectivelyAcknowledgesOutOfOrderPackets()
    {
        var stream = await ConnectStream();

        // Widen the window so all three sends go out immediately
        var cwndField = typeof(UtpStream).GetField("_cwnd", BindingFlags.NonPublic | BindingFlags.Instance);
        cwndField!.SetValue(stream, 100_000.0);

        byte[] payload = new byte[100];
        await stream.WriteAsync(payload);
        await stream.WriteAsync(payload);
        await stream.WriteAsync(payload);

        Assert.True(_listener.SentPackets.TryDequeue(out var pkt1));
        Assert.True(_listener.SentPackets.TryDequeue(out var pkt2));
        Assert.True(_listener.SentPackets.TryDequeue(out var pkt3));
        _listener.SentPackets.Clear();

        var h1 = ParseHeader(pkt1.Data);

        // SACK ACK: cumulative ack = seq1, SACK bit 0 = seq1+2 = seq3
        // bit 0 in the bitmask represents ackNr+2 per BEP-29
        var sackPacket = CreateSackPacket(2, GetRecvId(stream), 1, h1.SeqNr, sackBitmask: 0x01);
        _listener.SimulateReceive(sackPacket, _remoteParams);

        // seq1 removed by cumulative ACK; seq3 removed by SACK; seq2 still in flight
        Assert.Equal(1, stream.SentPacketsCount);
    }

    [Fact(Timeout = 10000)]
    public async Task MultipleOutOfOrder_AllDeliveredInSequence()
    {
        var stream = await ConnectStream();
        ushort startSeq = GetNextSeq(stream);
        ushort connId = GetRecvId(stream);
        ushort ackNr = GetLastSentSeq(stream);

        // Deliver seq+3, seq+4, seq+2 before seq+1
        _listener.SimulateReceive(CreatePacket(0, 0, connId, (ushort)(startSeq + 3), ackNr, new byte[] { 3 }), _remoteParams);
        _listener.SimulateReceive(CreatePacket(0, 0, connId, (ushort)(startSeq + 4), ackNr, new byte[] { 4 }), _remoteParams);
        _listener.SimulateReceive(CreatePacket(0, 0, connId, (ushort)(startSeq + 2), ackNr, new byte[] { 2 }), _remoteParams);

        byte[] buffer = new byte[10];
        var readTask = stream.ReadAsync(buffer).AsTask();
        await Task.Delay(50);
        Assert.False(readTask.IsCompleted); // still waiting for seq+1

        // Fill the gap — should flush the entire reorder buffer
        _listener.SimulateReceive(CreatePacket(0, 0, connId, (ushort)(startSeq + 1), ackNr, new byte[] { 1 }), _remoteParams);

        int total = await readTask.WaitAsync(TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (total < 4)
        {
            int r = await stream.ReadAsync(buffer.AsMemory(total, 4 - total), cts.Token);
            if (r == 0) break;
            total += r;
        }

        Assert.Equal(4, total);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, buffer[..4]);
    }

    [Fact(Timeout = 10000)]
    public async Task Dispose_WhileReorderBufferHasPendingPackets_DoesNotThrow()
    {
        var stream = await ConnectStream();
        ushort startSeq = GetNextSeq(stream);
        ushort connId = GetRecvId(stream);
        ushort ackNr = GetLastSentSeq(stream);

        // Put seq+2 in the reorder buffer (seq+1 is still missing)
        _listener.SimulateReceive(CreatePacket(0, 0, connId, (ushort)(startSeq + 2), ackNr, new byte[] { 2 }), _remoteParams);

        var ex = await Record.ExceptionAsync(() => stream.DisposeAsync().AsTask());
        Assert.Null(ex);
    }

    [Fact(Timeout = 10000)]
    public async Task UpdateRtt_MeasuredFromAck_UpdatesPacketTimeout()
    {
        var stream = await ConnectStream();

        var sentPacketsField = typeof(UtpStream).GetField("_sentPackets", BindingFlags.NonPublic | BindingFlags.Instance);

        byte[] data = new byte[100];
        await stream.WriteAsync(data);
        Assert.True(_listener.SentPackets.TryDequeue(out var pkt));
        var h = ParseHeader(pkt.Data);

        // Backdate SendTime on the in-flight packet so (now - SendTime) = 100ms > 0.
        // This gives a valid RTT sample when the ACK is processed without needing to advance fake time.
        var sentPktsDict = sentPacketsField!.GetValue(stream)!;
        var values = (System.Collections.IEnumerable)sentPktsDict.GetType().GetProperty("Values")!.GetValue(sentPktsDict)!;
        foreach (var sp in values)
        {
            sp.GetType().GetProperty("SendTime")!.SetValue(sp, _time.GetUtcNow().AddMilliseconds(-100));
            break;
        }

        byte[] ack = CreatePacket(2, 0, GetRecvId(stream), 1, h.SeqNr);
        _listener.SimulateReceive(ack, _remoteParams);

        Assert.True(stream.Rtt > 0, $"Expected Rtt > 0 after RTT sample, got {stream.Rtt}");
    }

    #endregion

    #region Helpers

    private async Task<UtpStream> ConnectStream()
    {
        var stream = _manager.CreateStream(_remoteParams);
        var t = stream.ConnectAsync();

        Assert.True(_listener.SentPackets.TryDequeue(out var syn));
        var h = ParseHeader(syn.Data);

        var ack = CreatePacket(2, 0, h.ConnectionId, (ushort)(h.SeqNr + 1), h.SeqNr);
        _listener.SimulateReceive(ack, _remoteParams);

        await t;
        _listener.SentPackets.Clear(); // Clear handshake packets
        return stream;
    }

    private static ushort GetNextSeq(UtpStream stream) => stream.AckNr;

    private static ushort GetRecvId(UtpStream stream) => stream.ConnectionIdRecv;

    private static ushort GetSendId(UtpStream stream) => stream.ConnectionIdSend;

    private static ushort GetLastSentSeq(UtpStream stream) => (ushort)(stream.SeqNr - 1);

    private static (byte Type, byte Version, byte Extension, ushort ConnectionId, uint Timestamp, uint TimestampDiff, uint Wnd, ushort SeqNr, ushort AckNr) ParseHeader(byte[] data)
    {
        byte type = (byte)(data[0] >> 4);
        byte ver = (byte)(data[0] & 0x0F);
        byte ext = data[1];
        ushort connId = UtpManager.ReadUInt16BigEndian(data, 2);
        uint ts = UtpManager.ReadUInt32BigEndian(data, 4);
        uint tsDiff = UtpManager.ReadUInt32BigEndian(data, 8);
        uint wnd = UtpManager.ReadUInt32BigEndian(data, 12);
        ushort seq = UtpManager.ReadUInt16BigEndian(data, 16);
        ushort ack = UtpManager.ReadUInt16BigEndian(data, 18);
        return (type, ver, ext, connId, ts, tsDiff, wnd, seq, ack);
    }

    private static byte[] CreatePacket(byte type, byte ext, ushort connId, ushort seq, ushort ack, byte[]? payload = null)
    {
        int payloadLen = payload?.Length ?? 0;
        byte[] buffer = new byte[20 + payloadLen];
        buffer[0] = (byte)((type << 4) | 1);
        buffer[1] = ext;
        UtpManager.WriteUInt16BigEndian(buffer, 2, connId);
        UtpManager.WriteUInt32BigEndian(buffer, 4, 0);
        UtpManager.WriteUInt32BigEndian(buffer, 8, 0);
        UtpManager.WriteUInt32BigEndian(buffer, 12, 65535);
        UtpManager.WriteUInt16BigEndian(buffer, 16, seq);
        UtpManager.WriteUInt16BigEndian(buffer, 18, ack);

        if (payload != null)
        {
            Array.Copy(payload, 0, buffer, 20, payloadLen);
        }
        return buffer;
    }

    private static void AddSentPacket(object sentPackets, Type sentPacketType, ushort seq)
    {
        var pkt = Activator.CreateInstance(sentPacketType)!;
        sentPacketType.GetProperty("SeqNr")!.SetValue(pkt, seq);
        sentPacketType.GetProperty("Buffer")!.SetValue(pkt, new byte[1]);
        sentPacketType.GetProperty("Length")!.SetValue(pkt, 1);
        sentPacketType.GetProperty("SendTime")!.SetValue(pkt, DateTimeOffset.UtcNow);
        sentPacketType.GetProperty("Resent")!.SetValue(pkt, false);
        sentPacketType.GetProperty("RttSampled")!.SetValue(pkt, false);
        sentPacketType.GetProperty("Pooled")!.SetValue(pkt, false);
        var addMethod = sentPackets.GetType().GetMethod("Add", new[] { typeof(ushort), sentPacketType })!;
        addMethod.Invoke(sentPackets, new object[] { seq, pkt });
    }

    private static byte[] CreatePacketWithExtensionBits(byte type, ushort connId, ushort seq, ushort ack, byte[] extensionBits)
    {
        if (extensionBits.Length != 8)
        {
            throw new ArgumentException("Extension bits must be 8 bytes", nameof(extensionBits));
        }

        byte[] buffer = new byte[20 + 2 + 8];
        buffer[0] = (byte)((type << 4) | 1);
        buffer[1] = 2; // Extension bits
        UtpManager.WriteUInt16BigEndian(buffer, 2, connId);
        UtpManager.WriteUInt32BigEndian(buffer, 4, 0);
        UtpManager.WriteUInt32BigEndian(buffer, 8, 0);
        UtpManager.WriteUInt32BigEndian(buffer, 12, 65535);
        UtpManager.WriteUInt16BigEndian(buffer, 16, seq);
        UtpManager.WriteUInt16BigEndian(buffer, 18, ack);

        buffer[20] = 0; // Next extension = none
        buffer[21] = 8; // Length
        Array.Copy(extensionBits, 0, buffer, 22, 8);

        return buffer;
    }

    private static byte[] CreateSackPacket(byte type, ushort connId, ushort seq, ushort ack, byte sackBitmask)
    {
        byte[] buffer = new byte[26]; // 20-byte header + 2-byte ext header + 4-byte bitmask
        buffer[0] = (byte)((type << 4) | 1);
        buffer[1] = 1; // Extension type 1 = SACK
        UtpManager.WriteUInt16BigEndian(buffer, 2, connId);
        UtpManager.WriteUInt32BigEndian(buffer, 4, 0);
        UtpManager.WriteUInt32BigEndian(buffer, 8, 0);
        UtpManager.WriteUInt32BigEndian(buffer, 12, 65535);
        UtpManager.WriteUInt16BigEndian(buffer, 16, seq);
        UtpManager.WriteUInt16BigEndian(buffer, 18, ack);
        buffer[20] = 0; // next extension = none
        buffer[21] = 4; // bitmask length = 4 bytes
        buffer[22] = sackBitmask;
        // bytes 23-25 = 0
        return buffer;
    }

    #endregion
}





