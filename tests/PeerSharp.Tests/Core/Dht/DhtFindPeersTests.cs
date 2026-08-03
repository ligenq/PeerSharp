using System.Net;
using PeerSharp.Core;
using PeerSharp.Internals.Dht;

namespace PeerSharp.Tests.Core.Dht;

/// <summary>
/// What <see cref="DhtManager.FindPeers"/> reports back, which is what makes it safe to schedule.
///
/// <para>
/// A torrent starts moments after the DHT is told to bootstrap, so its routing table is still empty
/// and there is nobody to ask. That used to be indistinguishable from a lookup that had been sent:
/// the method returned void, the caller assumed it was done, and nothing asked again - so a torrent
/// whose tracker returns few peers stayed at zero forever. The count is what lets the caller retry
/// while the table is empty and settle down once it is not.
/// </para>
/// </summary>
public class DhtFindPeersTests
{
    [Fact(Timeout = 30000)]
    public async Task AnEmptyRoutingTableQueriesNobody()
    {
        // Deliberately not the loopback fixture: that one pings its peer during construction, so its
        // client already knows a node. This is the state a real client is in for the first seconds
        // after start, when bootstrap has been asked for but nothing has replied yet.
        var settings = new Settings();
        settings.Dht.BootstrapNodes = [];

        var transport = new DhtLoopbackFixture.LoopbackTransport
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.9"), 6881)
        };

        await using var dht = new DhtManager(InfoHash.CreateRandom(), transport, settings, TimeProvider.System);
        await dht.StartAsync();

        Assert.Equal(0, dht.FindPeers(InfoHash.CreateRandom()));
    }

    [Fact(Timeout = 30000)]
    public async Task AKnownNodeIsQueried()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();

        var sent = new List<int>();
        fixture.ClientTransport.OnSend = data => sent.Add(data.Length);

        // Reach the server once so the client learns about it, then ask for peers.
        await fixture.Client.FindNodeAsync(
            fixture.ServerEndPoint,
            new DhtTarget(InfoHash.CreateRandom().Span));

        int queried = fixture.Client.FindPeers(InfoHash.CreateRandom());

        Assert.True(queried > 0, "A node the client has already spoken to should be asked for peers.");
        Assert.NotEmpty(sent);
    }
}
