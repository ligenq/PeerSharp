using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Config;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Utp;
using System.Net;
using System.Reflection;

namespace PeerSharp.Tests.Core.Utp;

/// <summary>
/// What the congestion window does after a timeout, which is the difference between a uTP connection
/// that recovers and one that is alive but carries nothing.
/// </summary>
/// <remarks>
/// <para>
/// It showed up as an intermittent stall: a 256 MiB loopback transfer that normally finishes in
/// under four seconds instead sat at one packet in flight until the run was cut off, in roughly one
/// run in seven, with the sender's window reading a single MSS after a handful of timeouts.
/// </para>
/// <para>
/// The timeout handler cut the window to one packet, set the slow-start threshold to that same one
/// packet, and left slow-start. Between them there was no way back up: the threshold guaranteed an
/// immediate exit even had slow-start been re-entered, and the only remaining growth was LEDBAT's
/// linear gain, which is itself zeroed whenever this side is not filling its window - which a client
/// that mostly sends requests never is. One timeout pinned a connection at a single packet.
/// </para>

/// </remarks>
public class UtpTimeoutRecoveryTests
{
    [Fact]
    public void ATimeoutWithPacketsInFlightReEntersSlowStartSoTheWindowCanRecover()
    {
        var clock = new FakeTimeProvider();
        var stream = CreateStream(clock);
        ArmConnection(stream, clock, bytesUnacked: 4000);
        Set(stream, "_slowStart", false);

        TimeOut(stream, clock);

        Assert.True(
            (bool)Get(stream, "_slowStart")!,
            "After a timeout the window is one packet. Without slow-start it can only grow by the "
                + "LEDBAT gain, which is zero while this side is not filling the window.");
    }

    [Fact]
    public void ATimeoutWithPacketsInFlightDoesSurrenderTheWindow()
    {
        // The window really is surrendered, so the slow-start re-entry above cannot be mistaken for
        // the timeout having been ignored.
        var clock = new FakeTimeProvider();
        var stream = CreateStream(clock);
        ArmConnection(stream, clock, bytesUnacked: 4000);
        Set(stream, "_cwnd", 60000d);

        TimeOut(stream, clock);

        Assert.Equal((double)(int)Get(stream, "_mss")!, (double)Get(stream, "_cwnd")!, 0);
    }

    [Fact]
    public void ATimeoutDoesNotPullTheSlowStartThresholdDownToTheCutWindow()
    {
        // The two halves have to agree. Setting the threshold to the single packet the window was
        // just cut to leaves slow-start again on the very first acknowledgement, which makes the
        // re-entry above worth nothing. libtorrent leaves the threshold to a loss event, which is
        // what knows where the path actually saturated.
        var clock = new FakeTimeProvider();
        var stream = CreateStream(clock);
        ArmConnection(stream, clock, bytesUnacked: 4000);
        Set(stream, "_cwnd", 60000d);
        Set(stream, "_ssthresh", 40000d);

        TimeOut(stream, clock);

        Assert.Equal(40000d, (double)Get(stream, "_ssthresh")!, 0);
    }

    /// <summary>Puts the stream in the state a connected, sending socket is in.</summary>
    private static void ArmConnection(UtpStream stream, FakeTimeProvider clock, int bytesUnacked)
    {
        Set(stream, "_state", UtpState.Connected);
        Set(stream, "_lastReceiveTime", clock.GetUtcNow());
        Set(stream, "_sentBytesUnacked", bytesUnacked);

        if (bytesUnacked > 0)
        {
            // One outstanding packet, so the handler sees something to have lost.
            var sent = typeof(UtpStream)
                .GetField("_sentPackets", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(stream)!;
            var packetType = typeof(UtpStream)
                .GetNestedType("SentPacket", BindingFlags.NonPublic)!;
            var packet = Activator.CreateInstance(packetType)!;
            packetType.GetProperty("Buffer")!.SetValue(packet, new byte[64]);
            packetType.GetProperty("Length")!.SetValue(packet, 64);
            packetType.GetProperty("SeqNr")!.SetValue(packet, (ushort)1);
            packetType.GetProperty("SendTime")!.SetValue(packet, clock.GetUtcNow());
            sent.GetType().GetMethod("set_Item")!.Invoke(sent, [(ushort)1, packet]);

            var queue = typeof(UtpStream)
                .GetField("_sentSeqQueue", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(stream)!;
            queue.GetType().GetMethod("Enqueue")!.Invoke(queue, [(ushort)1]);
        }
    }

    private static void TimeOut(UtpStream stream, FakeTimeProvider clock)
    {
        Set(stream, "_nextTimeout", clock.GetUtcNow().AddMilliseconds(-1));
        stream.CheckTimeout();
    }

    private static void Set(UtpStream stream, string field, object value) =>
        typeof(UtpStream)
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(stream, value);

    private static object? Get(UtpStream stream, string field) =>
        typeof(UtpStream)
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
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
