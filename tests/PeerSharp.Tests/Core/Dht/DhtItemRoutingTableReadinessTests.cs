using PeerSharp.Core;
using PeerSharp.Internals.Dht;
using System.Net;

namespace PeerSharp.Tests.Core.Dht;

public class DhtItemRoutingTableReadinessTests
{
    [Fact]
    public async Task WaitForUsableItemRoutingTableAsync_WaitsWhileTableIsCold()
    {
        await using var dht = await CreateDhtAsync(initialNodes: []);
        using var cts = new CancellationTokenSource();

        var wait = dht.WaitForUsableItemRoutingTableAsync(RandomTarget(), cts.Token);

        Assert.False(wait.IsCompleted);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public async Task WaitForUsableItemRoutingTableAsync_CompletesWithSixActiveCandidates()
    {
        await using var dht = await CreateDhtAsync(CreateActiveNodes());

        var wait = dht.WaitForUsableItemRoutingTableAsync(RandomTarget());

        Assert.True(wait.IsCompletedSuccessfully);
        await wait;
    }

    [Fact]
    public async Task WaitForUsableItemRoutingTableAsync_HonorsCancellationWhenTableIsWarm()
    {
        await using var dht = await CreateDhtAsync(CreateActiveNodes());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => dht.WaitForUsableItemRoutingTableAsync(RandomTarget(), cts.Token));
    }

    private static async Task<DhtManager> CreateDhtAsync(IReadOnlyList<DhtNode> initialNodes)
    {
        var transport = new DhtLoopbackFixture.LoopbackTransport
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 6881),
        };
        var settings = new Settings();
        settings.Dht.BootstrapNodes = [];
        settings.Dht.InitialState = new DhtState(InfoHash.CreateRandom().ToArray(), initialNodes);

        var dht = new DhtManager(InfoHash.CreateRandom(), transport, settings, TimeProvider.System);
        await dht.StartAsync();
        return dht;
    }

    private static byte[] NodeId(int suffix)
    {
        var id = new byte[DhtTarget.Length];
        id[^1] = (byte)suffix;
        return id;
    }

    private static DhtNode[] CreateActiveNodes() => Enumerable.Range(1, 6)
        .Select(i => new DhtNode(
            NodeId(i),
            new IPEndPoint(IPAddress.Parse($"192.0.2.{i}"), 6000 + i)))
        .ToArray();

    private static DhtTarget RandomTarget() => new(InfoHash.CreateRandom().Span);
}
