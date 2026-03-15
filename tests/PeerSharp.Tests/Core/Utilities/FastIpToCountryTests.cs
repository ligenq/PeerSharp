using PeerSharp.Internals.Utilities;
using System.Net;
using System.Text;

namespace PeerSharp.Tests.Core.Utilities;

public class FastIpToCountryTests
{
    [Fact]
    public void GetCountry_FindsCorrectCountry()
    {
        // Construct a mock database
        var ms = new MemoryStream();

        // Phase 1: Countries
        ms.Write(Encoding.ASCII.GetBytes("US\n"));
        ms.Write(Encoding.ASCII.GetBytes("GB\n"));
        ms.Write(Encoding.ASCII.GetBytes("\n")); // Separator

        // Phase 2: Buckets
        // Let's target IP 1.2.3.4 -> Bucket 1.

        // Fill bucket 0 (dummy separator)
        WriteEntry(ms, 0, 0x4545);

        // Bucket 1: 1.0.0.0 -> US (0), 1.10.0.0 -> GB (1)
        WriteEntry(ms, 0x01000000, 0); // 1.0.0.0
        WriteEntry(ms, 0x010A0000, 1); // 1.10.0.0
        WriteEntry(ms, 0, 0x4545); // End of bucket 1

        ms.Position = 0;
        var geo = new FastIpToCountry();
        geo.Load(ms);

        Assert.Equal("US", geo.GetCountry(IPAddress.Parse("1.2.3.4")));
        Assert.Equal("GB", geo.GetCountry(IPAddress.Parse("1.11.0.1")));
        Assert.Equal("", geo.GetCountry(IPAddress.Parse("2.0.0.1"))); // Bucket 2 empty
    }

    private static void WriteEntry(Stream s, uint ip, ushort country)
    {
        s.Write(BitConverter.GetBytes(ip));
        s.Write(BitConverter.GetBytes(country));
    }
}





