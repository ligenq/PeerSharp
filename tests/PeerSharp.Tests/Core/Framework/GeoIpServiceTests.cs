using System.Net;
using System.Text;
using PeerSharp.Internals.Framework;

namespace PeerSharp.Tests.Core.Framework;

public class GeoIpServiceTests
{
    [Fact]
    public async Task LoadAsync_CanceledReloadPreservesPublishedDatabase()
    {
        var service = new GeoIpService();
        using var initial = new MemoryStream([85, 83, 10, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 69, 69]);
        service.Load(initial);
        using var cts = new CancellationTokenSource();
        using var replacement = new CancelingStream(cts);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LoadAsync(replacement, cts.Token));
        Assert.True(service.Enabled);
        Assert.Equal("US", service.GetCountry(IPAddress.Parse("0.0.0.1")));
    }

    private sealed class CancelingStream(CancellationTokenSource cts) : MemoryStream([83, 69, 10, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 69, 69])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int count = Read(buffer.Span);
            cts.Cancel();
            return ValueTask.FromResult(count);
        }
    }

    [Fact]
    public void Load_InvalidDatabaseDoesNotEnableService()
    {
        var service = new GeoIpService();
        using var invalid = new MemoryStream(Encoding.ASCII.GetBytes("US\n\n"));
        service.Load(invalid);
        Assert.False(service.Enabled);
    }

    [Fact]
    public async Task LoadAsync_ClearDuringLoadDoesNotRestoreDatabase()
    {
        var service = new GeoIpService();
        using var stream = new ClearingStream(service);
        await service.LoadAsync(stream);
        Assert.False(service.Enabled);
        service.Enabled = true;
        Assert.Equal("", service.GetCountry(IPAddress.Parse("0.0.0.1")));
    }

    private sealed class ClearingStream(GeoIpService service) : MemoryStream([85, 83, 10, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 69, 69])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            service.Clear();
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

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





