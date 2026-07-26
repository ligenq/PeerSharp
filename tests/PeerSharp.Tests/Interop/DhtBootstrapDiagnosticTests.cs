using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Network;


namespace PeerSharp.Tests.Interop;

/// <summary>
/// Measures how well bootstrap actually populates the routing table against the live network.
///
/// This exists because "no live node accepted the put" is not a diagnosis. A lookup can fail
/// because the encoding is wrong, because the network is unreachable, or because the routing table
/// is too sparse to walk anywhere - and those need completely different fixes. Counting nodes
/// separates them.
///
/// Gated and excluded from CI exactly like the rest of this namespace.
/// </summary>
public class DhtBootstrapDiagnosticTests
{
    private readonly ITestOutputHelper _output;

    public DhtBootstrapDiagnosticTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static void RequireInteropEnabled()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PEERSHARP_INTEROP")))
        {
            Assert.Skip("Set PEERSHARP_INTEROP=1 to run live DHT interoperability tests.");
        }
    }

    /// <summary>
    /// Reports the routing table size over time. Before find_node was sent this would plateau at
    /// roughly one entry per responsive bootstrap router; it should now grow well past that.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task Bootstrap_PopulatesTheRoutingTable()
    {
        RequireInteropEnabled();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var settings = new Settings();
        await using var listener = new UdpListener(0, new UdpSocketFactory(), settings, NullLoggerFactory.Instance, TimeProvider.System);
        await listener.StartAsync(cts.Token);

        await using var dht = DhtManager.CreateSecure(listener, settings);
        await dht.StartAsync(cts.Token);

        int peak = 0;
        for (int elapsed = 0; elapsed < 60; elapsed += 5)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);

            // ConsumeStateSnapshot only yields a snapshot when the table changed, so an empty
            // result means "no change", not "no nodes".
            var snapshot = dht.ConsumeStateSnapshot();
            if (snapshot is not null)
            {
                peak = Math.Max(peak, snapshot.Nodes.Count);
            }

            _output.WriteLine($"t+{elapsed + 5}s: peak routing table size = {peak}");
        }

        _output.WriteLine($"Final peak: {peak} nodes");

        // A single responsive bootstrap router with no find_node walk yields about one node. Ten is
        // a low bar that still distinguishes "the walk works" from "we only know the routers".
        Assert.True(
            peak >= 10,
            $"Routing table only reached {peak} nodes. Bootstrap is not discovering peers, so no iterative lookup can succeed.");
    }
}
