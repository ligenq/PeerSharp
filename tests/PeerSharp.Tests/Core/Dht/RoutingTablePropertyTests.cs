using CsCheck;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals.Dht;
using System.Net;

namespace PeerSharp.Tests.Core.Dht;

/// <summary>
/// What the routing table promises for any sequence of nodes, rather than for a hand-picked few.
/// </summary>
/// <remarks>
/// <para>
/// The table is filled from the network: every node in it was suggested by a stranger, in whatever
/// order and quantity they chose, and the same node can be offered repeatedly. That makes the
/// interesting cases combinations rather than individual inputs, which is what these cover.
/// </para>
/// <para>
/// Deliberately not asserted: that <see cref="RoutingTable.FindClosest"/> returns the k nodes
/// nearest the target. It walks outward from the target's bucket, which is Kademlia's approximation
/// and not a sort, so demanding exact nearest-first ordering would be asserting something the
/// implementation does not claim and does not need.
/// </para>
/// </remarks>
public class RoutingTablePropertyTests
{
    private static readonly Gen<byte[]> NodeId = Gen.Byte.Array[20, 20];

    /// <summary>
    /// Private addresses, because BEP 42 only derives a node ID from routable ones. Generating
    /// public addresses would leave almost every node marked insecure and quietly change which
    /// branch of the table the test exercises.
    /// </summary>
    private static readonly Gen<IPEndPoint> EndPoint = Gen.Select(
        Gen.Byte, Gen.Byte, Gen.UShort[1024, 65535])
        .Select(t => new IPEndPoint(new IPAddress([10, 0, t.Item1, t.Item2]), t.Item3));

    private static readonly Gen<(byte[] Id, IPEndPoint EndPoint)[]> Nodes =
        Gen.Select(NodeId, EndPoint).Array[0, 60];

    [Fact]
    public void FindClosestNeverReturnsMoreThanAsked()
    {
        Gen.Select(NodeId, Nodes, NodeId, Gen.Int[0, 40]).Sample((localId, nodes, target, count) =>
        {
            var table = Build(localId, nodes);

            Assert.True(table.FindClosest(target, count).Count <= count);
        }, iter: 2_000);
    }

    [Fact]
    public void FindClosestNeverRepeatsANode()
    {
        // A duplicate here is a lookup wasting a slot on a node it already has, and in an iterative
        // search that slot is the whole budget for making progress.
        Gen.Select(NodeId, Nodes, NodeId, Gen.Int[1, 40]).Sample((localId, nodes, target, count) =>
        {
            var table = Build(localId, nodes);
            var found = table.FindClosest(target, count);

            Assert.Equal(found.Count, found.Select(node => Convert.ToHexString(node.Id)).Distinct().Count());
        }, iter: 2_000);
    }

    [Fact]
    public void FindClosestOnlyReturnsNodesThatWereAdded()
    {
        Gen.Select(NodeId, Nodes, NodeId, Gen.Int[1, 40]).Sample((localId, nodes, target, count) =>
        {
            var table = Build(localId, nodes);
            var added = nodes.Select(node => Convert.ToHexString(node.Id)).ToHashSet();

            foreach (var node in table.FindClosest(target, count))
            {
                Assert.Contains(Convert.ToHexString(node.Id), added);
            }
        }, iter: 2_000);
    }

    [Fact]
    public void OfferingTheSameNodeRepeatedlyDoesNotGrowTheTable()
    {
        // Announcing peers re-offer themselves constantly. If each offer took another slot, a
        // handful of nodes would evict everything else the table knows.
        Gen.Select(NodeId, NodeId, EndPoint, Gen.Int[1, 20]).Sample((localId, id, endPoint, repeats) =>
        {
            var table = new RoutingTable(localId, new FakeTimeProvider());
            for (int i = 0; i < repeats; i++)
            {
                table.AddNode(id, endPoint);
            }

            var all = table.GetAllNodes();
            Assert.True(all.Count <= 1, $"one node offered {repeats} times occupied {all.Count} slots");
        }, iter: 2_000);
    }

    [Fact]
    public void EveryNodeInTheTableIsThereOnce()
    {
        Gen.Select(NodeId, Nodes).Sample((localId, nodes) =>
        {
            var table = Build(localId, nodes);
            var all = table.GetAllNodes();

            Assert.Equal(all.Count, all.Select(node => Convert.ToHexString(node.Id)).Distinct().Count());
        }, iter: 2_000);
    }

    [Fact]
    public void GetAllNodesRespectsItsLimit()
    {
        Gen.Select(NodeId, Nodes, Gen.Int[0, 30]).Sample((localId, nodes, max) =>
        {
            var table = Build(localId, nodes);

            Assert.True(table.GetAllNodes(max).Count <= max);
        }, iter: 2_000);
    }

    [Fact]
    public void DistanceIsSymmetricAndZeroToItself()
    {
        // XOR distance is a metric, and the bucket a node lands in is derived from it. If it were
        // not symmetric, two nodes would disagree about how far apart they are.
        Gen.Select(NodeId, NodeId).Sample((left, right) =>
        {
            Assert.Equal(RoutingTable.GetDistance(left, right), RoutingTable.GetDistance(right, left));
            Assert.Equal(0, RoutingTable.GetDistance(left, left));
        }, iter: 5_000);
    }

    [Fact]
    public void AnIdOfTheWrongLengthIsIgnoredRatherThanStored()
    {
        // Node IDs arrive as a byte string from the network and nothing guarantees the length.
        Gen.Select(NodeId, Gen.Byte.Array[0, 40], EndPoint).Sample((localId, id, endPoint) =>
        {
            var table = new RoutingTable(localId, new FakeTimeProvider());
            table.AddNode(id, endPoint);

            Assert.Equal(id.Length == 20 ? 1 : 0, table.GetAllNodes().Count);
        }, iter: 2_000);
    }

    private static RoutingTable Build(byte[] localId, (byte[] Id, IPEndPoint EndPoint)[] nodes)
    {
        var table = new RoutingTable(localId, new FakeTimeProvider());
        foreach (var (id, endPoint) in nodes)
        {
            table.AddNode(id, endPoint);
        }

        return table;
    }
}
