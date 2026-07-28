using PeerSharp.Core;
using PeerSharp.Internals.Dht;
using System.Net;

namespace PeerSharp.Tests.Core.Dht;

/// <summary>
/// Cancelling a BEP 44 lookup while its queries are outstanding.
///
/// <para>
/// A node that never answers is ordinary in a DHT, so an unanswered query returns null and the lookup
/// moves on. The caller cancelling is a different thing entirely, and must not be reported the same
/// way: null from a BEP 46 resolve means the publisher key names nothing, which is a fact about the
/// world rather than about the caller's token.
/// </para>
///
/// <para>
/// Both used to arrive as null, because the per-query handler caught every
/// <see cref="OperationCanceledException"/>. The enclosing loop re-checks the token each round, so this
/// only escaped when cancellation landed during the final round or the candidates ran out immediately
/// after - narrow, and silent when it happened.
/// </para>
/// </summary>
public class DhtItemLookupCancellationTests
{
    /// <summary>
    /// Every node here is a documentation-range address that will never reply, so the lookup is still
    /// waiting on its queries when the token is cancelled.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task GetItemAsync_CancelledMidLookup_ThrowsRatherThanReportingNotFound()
    {
        await using var dht = await CreateDhtAsync();
        using var cts = new CancellationTokenSource();

        var lookup = dht.GetItemAsync(RandomTarget(), salt: null, cts.Token);

        // Let the first round of queries go out before cancelling, so cancellation lands while they
        // are in flight rather than before the lookup starts.
        await Task.Delay(200);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
    }

    [Fact(Timeout = 60000)]
    public async Task GetItemAsync_CancelledBeforeStarting_Throws()
    {
        await using var dht = await CreateDhtAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => dht.GetItemAsync(RandomTarget(), salt: null, cts.Token));
    }

    /// <summary>
    /// The control. Nodes that simply never answer are not cancellation, and must still produce a
    /// plain "not found" rather than an exception - otherwise the fix above would have turned every
    /// unresponsive DHT into a fault.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task GetItemAsync_NobodyAnswers_ReturnsNullWithoutThrowing()
    {
        await using var dht = await CreateDhtAsync();

        var item = await dht.GetItemAsync(RandomTarget(), salt: null, CancellationToken.None);

        Assert.Null(item);
    }

    private static async Task<DhtManager> CreateDhtAsync()
    {
        var transport = new DhtLoopbackFixture.LoopbackTransport
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 6881),
        };

        var settings = new Settings();
        settings.Dht.BootstrapNodes = [];
        settings.Dht.InitialState = new DhtState(InfoHash.CreateRandom().ToArray(), CreateSilentNodes());

        var dht = new DhtManager(InfoHash.CreateRandom(), transport, settings, TimeProvider.System);
        await dht.StartAsync();
        return dht;
    }

    /// <summary>Six nodes in the TEST-NET-1 range, enough for the lookup to consider the table usable.</summary>
    private static DhtNode[] CreateSilentNodes() => [.. Enumerable.Range(1, 6).Select(i =>
    {
        var id = new byte[DhtTarget.Length];
        id[^1] = (byte)i;
        return new DhtNode(id, new IPEndPoint(IPAddress.Parse($"192.0.2.{i}"), 6000 + i));
    })];

    private static DhtTarget RandomTarget() => new(InfoHash.CreateRandom().Span);
}
