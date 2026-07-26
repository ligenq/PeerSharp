using BenchmarkDotNet.Attributes;
using PeerSharp.Internals.Dht;
using System.Net;

namespace PeerSharp.Benchmarks;

/// <summary>
/// Kademlia routing table operations. An iterative DHT lookup calls
/// <see cref="RoutingTable.FindClosest"/> once per round to pick the next nodes to query, and a
/// busy node runs many lookups at once - one per torrent being announced or searched.
///
/// <see cref="RoutingTable.AddNode"/> is measured too because it runs far more often than lookups
/// do: every response from every node feeds one, so it is driven by inbound packet rate rather
/// than by user actions.
///
/// The table is filled with random ids, which spreads nodes across buckets the way a real DHT
/// does. A sequential fill would pile everything into one bucket and flatter the search.
///
/// <c>GetDistance</c> is deliberately not benchmarked: it is a 20-byte XOR that the JIT hoists
/// out of the measurement loop entirely, so the harness reports 0 ns rather than anything real.
/// </summary>
[MemoryDiagnoser]
public class DhtRoutingTableBenchmarks
{
    private const int NodeIdBytes = 20;

    private RoutingTable _table = null!;
    private byte[] _target = null!;
    private byte[][] _churnIds = null!;
    private int _churnIndex;

    /// <summary>
    /// Nodes in the table. A healthy Mainline node holds a few hundred; 8,000 models a
    /// long-running client that has met a large slice of the network.
    /// </summary>
    [Params(500, 8_000)]
    public int NodeCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(20260726);

        var localId = new byte[NodeIdBytes];
        random.NextBytes(localId);
        _table = new RoutingTable(localId, TimeProvider.System);

        for (int i = 0; i < NodeCount; i++)
        {
            var id = new byte[NodeIdBytes];
            random.NextBytes(id);
            _table.AddNode(id, new IPEndPoint(new IPAddress(random.Next()), 6881));
        }

        _target = new byte[NodeIdBytes];
        random.NextBytes(_target);

        // Pre-generated ids for the add benchmark, so id generation is not measured.
        _churnIds = new byte[512][];
        for (int i = 0; i < _churnIds.Length; i++)
        {
            _churnIds[i] = new byte[NodeIdBytes];
            random.NextBytes(_churnIds[i]);
        }
    }

    /// <summary>The standard k=8 lookup that drives each round of an iterative search.</summary>
    [Benchmark(Baseline = true, Description = "FindClosest, k=8")]
    public int FindClosestEight() => _table.FindClosest(_target, 8).Count;

    /// <summary>A wider fan-out, as used when seeding a fresh lookup or answering find_node.</summary>
    [Benchmark(Description = "FindClosest, k=64")]
    public int FindClosestSixtyFour() => _table.FindClosest(_target, 64).Count;

    [Benchmark(Description = "AddNode (known-node refresh path)")]
    public void AddNode()
    {
        _churnIndex = (_churnIndex + 1) % _churnIds.Length;
        _table.AddNode(_churnIds[_churnIndex], new IPEndPoint(IPAddress.Loopback, 6881));
    }

    [Benchmark(Description = "GetAllNodes (state persistence)")]
    public int GetAllNodes() => _table.GetAllNodes().Count;
}
