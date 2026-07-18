using System.Net;
using PeerSharp.Internals.Network;

namespace PeerSharp.Tests.Core.Network;

public class IpBlocklistTests
{
    [Fact]
    public void IsBlocked_ReturnsFalseWhenDisabled()
    {
        var blocklist = new IpBlocklist { Enabled = false };
        blocklist.AddRange(IPAddress.Parse("1.1.1.1"), IPAddress.Parse("1.1.1.10"));

        Assert.False(blocklist.IsBlocked(IPAddress.Parse("1.1.1.5")));
    }

    [Fact]
    public void IsBlocked_IPv4_SingleIp()
    {
        var blocklist = new IpBlocklist { Enabled = true };
        blocklist.AddRange(IPAddress.Parse("1.2.3.4"), IPAddress.Parse("1.2.3.4"));

        Assert.True(blocklist.IsBlocked(IPAddress.Parse("1.2.3.4")));
        Assert.False(blocklist.IsBlocked(IPAddress.Parse("1.2.3.5")));
    }

    [Fact]
    public void IsBlocked_IPv4_Range()
    {
        var blocklist = new IpBlocklist { Enabled = true };
        blocklist.AddRange(IPAddress.Parse("192.168.1.100"), IPAddress.Parse("192.168.1.200"));

        Assert.True(blocklist.IsBlocked(IPAddress.Parse("192.168.1.100")));
        Assert.True(blocklist.IsBlocked(IPAddress.Parse("192.168.1.150")));
        Assert.True(blocklist.IsBlocked(IPAddress.Parse("192.168.1.200")));
        Assert.False(blocklist.IsBlocked(IPAddress.Parse("192.168.1.99")));
        Assert.False(blocklist.IsBlocked(IPAddress.Parse("192.168.1.201")));
    }

    [Fact]
    public void IsBlocked_IPv6_Range()
    {
        var blocklist = new IpBlocklist { Enabled = true };
        blocklist.AddRange(IPAddress.Parse("2001:db8::1"), IPAddress.Parse("2001:db8::10"));

        Assert.True(blocklist.IsBlocked(IPAddress.Parse("2001:db8::5")));
        Assert.False(blocklist.IsBlocked(IPAddress.Parse("2001:db8::11")));
    }

    [Fact]
    public void AddCidr_Works()
    {
        var blocklist = new IpBlocklist { Enabled = true };
        blocklist.AddCidr("10.0.0.0/24");

        Assert.True(blocklist.IsBlocked(IPAddress.Parse("10.0.0.1")));
        Assert.True(blocklist.IsBlocked(IPAddress.Parse("10.0.0.255")));
        Assert.False(blocklist.IsBlocked(IPAddress.Parse("10.0.1.0")));
    }

    [Fact]
    public void LoadFromStream_SupportsMultipleFormats()
    {
        var content = string.Join("\n", new[]
        {
            "# Comment",
            "// Another comment",
            "Simple Block:1.1.1.1-1.1.1.10",
            "10.0.0.0/8",
            "192.168.1.50"
        });

        var blocklist = new IpBlocklist();
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        blocklist.LoadFromStream(stream);

        Assert.True(blocklist.Enabled);
        Assert.True(blocklist.IsBlocked("1.1.1.5"));
        Assert.True(blocklist.IsBlocked("10.255.255.255"));
        Assert.True(blocklist.IsBlocked("192.168.1.50"));
        Assert.False(blocklist.IsBlocked("1.1.1.11"));
        Assert.False(blocklist.IsBlocked("11.0.0.1"));
    }

    [Fact]
    public async Task LoadFromStreamAsync_SupportsMultipleFormatsAndLeavesStreamOpen()
    {
        var content = string.Join("\n", new[]
        {
            "# Comment",
            "// Another comment",
            "Simple Block:1.1.1.1-1.1.1.10",
            "10.0.0.0/8",
            "192.168.1.50",
            "not a range"
        });

        var blocklist = new IpBlocklist();
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));

        int count = await blocklist.LoadFromStreamAsync(stream, TestContext.Current.CancellationToken);

        Assert.Equal(3, count);
        Assert.Equal(3, blocklist.RangeCount);
        Assert.True(blocklist.Enabled);
        Assert.True(stream.CanRead);
        Assert.True(blocklist.IsBlocked("1.1.1.5"));
        Assert.True(blocklist.IsBlocked("10.255.255.255"));
        Assert.True(blocklist.IsBlocked("192.168.1.50"));
        Assert.False(blocklist.IsBlocked("1.1.1.11"));
        Assert.False(blocklist.IsBlocked("11.0.0.1"));
    }

    [Fact]
    public async Task LoadFromStreamAsync_CancellationRequested_ThrowsAndDoesNotEnable()
    {
        var blocklist = new IpBlocklist();
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("1.1.1.1"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => blocklist.LoadFromStreamAsync(stream, cts.Token));

        Assert.False(blocklist.Enabled);
        Assert.Equal(0, blocklist.RangeCount);
    }

    [Fact]
    public async Task LoadFromStreamAsync_ReadFailure_ReturnsZeroAndDoesNotEnable()
    {
        var blocklist = new IpBlocklist();
        using var stream = new ThrowingReadStream();

        int count = await blocklist.LoadFromStreamAsync(stream, TestContext.Current.CancellationToken);

        Assert.Equal(0, count);
        Assert.False(blocklist.Enabled);
        Assert.Equal(0, blocklist.RangeCount);
    }

    [Fact]
    public void IsBlocked_NestedRanges_MatchesContainingRange()
    {
        // A wide range whose Start precedes several narrow ranges. Binary search over
        // ranges sorted by Start must not skip the containing range when a mid probe
        // lands on a later, narrower range whose End is below the queried IP.
        var blocklist = new IpBlocklist { Enabled = true };
        blocklist.AddRange(IPAddress.Parse("10.0.0.0"), IPAddress.Parse("10.0.255.255"));
        blocklist.AddRange(IPAddress.Parse("10.0.0.5"), IPAddress.Parse("10.0.0.6"));
        blocklist.AddRange(IPAddress.Parse("10.0.0.7"), IPAddress.Parse("10.0.0.8"));

        // 10.0.128.0 is only inside the wide first range
        Assert.True(blocklist.IsBlocked(IPAddress.Parse("10.0.128.0")));
        Assert.True(blocklist.IsBlocked(IPAddress.Parse("10.0.255.255")));
        Assert.False(blocklist.IsBlocked(IPAddress.Parse("10.1.0.0")));
    }

    [Fact]
    public void IsBlocked_OverlappingRanges_MatchesAcrossSeam()
    {
        var blocklist = new IpBlocklist { Enabled = true };
        blocklist.AddRange(IPAddress.Parse("1.0.0.0"), IPAddress.Parse("1.0.0.100"));
        blocklist.AddRange(IPAddress.Parse("1.0.0.50"), IPAddress.Parse("1.0.0.200"));

        Assert.True(blocklist.IsBlocked(IPAddress.Parse("1.0.0.0")));
        Assert.True(blocklist.IsBlocked(IPAddress.Parse("1.0.0.150")));
        Assert.True(blocklist.IsBlocked(IPAddress.Parse("1.0.0.200")));
        Assert.False(blocklist.IsBlocked(IPAddress.Parse("1.0.0.201")));
    }

    [Fact]
    public void IsBlocked_AdjacentRanges_AreCoalesced()
    {
        var blocklist = new IpBlocklist { Enabled = true };
        blocklist.AddRange(IPAddress.Parse("2.0.0.0"), IPAddress.Parse("2.0.0.10"));
        blocklist.AddRange(IPAddress.Parse("2.0.0.11"), IPAddress.Parse("2.0.0.20"));

        Assert.True(blocklist.IsBlocked(IPAddress.Parse("2.0.0.10")));
        Assert.True(blocklist.IsBlocked(IPAddress.Parse("2.0.0.11")));
        Assert.True(blocklist.IsBlocked(IPAddress.Parse("2.0.0.20")));
        Assert.False(blocklist.IsBlocked(IPAddress.Parse("2.0.0.21")));
    }

    [Fact]
    public void Clear_RemovesAllRangesAndDisables()
    {
        var blocklist = new IpBlocklist();
        blocklist.Enabled = true;
        blocklist.AddCidr("1.1.1.0/24");
        Assert.True(blocklist.IsBlocked("1.1.1.1"));

        blocklist.Clear();
        Assert.False(blocklist.Enabled);
        Assert.False(blocklist.IsBlocked("1.1.1.1"));
        Assert.Equal(0, blocklist.RangeCount);
    }

    private sealed class ThrowingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new IOException("read failed");
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            throw new IOException("read failed");
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}





