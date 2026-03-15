using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PeerSharp.Internals.Dht;

/// <summary>
/// BEP 33: Bloom filter for DHT scrape functionality.
/// 256 bytes (2048 bits) with two hash functions per entry.
/// </summary>
internal class DhtBloomFilter
{
    public const int FilterSizeBits = FilterSizeBytes * 8;

    // BEP 33: Bloom filter size is 256 bytes (2048 bits)
    public const int FilterSizeBytes = 256;

    // 2048 bits

    private readonly byte[] _filter;

    public DhtBloomFilter()
    {
        _filter = new byte[FilterSizeBytes];
    }

    public DhtBloomFilter(byte[] data)
    {
        if (data.Length != FilterSizeBytes)
        {
            throw new ArgumentException($"Bloom filter must be {FilterSizeBytes} bytes", nameof(data));
        }
        _filter = (byte[])data.Clone();
    }

    /// <summary>
    /// Add an IP address to the bloom filter.
    /// BEP 33: Hash the IP with SHA-1 and use two hash functions.
    /// </summary>
    public void Add(System.Net.IPAddress ip)
    {
        var ipBytes = ip.GetAddressBytes();
        AddBytes(ipBytes);
    }

    /// <summary>
    /// Add raw bytes to the bloom filter using BEP 33 hash functions.
    /// </summary>
    public void AddBytes(byte[] data)
    {
        var hash = SHA1.HashData(data);

        // BEP 33: Two hash functions
        // index1 = first 2 bytes interpreted as big-endian, mod 2048
        // index2 = bytes 2-4 interpreted as big-endian, mod 2048
        int index1 = BinaryPrimitives.ReadUInt16BigEndian(hash.AsSpan(0)) % FilterSizeBits;
        int index2 = BinaryPrimitives.ReadUInt16BigEndian(hash.AsSpan(2)) % FilterSizeBits;

        SetBit(index1);
        SetBit(index2);
    }

    /// <summary>
    /// Clear all bits in the filter.
    /// </summary>
    public void Clear()
    {
        Array.Clear(_filter, 0, _filter.Length);
    }

    /// <summary>
    /// Estimate the number of items in the filter.
    /// BEP 33: count = log(1 - bits_set / 2048) / (2 * log(1 - 1/2048))
    /// </summary>
    public int EstimateCount()
    {
        int bitsSet = CountBitsSet();
        if (bitsSet == 0)
        {
            return 0;
        }
        if (bitsSet >= FilterSizeBits)
        {
            return int.MaxValue; // Saturated
        }

        // BEP 33 formula
        double m = FilterSizeBits;
        double k = 2; // Number of hash functions
        double x = bitsSet;

        // n ≈ -m/k * ln(1 - x/m)
        double estimate = -(m / k) * Math.Log(1.0 - (x / m));
        return (int)Math.Round(estimate);
    }

    /// <summary>
    /// Get the raw filter bytes for transmission.
    /// </summary>
    public byte[] GetBytes()
    {
        return (byte[])_filter.Clone();
    }

    /// <summary>
    /// Check if an IP address might be in the filter (may have false positives).
    /// </summary>
    public bool MightContain(System.Net.IPAddress ip)
    {
        var ipBytes = ip.GetAddressBytes();
        return MightContainBytes(ipBytes);
    }

    /// <summary>
    /// Check if bytes might be in the filter.
    /// </summary>
    public bool MightContainBytes(byte[] data)
    {
        var hash = SHA1.HashData(data);

        int index1 = BinaryPrimitives.ReadUInt16BigEndian(hash.AsSpan(0)) % FilterSizeBits;
        int index2 = BinaryPrimitives.ReadUInt16BigEndian(hash.AsSpan(2)) % FilterSizeBits;

        return GetBit(index1) && GetBit(index2);
    }

    private static int BitCount(byte b)
    {
        // Brian Kernighan's algorithm
        int count = 0;
        while (b != 0)
        {
            b &= (byte)(b - 1);
            count++;
        }
        return count;
    }

    private int CountBitsSet()
    {
        int count = 0;
        foreach (byte b in _filter)
        {
            count += BitCount(b);
        }
        return count;
    }

    private bool GetBit(int index)
    {
        int byteIndex = index / 8;
        int bitIndex = index % 8;
        return (_filter[byteIndex] & (1 << bitIndex)) != 0;
    }

    private void SetBit(int index)
    {
        int byteIndex = index / 8;
        int bitIndex = index % 8;
        _filter[byteIndex] |= (byte)(1 << bitIndex);
    }
}
