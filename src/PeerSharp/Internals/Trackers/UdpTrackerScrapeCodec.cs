using System.Buffers.Binary;

namespace PeerSharp.Internals.Trackers;

internal static class UdpTrackerScrapeCodec
{
    public const int MaxHashesPerRequest = 74;

    public static byte[] BuildRequest(long connectionId, int transactionId, IList<byte[]> infoHashes)
    {
        ArgumentNullException.ThrowIfNull(infoHashes);

        int hashCount = Math.Min(infoHashes.Count, MaxHashesPerRequest);
        byte[] request = new byte[16 + (hashCount * InfoHash.V1Length)];
        Span<byte> span = request;

        BinaryPrimitives.WriteInt64BigEndian(span, connectionId);
        BinaryPrimitives.WriteInt32BigEndian(span[8..], 2);
        BinaryPrimitives.WriteInt32BigEndian(span[12..], transactionId);

        for (int i = 0; i < hashCount; i++)
        {
            if (infoHashes[i].Length != InfoHash.V1Length)
            {
                throw new ArgumentException("UDP scrape info hashes must be 20 bytes.", nameof(infoHashes));
            }

            infoHashes[i].CopyTo(span.Slice(16 + (i * InfoHash.V1Length), InfoHash.V1Length));
        }

        return request;
    }

    public static MultiScrapeResponse ParseResponse(ReadOnlySpan<byte> buffer, int expectedTransactionId, IList<byte[]> infoHashes)
    {
        ArgumentNullException.ThrowIfNull(infoHashes);

        int hashCount = Math.Min(infoHashes.Count, MaxHashesPerRequest);
        int minSize = 8 + (hashCount * 12);

        if (buffer.Length < 8)
        {
            throw new InvalidDataException($"Scrape response too short: {buffer.Length} bytes (expected at least 8)");
        }

        int action = BinaryPrimitives.ReadInt32BigEndian(buffer);
        int transactionId = BinaryPrimitives.ReadInt32BigEndian(buffer[4..]);

        if (transactionId != expectedTransactionId)
        {
            throw new UdpTrackerException($"Transaction ID mismatch: expected {expectedTransactionId}, got {transactionId}", isTransient: true);
        }

        if (action == 3)
        {
            throw new UdpTrackerException($"Tracker returned error on scrape: {UdpTracker.ParseTrackerErrorMessage(buffer)}", isTransient: false);
        }

        if (action != 2)
        {
            throw new InvalidDataException($"Invalid scrape action: expected 2, got {action}");
        }

        if (buffer.Length < minSize)
        {
            throw new InvalidDataException($"Scrape response too short: {buffer.Length} bytes (expected at least {minSize})");
        }

        var response = new MultiScrapeResponse();
        int responseCount = (buffer.Length - 8) / 12;

        for (int i = 0; i < Math.Min(hashCount, responseCount); i++)
        {
            int offset = 8 + (i * 12);
            response.Results[Convert.ToHexString(infoHashes[i])] = new ScrapeResponse
            {
                SeedCount = (uint)BinaryPrimitives.ReadInt32BigEndian(buffer[offset..]),
                Downloaded = (uint)BinaryPrimitives.ReadInt32BigEndian(buffer[(offset + 4)..]),
                LeechCount = (uint)BinaryPrimitives.ReadInt32BigEndian(buffer[(offset + 8)..])
            };
        }

        return response;
    }
}
