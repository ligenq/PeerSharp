using Microsoft.Extensions.Time.Testing;
using PeerSharp.Config;
using PeerSharp.Internals.Utilities;
using System.Net;

namespace PeerSharp.Tests.Core.Network;

public class NatPmpPortMappingTests
{
    [Fact]
    public void TheMapperNamesItselfByTheProtocolItSpeaks()
    {
        // The name goes into PortMappingStatus, which is how a caller with both mappers enabled
        // tells which one answered.
        Assert.Equal("NAT-PMP", new NatPmpPortMapping().Name);
    }

    [Fact]
    public async Task WithNoGatewayThereIsNothingToMapAndTheStatusSaysWhy()
    {
        // A machine with no NAT-PMP router is the ordinary case, not a failure to report loudly.
        var mapper = new NatPmpPortMapping(() => [], 5351);

        await mapper.StartAsync(TestContext.Current.CancellationToken);

        Assert.False(await mapper.MapPortAsync(6881, "TCP", "test", TestContext.Current.CancellationToken));

        var status = Assert.Single(mapper.GetStatus());
        Assert.Equal("NAT-PMP", status.Protocol);
        Assert.Equal(PortMappingResult.Failed, status.Result);
        Assert.Null(status.ExternalPort);
        Assert.Equal("No gateways discovered", status.ErrorMessage);
    }

    [Fact]
    public async Task AGatewayThatNeverAnswersIsReportedAsAFailedMappingRatherThanHanging()
    {
        // TEST-NET-1 is guaranteed unroutable, so this exercises the timeout path rather than
        // whatever router happens to be on the developer's network.
        var clock = new FakeTimeProvider();
        var mapper = new NatPmpPortMapping(
            () => [IPAddress.Parse("192.0.2.1")],
            natPmpPort: 5351,
            timeProvider: clock);

        await mapper.StartAsync(TestContext.Current.CancellationToken);
        var mapping = mapper.MapPortAsync(6881, "TCP", "test", TestContext.Current.CancellationToken);

        await TorrentTestUtility.AdvanceUntilAsync(clock, () => mapping.IsCompleted, TimeSpan.FromSeconds(1));

        Assert.False(await mapping);
        Assert.Contains(mapper.GetStatus(), s => s.Result == PortMappingResult.Failed);
    }

    [Fact]
    public async Task StartAsyncHonoursAnAlreadyCancelledToken()
    {
        var mapper = new NatPmpPortMapping(() => [], 5351);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mapper.StartAsync(cts.Token));
    }

    [Fact]
    public async Task UnmappingWithNothingMappedIsSafe()
    {
        // Called on every shutdown, including one where startup never found a gateway.
        var mapper = new NatPmpPortMapping(() => [], 5351);

        await mapper.UnmapAllAsync(TestContext.Current.CancellationToken);
    }
}

public class UpnpPortMappingTests
{
    [Fact]
    public void TheMapperNamesItselfByTheProtocolItSpeaks()
    {
        Assert.Equal("UPnP", new UpnpPortMapping().Name);
    }

    [Fact]
    public async Task WithNoGatewayDiscoveredThereIsNothingToMapAndTheStatusSaysWhy()
    {
        var mapper = new UpnpPortMapping(_ => Task.FromResult(new List<UpnpGateway>()));

        await mapper.StartAsync(TestContext.Current.CancellationToken);

        Assert.False(await mapper.MapPortAsync(6881, "TCP", "test", TestContext.Current.CancellationToken));

        var status = Assert.Single(mapper.GetStatus());
        Assert.Equal("UPnP", status.Protocol);
        Assert.Equal(PortMappingResult.Failed, status.Result);
        Assert.Equal("No gateways discovered", status.ErrorMessage);
    }

    [Fact]
    public async Task DiscoveryFailingSurfacesToTheCallerRatherThanBeingSwallowedHere()
    {
        // Discovery is a probe on a network we know nothing about, so it fails often. The mapper
        // reports that upward rather than deciding on its own that a failed probe is fine:
        // NetworkManager catches it per mapper, which is where the "carry on without a mapping"
        // decision belongs, and where the other mappers still get their turn.
        var mapper = new UpnpPortMapping(_ => Task.FromException<List<UpnpGateway>>(new HttpRequestException("no")));

        await Assert.ThrowsAsync<HttpRequestException>(() => mapper.StartAsync(TestContext.Current.CancellationToken));

        // And the mapper is still usable afterwards: no gateway, so nothing to map.
        Assert.False(await mapper.MapPortAsync(6881, "TCP", "test", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AGatewayThatCannotBeReachedIsReportedPerGateway()
    {
        // Several gateways is normal on a machine with more than one interface, and the status is
        // reported per gateway so a caller can see which one refused.
        var gateway = new UpnpGateway
        {
            Name = "TestRouter",
            ControlUrl = "http://192.0.2.1:1900/control",
            ServiceType = "urn:schemas-upnp-org:service:WANIPConnection:1",
            LocalAddress = IPAddress.Loopback
        };
        var mapper = new UpnpPortMapping(_ => Task.FromResult(new List<UpnpGateway> { gateway }));

        await mapper.StartAsync(TestContext.Current.CancellationToken);
        await mapper.MapPortAsync(6881, "TCP", "test", TestContext.Current.CancellationToken);

        Assert.Contains(mapper.GetStatus(), s => s.Protocol.Contains("TestRouter", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnmappingWithNothingMappedIsSafe()
    {
        var mapper = new UpnpPortMapping(_ => Task.FromResult(new List<UpnpGateway>()));

        await mapper.UnmapAllAsync(TestContext.Current.CancellationToken);
    }
}

public class UpnpDiscoveryTests
{
    [Fact]
    public async Task DiscoveryOnAHostWithNoRouterCompletesEmptyRatherThanThrowing()
    {
        // The probe is a multicast datagram nobody may answer, which is what a machine behind a
        // switch or a cloud VM looks like. An empty list is the answer; an exception would take
        // engine startup with it.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var gateways = await UpnpDiscovery.DiscoverAsync(cts.Token);

        Assert.NotNull(gateways);
    }

    [Fact]
    public async Task ParsingADescriptionFromAnUnreachableAddressYieldsNothing()
    {
        // The description URL comes from whatever answered the multicast probe, so it is entirely
        // attacker-controlled. Failing to fetch it must produce "no gateway", not an exception.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var gateway = await UpnpDiscovery.ParseDescriptionAsync(
            "http://192.0.2.1:1900/description.xml", IPAddress.Loopback, cts.Token);

        Assert.Null(gateway);
    }

    [Fact]
    public void AGatewayCarriesTheFieldsAControlRequestNeeds()
    {
        var gateway = new UpnpGateway
        {
            Name = "Router",
            ControlUrl = "http://10.0.0.1:5000/ctl",
            ServiceType = "urn:schemas-upnp-org:service:WANIPConnection:1",
            LocalAddress = IPAddress.Parse("10.0.0.2")
        };

        Assert.Equal("Router", gateway.Name);
        Assert.Equal("http://10.0.0.1:5000/ctl", gateway.ControlUrl);
        Assert.Contains("WANIPConnection", gateway.ServiceType, StringComparison.Ordinal);
        Assert.Equal(IPAddress.Parse("10.0.0.2"), gateway.LocalAddress);
    }

    [Fact]
    public void AFreshGatewayHasTheEmptyDefaultsRatherThanNulls()
    {
        var gateway = new UpnpGateway();

        Assert.Equal(string.Empty, gateway.Name);
        Assert.Equal(string.Empty, gateway.ControlUrl);
        Assert.Equal(string.Empty, gateway.ServiceType);
        Assert.Equal(IPAddress.Any, gateway.LocalAddress);
    }
}
