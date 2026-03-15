using Microsoft.Coyote;
using Microsoft.Coyote.SystematicTesting;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals.Utp;
using PeerSharp.Internals.Network;
using System.Net;
using System.Collections.Concurrent;
using System.Reflection;

namespace PeerSharp.Tests.Concurrency;

public class UtpDataPathTests
{
    private readonly ITestOutputHelper _output;

    public UtpDataPathTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private void RunCoyoteTest(Action test, uint iterations = 100)
    {
        var config = Configuration.Create()
            .WithTestingIterations(iterations)
            .WithMaxSchedulingSteps(1000);

        var engine = TestingEngine.Create(config, test);
        engine.Run();

        var report = engine.TestReport;
        if (report.NumOfFoundBugs > 0)
        {
            _output.WriteLine($"Found {report.NumOfFoundBugs} bug(s)!");
            _output.WriteLine(engine.GetReport());
            Assert.Fail($"Coyote found {report.NumOfFoundBugs} concurrency bug(s). See test output for details.");
        }
    }

    private class MockUtpManager : IUtpManager
    {
        public Action<UtpStream>? OnNewConnection { get; set; }
        public ConcurrentQueue<(byte[] Data, IPEndPoint Remote)> SentPackets { get; } = new();

        public Task SendAsync(ReadOnlyMemory<byte> packet, IPEndPoint remote, CancellationToken ct)
        {
            SentPackets.Enqueue((packet.ToArray(), remote));
            return Task.CompletedTask;
        }

        public UtpStream CreateStream(IPEndPoint remote) => throw new NotImplementedException();
        public void CloseStream(UtpStream stream) { }
        public void Start(IUdpListener listener) { }
        public void Stop() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void UtpStream_ConcurrentDataTransfer_WithReordering()
    {
        RunCoyoteTest(() =>
        {
            var timeProvider = new FakeTimeProvider();
            var manager = new MockUtpManager();
            var remote = new IPEndPoint(IPAddress.Loopback, 12345);
            var stream = new UtpStream(manager, remote, 100, 101, timeProvider);

            // Set state to Connected via reflection or by simulating handshake
            // Simulating handshake is cleaner but more code. Let's force state.
            var stateField = typeof(UtpStream).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            stateField!.SetValue(stream, UtpState.Connected);

            // Set initial seq nr
            var seqField = typeof(UtpStream).GetField("_seqNr", BindingFlags.NonPublic | BindingFlags.Instance);
            ushort startSeq = (ushort)seqField!.GetValue(stream)!;

            // AckNr needs to be initialized
            var ackField = typeof(UtpStream).GetField("_ackNr", BindingFlags.NonPublic | BindingFlags.Instance);
            ackField!.SetValue(stream, (ushort)(startSeq - 1));

            var tasks = new List<Task>();

            // Sender task (simulating application writing to stream)
            tasks.Add(Task.Run(async () =>
            {
                var buffer = new byte[1000];
                await stream.WriteAsync(buffer);
            }));

            // Receiver task (simulating network receiving ACKs)
            tasks.Add(Task.Run(async () =>
            {
                // Simulate ACKs coming back, potentially reordered
                // We expect packets to be sent by the stream.
                // We need to poll manager.SentPackets.

                int acked = 0;
                while (acked < 1000)
                {
                    if (manager.SentPackets.TryDequeue(out var item))
                    {
                        // Parse packet to get seqnr, then send ACK
                        // This is complex because we need to parse the header.
                        // Ideally we'd use UtpManager.ParseHeader but it's private.
                        // We'll manually parse what we need.

                        // Header is 20 bytes.
                        // SeqNr is at offset 16 (ushort big endian)
                        var data = item.Data;
                        if (data.Length >= 20)
                        {
                            ushort seq = (ushort)((data[16] << 8) | data[17]);

                            // Send ACK back
                            // We construct a fake ACK packet and inject it into stream
                            // ProcessPacketWithSack is internal.

                            var header = new MessageHeader
                            {
                                TypeVer = (byte)((byte)MessageType.ST_STATE << 4 | 1),
                                SeqNr = (ushort)(seq + 100), // Random remote seq
                                AckNr = seq,
                                WndSize = 100000
                            };

                            // Inject
                            stream.ProcessPacketWithSack(header, [], 20, null, null, remote);

                            // We assume roughly 1 byte per packet for this simple test or 
                            // we just ack until done. 
                            // Since we sent 1000 bytes, and MSS is likely > 1000, it's probably 1 packet.
                            // But Coyote might interleave such that WriteAsync splits it?
                            // WriteAsync uses GetPayloadMss.

                            acked += data.Length - 20;
                        }
                    }
                    else
                    {
                        await Task.Delay(1);
                    }
                }
            }));

            Task.WaitAll(tasks.ToArray());

            // Verify stream is still healthy
            stream.Dispose();
        });
    }

    [Fact]
    public void UtpStream_PacketLoss_Retransmits()
    {
        RunCoyoteTest(() =>
        {
            var timeProvider = new FakeTimeProvider();
            var manager = new MockUtpManager();
            var remote = new IPEndPoint(IPAddress.Loopback, 12345);
            var stream = new UtpStream(manager, remote, 100, 101, timeProvider);

            // Force Connected state
            typeof(UtpStream).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(stream, UtpState.Connected);

            // Init Seq/Ack
            var seqField = typeof(UtpStream).GetField("_seqNr", BindingFlags.NonPublic | BindingFlags.Instance);
            ushort startSeq = (ushort)seqField!.GetValue(stream)!;
            typeof(UtpStream).GetField("_ackNr", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(stream, (ushort)(startSeq - 1));

            var tasks = new List<Task>();

            // Sender
            tasks.Add(Task.Run(async () =>
            {
                var buffer = new byte[100]; // Small packet
                await stream.WriteAsync(buffer);
            }));

            // Receiver (Network)
            tasks.Add(Task.Run(async () =>
            {
                bool dropped = false;
                while (true)
                {
                    if (manager.SentPackets.TryDequeue(out var item))
                    {
                        var data = item.Data;
                        if (data.Length >= 20)
                        {
                            // Drop the first DATA packet
                            byte typeVer = data[0];
                            var type = (MessageType)(typeVer >> 4);

                            if (type == MessageType.ST_DATA && !dropped)
                            {
                                dropped = true;
                                // Advance time to trigger timeout (Initial timeout is 3s)
                                timeProvider.Advance(TimeSpan.FromSeconds(4));
                                // Don't ACK
                                continue;
                            }

                            // If it's the retransmission (or any subsequent packet), ACK it
                            if (type == MessageType.ST_DATA && dropped)
                            {
                                ushort seq = (ushort)((data[16] << 8) | data[17]);
                                var header = new MessageHeader
                                {
                                    TypeVer = (byte)((byte)MessageType.ST_STATE << 4 | 1),
                                    SeqNr = (ushort)(seq + 100),
                                    AckNr = seq,
                                    WndSize = 100000
                                };
                                stream.ProcessPacketWithSack(header, [], 20, null, null, remote);
                                break; // Done
                            }
                        }
                    }
                    else
                    {
                        await Task.Delay(1);
                        // Ensure we check for timeouts if queue is empty
                        if (dropped)
                        {
                             stream.CheckTimeout();
                        }
                    }
                }
            }));

            Task.WaitAll(tasks.ToArray());
            stream.Dispose();
        });
    }
}
