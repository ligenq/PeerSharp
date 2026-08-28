using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals.Utilities;
using System.Net;
using System.Net.Sockets;

namespace PeerSharp.Tests.Core.Utilities;

/// <summary>
/// What the port mappers do when there is no router willing to talk to them.
/// </summary>
/// <remarks>
/// <para>
/// This is the common case rather than the exceptional one. Most consumer routers speak UPnP and not
/// NAT-PMP, plenty speak neither, and a machine behind carrier-grade NAT or a VPN has nothing to map
/// against at all. Coverage showed these paths never ran: the tests exercised gateways that answer,
/// and every branch for a gateway that does not was untested.
/// </para>
/// <para>
/// Failing here has to be quiet and quick. A mapper that throws takes the engine's startup with it,
/// and one that waits on a silent gateway delays every torrent behind it.
/// </para>
/// </remarks>
public sealed class PortMapperFallbackTests
{
    [Fact(Timeout = 30_000)]
    public async Task MappingBeforeAnyGatewayIsFoundFails()
    {
        // MapPortAsync is reachable before StartAsync has run, and with no gateway there is nothing
        // to ask - it must answer rather than send to nowhere.
        var mapper = new NatPmpPortMapping(() => [], 5351);

        Assert.False(await mapper.MapPortAsync(6881, "TCP", "test", TestContext.Current.CancellationToken));
        Assert.DoesNotContain(mapper.GetStatus(), s => s.Result == PortMappingResult.Success);
    }

    [Fact(Timeout = 30_000)]
    public async Task UnmappingWithoutGatewaysDoesNothing()
    {
        var mapper = new NatPmpPortMapping(() => [], 5351);
        await mapper.StartAsync(TestContext.Current.CancellationToken);

        // Must not throw, and must not attempt to send: there is no gateway and nothing was mapped.
        await mapper.UnmapAllAsync(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 30_000)]
    public async Task AGatewayThatNeverAnswersTimesOutAndReportsFailure()
    {
        // The branch that runs for every router that ignores NAT-PMP. The receive deadline is built
        // from the injected clock, so the wait is advanced rather than waited out.
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)silent.Client.LocalEndPoint!).Port;

        var time = new FakeTimeProvider();
        var mapper = new NatPmpPortMapping(() => [IPAddress.Loopback], port, time);
        await mapper.StartAsync(TestContext.Current.CancellationToken);

        var mapping = mapper.MapPortAsync(6881, "TCP", "test", TestContext.Current.CancellationToken);

        // Let the request reach the socket that will never reply, then run the deadline out.
        while (!mapping.IsCompleted && silent.Available == 0)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        time.Advance(TimeSpan.FromSeconds(5));

        Assert.False(await mapping);

        var status = Assert.Single(mapper.GetStatus());
        Assert.Equal(PortMappingResult.Failed, status.Result);
        Assert.Null(status.ExternalPort);
    }

    [Fact(Timeout = 30_000)]
    public async Task CancellingAMappingIsNotReportedAsAFault()
    {
        // Shutdown while a mapping is in flight. The cancellation is the caller's, so it must come
        // back as a plain failure rather than escaping into the engine's startup path.
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)silent.Client.LocalEndPoint!).Port;

        var mapper = new NatPmpPortMapping(() => [IPAddress.Loopback], port, new FakeTimeProvider());
        await mapper.StartAsync(TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        var mapping = mapper.MapPortAsync(6881, "UDP", "test", cts.Token);

        while (!mapping.IsCompleted && silent.Available == 0)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await cts.CancelAsync();

        Assert.False(await mapping);
    }

    [Fact(Timeout = 30_000)]
    public async Task TheDefaultMapperFindsThisMachinesGatewaysWithoutTalkingToThem()
    {
        // The parameterless constructor is what the engine uses, and it had no test: nothing checked
        // that its gateway discovery filters to routable IPv4 addresses. Only StartAsync is called
        // here, which enumerates interfaces and sends nothing - deliberately, since mapping a port
        // would put real packets on whatever network this runs on.
        var mapper = new NatPmpPortMapping();

        await mapper.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal("NAT-PMP", mapper.Name);

        // Nothing has been mapped, so the only status this can report is that it found no gateway.
        // Whichever it is, it must not have attempted or claimed a mapping.
        Assert.DoesNotContain(mapper.GetStatus(), status => status.Result == PortMappingResult.Success);
        Assert.All(mapper.GetStatus(), status => Assert.Null(status.ExternalPort));
    }

    [Fact(Timeout = 30_000)]
    public void TheDefaultUpnpMapperReportsNoGatewaysBeforeDiscovery()
    {
        // Constructed but never started. Discovery itself is left alone on purpose: it multicasts
        // SSDP onto whatever network the tests run on, which is a side effect a unit test should not
        // have and an assertion that would depend on the router in the room.
        var mapper = new UpnpPortMapping();

        Assert.Equal("UPnP", mapper.Name);

        var status = Assert.Single(mapper.GetStatus());
        Assert.Equal(PortMappingResult.Failed, status.Result);
    }
}
