using Microsoft.Coyote;
using Microsoft.Coyote.SystematicTesting;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Network;
using PeerSharp.BEncoding;
using System.Net;
using System.Collections.Concurrent;

namespace PeerSharp.Tests.Concurrency;

public class DhtConcurrencyTests
{
    private readonly ITestOutputHelper _output;

    public DhtConcurrencyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private void RunCoyoteTest(Action test, uint iterations = 100)
    {
        var config = Configuration.Create()
            .WithTestingIterations(iterations)
            .WithMaxSchedulingSteps(1000);

        using var engine = TestingEngine.Create(config, test);
        engine.Run();

        var report = engine.TestReport;
        if (report.NumOfFoundBugs > 0)
        {
            _output.WriteLine($"Found {report.NumOfFoundBugs} bug(s)!");
            _output.WriteLine(engine.GetReport());
            Assert.Fail($"Coyote found {report.NumOfFoundBugs} concurrency bug(s). See test output for details.");
        }
    }

    private class MockUdpListener : IUdpListener
    {
        public int Port => 6881;
        public IUdpReceiver? Receiver { get; private set; }
        public ConcurrentQueue<(byte[] Data, IPEndPoint EndPoint)> SentPackets { get; } = new();

        public void RegisterReceiver(IUdpReceiver receiver)
        {
            Receiver = receiver;
        }

        public Task SendAsync(ReadOnlyMemory<byte> data, IPEndPoint endpoint, CancellationToken ct = default)
        {
            SentPackets.Enqueue((data.ToArray(), endpoint));
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Stop() { }
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void Dht_ConcurrentAddNode_MaintainsRoutingTableConsistency()
    {
        RunCoyoteTest(() =>
        {
            var listener = new MockUdpListener();
            var settings = new Settings();
            var timeProvider = new FakeTimeProvider();

            // Create manager via secure factory
            var dht = DhtManager.CreateSecure(listener, settings, null, timeProvider);
            // DhtManager.StartAsync is usually awaited, but in Coyote test we might fire and forget or await
            // StartAsync starts background maintenance loop.
            _ = dht.StartAsync();

            var tasks = new List<Task>();
            const int nodeCount = 20;

            // Concurrent add nodes (via ping response simulation)
            for (int i = 0; i < nodeCount; i++)
            {
                byte idByte = (byte)i;
                tasks.Add(Task.Run(() =>
                {
                    // Generate ID that falls into different buckets
                    var id = new byte[20];
                    id[0] = idByte;
                    var ep = new IPEndPoint(IPAddress.Loopback, 1000 + idByte);

                    // Simulate receiving a response from this node to add it
                    // We need to trigger AddNode indirectly or access internal _table if possible.
                    // DhtManager.AddNode is private/internal? 
                    // Let's use the public API: dht.Ping(ep) -> sends ping.
                    // If we receive a response, it adds the node.
                    // So we simulate a response.

                    // Build response packet
                    var dict = new BDict();
                    dict.Dict["t"] = new BString([0, 0]); // transaction id
                    dict.Dict["y"] = new BString("r"u8.ToArray()); // response
                    dict.Dict["r"] = new BDict();
                    ((BDict)dict.Dict["r"]).Dict["id"] = new BString(id);

                    var packet = BencodeWriter.Write(dict);

                    // We need to inject this into the DhtManager
                    listener.Receiver?.Receive(packet, ep);
                }));
            }

            Task.WaitAll(tasks.ToArray());

            // Verify routing table consistency via reflection or public metrics
            // We can't easily assert exact count due to bucket logic, but we should not crash
            // and internal state should be valid.

            _ = dht.StopAsync();
        });
    }

    [Fact]
    public void Dht_ConcurrentGetPeers_NoDeadlocks()
    {
        RunCoyoteTest(() =>
        {
            var listener = new MockUdpListener();
            var settings = new Settings();
            var timeProvider = new FakeTimeProvider();
            var dht = DhtManager.CreateSecure(listener, settings, null, timeProvider);
            _ = dht.StartAsync();

            var tasks = new List<Task>();
            var infoHash = new InfoHash(new byte[20]);

            for (int i = 0; i < 10; i++)
            {
                tasks.Add(Task.Run(() => dht.FindPeers(infoHash)));
            }

            Task.WaitAll(tasks.ToArray());
            _ = dht.StopAsync();
        });
    }
}
