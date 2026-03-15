using System.Net;
using System.Text;
using PeerSharp.Internals.Framework;

namespace PeerSharp.Tests.Core.Framework;

public class GeoIpServiceTests
{
    [Fact]
    public void GetCountry_BeforeLoad_ReturnsEmpty()
    {
        var service = new GeoIpService();
        Assert.Equal("", service.GetCountry(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void Load_WithStream_Works()
    {
        var service = new GeoIpService();
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("US\n\n")); // 1 country, empty separator

        // Write one entry for 8.0.0.0 bucket (8)
        for (int i = 0; i < 8; i++) { ms.Write(BitConverter.GetBytes(0u)); ms.Write(BitConverter.GetBytes((ushort)0x4545)); }
        ms.Write(BitConverter.GetBytes(0x08000000u)); ms.Write(BitConverter.GetBytes((ushort)0));
        ms.Write(BitConverter.GetBytes(0u)); ms.Write(BitConverter.GetBytes((ushort)0x4545));

        ms.Position = 0;
        service.Load(ms);

        Assert.Equal("US", service.GetCountry(IPAddress.Parse("8.8.8.8")));
    }
}





