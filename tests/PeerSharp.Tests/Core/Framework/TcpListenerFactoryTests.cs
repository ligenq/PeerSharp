using System.Net;
using System.Net.Sockets;
using PeerSharp.Internals.Framework;

namespace PeerSharp.Tests.Core.Framework;

public class TcpListenerFactoryTests
{
    [Fact]
    public void Create_AnyAddress_UsesIPv6WhenSupported()
    {
        var factory = new TcpListenerFactory();
        var listener = factory.Create(IPAddress.Any, 0);

        try
        {
            listener.Start();
            var endpoint = listener.LocalEndpoint as IPEndPoint;
            Assert.NotNull(endpoint);

            var expectedFamily = Socket.OSSupportsIPv6
                ? AddressFamily.InterNetworkV6
                : AddressFamily.InterNetwork;

            Assert.Equal(expectedFamily, endpoint!.AddressFamily);
        }
        finally
        {
            listener.Stop();
            listener.Dispose();
        }
    }
}





