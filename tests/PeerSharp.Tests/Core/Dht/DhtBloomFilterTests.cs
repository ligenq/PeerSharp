using PeerSharp.Internals.Dht;
using System.Net;

namespace PeerSharp.Tests.Core.Dht;

public class DhtBloomFilterTests
{
    [Fact]
    public void EmptyFilter_EstimateZero()
    {
        var filter = new DhtBloomFilter();
        Assert.Equal(0, filter.EstimateCount());
    }

    [Fact]
    public void Add_Ip_MightContain()
    {
        var filter = new DhtBloomFilter();
        var ip = IPAddress.Parse("1.2.3.4");

        filter.Add(ip);

        Assert.True(filter.MightContain(ip));
    }

    [Fact]
    public void Add_MultipleIps_MightContainAll()
    {
        var filter = new DhtBloomFilter();
        var ips = new[]
        {
            IPAddress.Parse("1.1.1.1"),
            IPAddress.Parse("2.2.2.2"),
            IPAddress.Parse("3.3.3.3")
        };

        foreach (var ip in ips)
        {
            filter.Add(ip);
        }

        foreach (var ip in ips)
        {
            Assert.True(filter.MightContain(ip));
        }
    }

    [Fact]
    public void EstimateCount_ReturnsApproximateValue()
    {
        var filter = new DhtBloomFilter();
        int count = 100;
        for (int i = 0; i < count; i++)
        {
            filter.AddBytes(BitConverter.GetBytes(i));
        }

        int estimate = filter.EstimateCount();
        // Allow 10% error margin for bloom filter estimate
        Assert.True(estimate >= count * 0.9 && estimate <= count * 1.1, $"Estimate {estimate} too far from {count}");
    }

    [Fact]
    public void GetBytes_ReturnsClone()
    {
        var filter = new DhtBloomFilter();
        filter.AddBytes(new byte[] { 1, 2, 3 });

        byte[] data = filter.GetBytes();
        Assert.Equal(DhtBloomFilter.FilterSizeBytes, data.Length);

        data[0]++;
        Assert.NotEqual(data[0], filter.GetBytes()[0]);
    }

    [Fact]
    public void Clear_ResetsFilter()
    {
        var filter = new DhtBloomFilter();
        filter.AddBytes(new byte[] { 1, 2, 3 });
        Assert.NotEqual(0, filter.EstimateCount());

        filter.Clear();
        Assert.Equal(0, filter.EstimateCount());
    }

    [Fact]
    public void Constructor_WithData_InitializesCorrectly()
    {
        var filter1 = new DhtBloomFilter();
        filter1.AddBytes(new byte[] { 4, 5, 6 });
        byte[] data = filter1.GetBytes();

        var filter2 = new DhtBloomFilter(data);
        Assert.Equal(filter1.EstimateCount(), filter2.EstimateCount());
        Assert.Equal(data, filter2.GetBytes());
    }
}





