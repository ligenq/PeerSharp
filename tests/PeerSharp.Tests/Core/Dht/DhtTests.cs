using System.Net;
using PeerSharp.Internals.Dht;

namespace PeerSharp.Tests.Core.Dht;

public class DhtTests
{
    [Fact]
    public void TestNodeIdDistance()
    {
        byte[] id1 = new byte[20];
        byte[] id2 = new byte[20];

        // Same ID -> distance 0
        Assert.Equal(0, RoutingTable.GetDistance(id1, id2));

        id2[19] = 1; // Last byte diff by 1 (LSB)
        // Distance calculation returns bucket index (0-159). 
        // LSB difference corresponds to bucket 0.
        Assert.Equal(0, RoutingTable.GetDistance(id1, id2));

        // Test MSB diff
        byte[] id3 = new byte[20];
        id3[0] = 0x80;
        // MSB difference corresponds to bucket 159.
        Assert.Equal(159, RoutingTable.GetDistance(id1, id3));
    }

    [Fact]
    public void TestRoutingTableAddFind()
    {
        byte[] localId = new byte[20];
        var table = new RoutingTable(localId, TimeProvider.System);

        byte[] farId = new byte[20]; farId[0] = 0x80; // Distance 159
        byte[] closeId = new byte[20]; closeId[19] = 0x01; // Distance 0

        table.AddNode(farId, new IPEndPoint(IPAddress.Loopback, 1000));
        table.AddNode(closeId, new IPEndPoint(IPAddress.Loopback, 1001));

        var nodes = table.FindClosest(localId, 8);

        // Should return sorted by proximity to bucket
        // Since target is localId (bucket 0), bucket 0 comes first.
        Assert.Equal(2, nodes.Count);
        Assert.Equal(closeId, nodes[0].Id);
        Assert.Equal(farId, nodes[1].Id);
    }

    [Fact]
    public void TestRoutingTableBucketSplit()
    {
        byte[] localId = new byte[20];
        var table = new RoutingTable(localId, TimeProvider.System);

        // Fill a bucket (e.g. bucket 159 - MSB differs)
        for (int k = 0; k < 10; k++)
        {
            byte[] id = new byte[20];
            id[0] = 0x80;
            id[1] = (byte)k; // Distinguish them
            table.AddNode(id, new IPEndPoint(IPAddress.Loopback, 2000 + k));
        }

        // MaxBucketNodesCount is 8.
        // We added 10.
        // 8 should be in Active Nodes.
        // 2 should be in Cache.
        // FindClosest only returns active nodes.

        var nodes = table.FindClosest(localId, 20);
        Assert.Equal(8, nodes.Count);
    }

    [Fact]
    public void RoutingTableAddNode_ShortNodeId_IsIgnored()
    {
        byte[] localId = new byte[20];
        var table = new RoutingTable(localId, TimeProvider.System);

        table.AddNode([1, 2, 3], new IPEndPoint(IPAddress.Loopback, 6881));

        Assert.Empty(table.GetAllNodes());
    }

    [Fact]
    public void GetDistance_ShortNodeId_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => RoutingTable.GetDistance([1, 2, 3], new byte[20]));
    }
}





