using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Network;
using System.Net.Sockets;

namespace PeerSharp.Tests.Core.Network;

/// <summary>
/// The listen port fallback.
/// </summary>
/// <remarks>
/// A configured port can be unavailable for reasons the caller cannot see or control - another
/// process, a second copy of their own application, or a Windows dynamic-range reservation that
/// makes an unused port refuse binds with a permission error and moves between reboots. The engine
/// bound to some other port still works; the engine that will not start does not.
/// </remarks>
public class ListenPortBinderTests
{
    [Fact]
    public void TheConfiguredPortIsUsedWhenItIsFree()
    {
        var attempted = new List<int>();

        int bound = Bind(6881, port => { attempted.Add(port); return port; });

        Assert.Equal(6881, bound);
        Assert.Equal([6881], attempted);
    }

    [Fact]
    public void PortZeroIsPassedStraightThrough()
    {
        // 0 already means "let the OS choose", so there is nothing to retry towards.
        var attempted = new List<int>();

        int bound = Bind(0, port => { attempted.Add(port); return port; });

        Assert.Equal(0, bound);
        Assert.Equal([0], attempted);
    }

    [Theory]
    [InlineData(SocketError.AddressAlreadyInUse)]
    [InlineData(SocketError.AccessDenied)]
    public void AnUnavailablePortMovesToTheNextOne(SocketError error)
    {
        var attempted = new List<int>();

        int bound = Bind(6881, port =>
        {
            attempted.Add(port);
            return port == 6881 ? throw new SocketException((int)error) : port;
        });

        Assert.Equal(6882, bound);
        Assert.Equal([6881, 6882], attempted);
    }

    [Fact]
    public void ARunOfUnavailablePortsFallsBackToAnOsAssignedOne()
    {
        var attempted = new List<int>();

        int bound = Bind(6881, port =>
        {
            attempted.Add(port);
            return port == 0 ? 49999 : throw new SocketException((int)SocketError.AccessDenied);
        });

        Assert.Equal(49999, bound);
        Assert.Equal(6881, attempted[0]);
        Assert.Equal(ListenPortBinder.MaxRetries + 1, attempted.Count(port => port != 0));
        Assert.Equal(0, attempted[^1]);
    }

    [Fact]
    public void TheSearchStopsAtTheTopOfThePortRange()
    {
        // Walking past 65535 would ask for a port that cannot exist.
        var attempted = new List<int>();

        int bound = Bind(65534, port =>
        {
            attempted.Add(port);
            return port == 0 ? 49999 : throw new SocketException((int)SocketError.AddressAlreadyInUse);
        });

        Assert.Equal(49999, bound);
        Assert.Equal([65534, 65535, 0], attempted);
    }

    [Fact]
    public void AnUnrelatedSocketFailureIsNotTreatedAsABusyPort()
    {
        // Retrying the next port cannot fix a broken network stack, and swallowing the reason would
        // leave the caller with a working-looking listener on a port nobody expects.
        var attempted = new List<int>();

        var ex = Assert.Throws<SocketException>(() => Bind(6881, port =>
        {
            attempted.Add(port);
            throw new SocketException((int)SocketError.NetworkDown);
        }));

        Assert.Equal(SocketError.NetworkDown, ex.SocketErrorCode);
        Assert.Equal([6881], attempted);
    }

    private static int Bind(int port, Func<int, int> bind)
    {
        return ListenPortBinder.Bind(port, bind, NullLogger.Instance, "TCP");
    }
}
