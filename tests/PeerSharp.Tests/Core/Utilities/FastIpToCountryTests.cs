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

    [Fact]
    public async Task LoadAsync_ReadsEntriesThatShareBufferWithCountrySeparator()
    {
        using var ms = CreateDatabase();
        var geo = new FastIpToCountry();

        await geo.LoadAsync(ms);

        Assert.Equal("US", geo.GetCountry(IPAddress.Parse("1.2.3.4")));
        Assert.Equal("GB", geo.GetCountry(IPAddress.Parse("1.11.0.1")));
        Assert.Equal("", geo.GetCountry(IPAddress.Parse("2.0.0.1")));
    }

    [Fact]
    public async Task LoadAsync_MissingFileLeavesLookupEmpty()
    {
        var geo = new FastIpToCountry();

        await geo.LoadAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.ipdb"));

        Assert.Equal("", geo.GetCountry(IPAddress.Parse("1.2.3.4")));
    }

    [Fact]
    public async Task LoadAsync_PropagatesCancellation()
    {
        var geo = new FastIpToCountry();
        using var stream = new CancellingStream();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => geo.LoadAsync(stream, cts.Token));
    }

    private static MemoryStream CreateDatabase()
    {
        var ms = new MemoryStream();

        ms.Write(Encoding.ASCII.GetBytes("US\n"));
        ms.Write(Encoding.ASCII.GetBytes("GB\n"));
        ms.Write(Encoding.ASCII.GetBytes("\n"));

        WriteEntry(ms, 0, 0x4545);
        WriteEntry(ms, 0x01000000, 0);
        WriteEntry(ms, 0x010A0000, 1);
        WriteEntry(ms, 0, 0x4545);

        ms.Position = 0;
        return ms;
    }

    private static void WriteEntry(Stream s, uint ip, ushort country)
    {
        s.Write(BitConverter.GetBytes(ip));
        s.Write(BitConverter.GetBytes(country));
    }

    private sealed class CancellingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(0);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}





