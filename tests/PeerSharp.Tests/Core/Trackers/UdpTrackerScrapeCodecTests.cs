using PeerSharp.Internals.Trackers;
using System.Buffers.Binary;
using System.Text;

namespace PeerSharp.Tests.Core.Trackers;

public class UdpTrackerScrapeCodecTests
{
    [Fact]
    public void BuildRequest_WritesHeaderAndLimitsHashes()
    {
        var hashes = Enumerable.Range(0, 80).Select(Hash).ToList();

        byte[] request = UdpTrackerScrapeCodec.BuildRequest(0x0102030405060708, 0x11223344, hashes);

        Assert.Equal(16 + (UdpTrackerScrapeCodec.MaxHashesPerRequest * InfoHash.V1Length), request.Length);
        Assert.Equal(0x0102030405060708, BinaryPrimitives.ReadInt64BigEndian(request.AsSpan(0)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(8)));
        Assert.Equal(0x11223344, BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(12)));
        Assert.Equal(hashes[0], request.AsSpan(16, InfoHash.V1Length).ToArray());
        Assert.Equal(hashes[73], request.AsSpan(16 + (73 * InfoHash.V1Length), InfoHash.V1Length).ToArray());
    }

    [Fact]
    public void BuildRequest_RejectsNonV1HashLength()
    {
        Assert.Throws<ArgumentException>(() =>
            UdpTrackerScrapeCodec.BuildRequest(1, 2, new[] { new byte[32] }));
    }

    [Fact]
    public void ParseResponse_ReturnsStatsForRequestedHashes()
    {
        byte[] hash1 = Hash(1);
        byte[] hash2 = Hash(2);
        byte[] response = Response(transactionId: 123, action: 2, (10, 20, 30), (1, 2, 3));

        var parsed = UdpTrackerScrapeCodec.ParseResponse(response, 123, new[] { hash1, hash2 });

        Assert.Equal(2, parsed.Results.Count);
        Assert.Equal(10u, parsed.Results[Convert.ToHexString(hash1)].SeedCount);
        Assert.Equal(20u, parsed.Results[Convert.ToHexString(hash1)].Downloaded);
        Assert.Equal(30u, parsed.Results[Convert.ToHexString(hash1)].LeechCount);
        Assert.Equal(1u, parsed.Results[Convert.ToHexString(hash2)].SeedCount);
        Assert.Equal(2u, parsed.Results[Convert.ToHexString(hash2)].Downloaded);
        Assert.Equal(3u, parsed.Results[Convert.ToHexString(hash2)].LeechCount);
    }

    [Fact]
    public void ParseResponse_TransactionMismatch_IsTransientTrackerException()
    {
        byte[] response = Response(transactionId: 999, action: 2, (10, 20, 30));

        var ex = Assert.Throws<UdpTrackerException>(() =>
            UdpTrackerScrapeCodec.ParseResponse(response, 123, new[] { Hash(1) }));

        Assert.True(ex.IsTransient);
        Assert.Contains("Transaction ID mismatch", ex.Message);
    }

    [Fact]
    public void ParseResponse_ErrorAction_UsesTrackerErrorMessage()
    {
        byte[] error = new byte[8 + 3];
        BinaryPrimitives.WriteInt32BigEndian(error.AsSpan(0), 3);
        BinaryPrimitives.WriteInt32BigEndian(error.AsSpan(4), 123);
        Encoding.ASCII.GetBytes("bad").CopyTo(error.AsSpan(8));

        var ex = Assert.Throws<UdpTrackerException>(() =>
            UdpTrackerScrapeCodec.ParseResponse(error, 123, new[] { Hash(1) }));

        Assert.False(ex.IsTransient);
        Assert.Contains("bad", ex.Message);
    }

    [Fact]
    public void ParseResponse_InvalidAction_ThrowsInvalidData()
    {
        byte[] response = Response(transactionId: 123, action: 1, (10, 20, 30));

        var ex = Assert.Throws<InvalidDataException>(() =>
            UdpTrackerScrapeCodec.ParseResponse(response, 123, new[] { Hash(1) }));

        Assert.Contains("Invalid scrape action", ex.Message);
    }

    [Fact]
    public void ParseResponse_TooShort_ThrowsInvalidData()
    {
        byte[] response = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(0), 2);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(4), 123);

        var ex = Assert.Throws<InvalidDataException>(() =>
            UdpTrackerScrapeCodec.ParseResponse(response, 123, new[] { Hash(1) }));

        Assert.Contains("Scrape response too short", ex.Message);
    }

    [Fact]
    public void ParseResponse_HeaderUnderEightBytes_ThrowsInvalidDataBeforeReadingAction()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            UdpTrackerScrapeCodec.ParseResponse(new byte[7], 123, new[] { Hash(1) }));

        Assert.Contains("Scrape response too short", ex.Message);
    }

    [Fact]
    public void ParseResponse_ErrorWithEmptyMessage_RetainsTrackerErrorPlaceholder()
    {
        // Action=3 with payload length 8 means there's no error text after the header.
        byte[] response = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(0), 3);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(4), 123);

        var ex = Assert.Throws<UdpTrackerException>(() =>
            UdpTrackerScrapeCodec.ParseResponse(response, 123, new[] { Hash(1) }));

        Assert.False(ex.IsTransient);
        Assert.Contains("(no error message)", ex.Message);
    }

    [Fact]
    public void ParseResponse_ErrorActionDoesNotCopyBuffer()
    {
        byte[] error = new byte[8 + 5];
        BinaryPrimitives.WriteInt32BigEndian(error.AsSpan(0), 3);
        BinaryPrimitives.WriteInt32BigEndian(error.AsSpan(4), 123);
        Encoding.ASCII.GetBytes("oops!").CopyTo(error.AsSpan(8));

        // The error path now consumes the buffer as ReadOnlySpan<byte>; passing a
        // sliced span exercises that overload directly without ToArray() copying.
        var ex = Assert.Throws<UdpTrackerException>(() =>
            UdpTrackerScrapeCodec.ParseResponse(error.AsSpan(), 123, new[] { Hash(1) }));

        Assert.Contains("oops!", ex.Message);
    }

    private static byte[] Hash(int seed)
    {
        return Enumerable.Range(0, InfoHash.V1Length).Select(i => (byte)(seed + i)).ToArray();
    }

    private static byte[] Response(int transactionId, int action, params (int Seeders, int Completed, int Leechers)[] rows)
    {
        byte[] response = new byte[8 + (rows.Length * 12)];
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(0), action);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(4), transactionId);
        for (int i = 0; i < rows.Length; i++)
        {
            int offset = 8 + (i * 12);
            BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(offset), rows[i].Seeders);
            BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(offset + 4), rows[i].Completed);
            BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(offset + 8), rows[i].Leechers);
        }

        return response;
    }
}
