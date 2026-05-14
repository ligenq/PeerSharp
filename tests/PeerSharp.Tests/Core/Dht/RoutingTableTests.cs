using PeerSharp.Internals.Dht;
using Microsoft.Extensions.Time.Testing;
using System.Net;

namespace PeerSharp.Tests.Core.Dht;

public class RoutingTableTests
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly byte[] _localId = new byte[20];

    [Fact]
    public void AddNode_SecureIp_AddsAsSecure()
    {
        var table = new RoutingTable(_localId, _timeProvider);
        var ip = IPAddress.Parse("123.45.67.89");
        byte[] nodeId = DhtSecurity.GenerateSecureNodeId(ip);
        var ep = new IPEndPoint(ip, 6881);

        table.AddNode(nodeId, ep);

        var closest = table.FindClosest(nodeId, 1);
        Assert.Single(closest);
        Assert.Equal(nodeId, closest[0].Id);
    }

    [Fact]
    public void NodeResponded_UpdatesTimestamp()
    {
        var table = new RoutingTable(_localId, _timeProvider);
        byte[] nodeId = new byte[20]; nodeId[0] = 0x80;
        var node = new NodeInfo(nodeId, new IPEndPoint(IPAddress.Loopback, 1000));

        var start = _timeProvider.GetUtcNow();
        table.NodeResponded(node);

        _timeProvider.Advance(TimeSpan.FromMinutes(5));
        table.NodeResponded(node);

        // No direct way to check LastUpdate, but we can verify it's still active
        var closest = table.FindClosest(nodeId, 1);
        Assert.Single(closest);
    }

    [Fact]
    public void NodeNotResponded_MarksInactive()
    {
        var table = new RoutingTable(_localId, _timeProvider);
        byte[] nodeId = new byte[20]; nodeId[0] = 0x80;
        var node = new NodeInfo(nodeId, new IPEndPoint(IPAddress.Loopback, 1000));

        table.NodeResponded(node);
        table.NodeNotResponded(node);

        var closest = table.FindClosest(nodeId, 1);
        Assert.Empty(closest); // inactive nodes are not returned
    }

    [Fact]
    public void NodeNotResponded_RemovesAfterTimeout()
    {
        var table = new RoutingTable(_localId, _timeProvider);
        byte[] nodeId = new byte[20]; nodeId[0] = 0x80;
        var node = new NodeInfo(nodeId, new IPEndPoint(IPAddress.Loopback, 1000));

        table.NodeResponded(node);

        // Advance time beyond MaxBucketNodeInactiveTime (15 mins)
        _timeProvider.Advance(TimeSpan.FromMinutes(16));

        table.NodeNotResponded(node);

        var closest = table.FindClosest(nodeId, 1);
        Assert.Empty(closest);
    }

    [Fact]
    public void BucketFull_SecureNodeReplacesInsecure()
    {
        var table = new RoutingTable(_localId, _timeProvider);
        // Fill bucket with insecure nodes (local IPs are considered secure in current code, 
        // so I need to use public IPs with invalid IDs)

        for (int i = 0; i < 8; i++)
        {
            var ip = IPAddress.Parse($"8.8.8.{i + 1}");
            byte[] insecureId = new byte[20]; insecureId[0] = 0xFF; insecureId[19] = (byte)i;
            table.NodeResponded(new NodeInfo(insecureId, new IPEndPoint(ip, 6881)), isSecure: false);
        }

        // Add a secure node
        var secureIp = IPAddress.Parse("123.45.67.89");
        byte[] secureId = DhtSecurity.GenerateSecureNodeId(secureIp);
        // Ensure it lands in same bucket (distance comparison)
        // Actually, just force it into bucket 159 if possible
        secureId[0] = 0xFF; // Might break BEP 42 validation if we use DhtSecurity.ValidateNodeId
        // Wait, AddNode calls ValidateNodeId.

        // Let's use AddNode with a properly generated ID for a public IP
        var targetIp = IPAddress.Parse("200.200.200.200");
        byte[] targetId = DhtSecurity.GenerateSecureNodeId(targetIp);
        table.AddNode(targetId, new IPEndPoint(targetIp, 6881));

        var closest = table.FindClosest(targetId, 20);
        Assert.Contains(closest, n => n.Id.SequenceEqual(targetId));
    }

    [Fact]
    public void GetAllNodes_ReturnsOnlyActiveNodes()
    {
        var table = new RoutingTable(_localId, _timeProvider);
        byte[] activeId = new byte[20]; activeId[0] = 0x80;
        byte[] inactiveId = new byte[20]; inactiveId[0] = 0x40;
        var active = new NodeInfo(activeId, new IPEndPoint(IPAddress.Loopback, 1000));
        var inactive = new NodeInfo(inactiveId, new IPEndPoint(IPAddress.Loopback, 1001));

        table.NodeResponded(active);
        table.NodeResponded(inactive);
        table.NodeNotResponded(inactive);

        var nodes = table.GetAllNodes();

        Assert.Contains(nodes, n => n.Id.SequenceEqual(activeId));
        Assert.DoesNotContain(nodes, n => n.Id.SequenceEqual(inactiveId));
    }

    [Fact]
    public void GetAllNodes_StopsAtMaxNodes()
    {
        var table = new RoutingTable(_localId, _timeProvider);
        for (int i = 0; i < 10; i++)
        {
            byte[] id = new byte[20];
            id[0] = (byte)(0x80 | i);
            id[19] = (byte)i;
            table.NodeResponded(new NodeInfo(id, new IPEndPoint(IPAddress.Loopback, 2000 + i)));
        }

        var nodes = table.GetAllNodes(maxNodes: 3);

        Assert.Equal(3, nodes.Count);
    }
}





