using BenchmarkDotNet.Attributes;
using PeerSharp.Internals.Network;
using System.Net;

namespace PeerSharp.Benchmarks;

/// <summary>
/// Blocklist lookup. Called once per inbound connection and once per peer returned by a tracker
/// or DHT lookup, so a connect storm issues thousands of these in a burst. Published lists
/// routinely carry 200k+ ranges.
///
/// The lookup itself is a binary search over sorted ranges, so the single-threaded cost should
/// barely move between 1k and 200k ranges. The interesting number is
/// <see cref="ContendedLookup"/>: every call takes a process-wide lock, and peer connections
/// arrive in parallel, so contention rather than search depth is the plausible bottleneck.
/// </summary>
[MemoryDiagnoser]
public class IpBlocklistBenchmarks
{
    private const int ContendedThreads = 8;
    private const int OpsPerThread = 512;

    private IpBlocklist _blocklist = null!;
    private IPAddress _blockedAddress = null!;
    private IPAddress _allowedAddress = null!;
    private IPAddress[] _mixedAddresses = null!;

    /// <summary>Number of ranges loaded. 200k is the size of a typical published list.</summary>
    [Params(1_000, 200_000)]
    public int RangeCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _blocklist = new IpBlocklist { Enabled = true };

        // Spread ranges across 10.x with a gap between each, so lookups land both inside and
        // outside blocked space rather than always hitting the same bucket.
        for (int i = 0; i < RangeCount; i++)
        {
            long start = 10L * 16777216 + ((long)i * 16);
            _blocklist.AddRange(ToAddress(start), ToAddress(start + 7));
        }

        _blockedAddress = ToAddress(10L * 16777216 + ((long)(RangeCount / 2) * 16) + 3);
        _allowedAddress = ToAddress(10L * 16777216 + ((long)(RangeCount / 2) * 16) + 12);

        _mixedAddresses = new IPAddress[64];
        for (int i = 0; i < _mixedAddresses.Length; i++)
        {
            long baseAddr = 10L * 16777216 + ((long)(i * (RangeCount / 64)) * 16);
            _mixedAddresses[i] = ToAddress(baseAddr + (i % 2 == 0 ? 3 : 12));
        }
    }

    private static IPAddress ToAddress(long value)
    {
        return new IPAddress(new byte[]
        {
            (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
        });
    }

    [Benchmark(Baseline = true, Description = "IsBlocked, hit")]
    public bool LookupBlocked() => _blocklist.IsBlocked(_blockedAddress);

    [Benchmark(Description = "IsBlocked, miss")]
    public bool LookupAllowed() => _blocklist.IsBlocked(_allowedAddress);

    /// <summary>
    /// Aggregate cost of <see cref="ContendedThreads"/> x <see cref="OpsPerThread"/> lookups
    /// issued concurrently - the shape a connect storm actually produces. This is throughput
    /// under contention, not single-lookup latency.
    /// </summary>
    [Benchmark(Description = "IsBlocked, 8 threads contending")]
    public void ContendedLookup()
    {
        var threads = new Thread[ContendedThreads];
        for (int i = 0; i < ContendedThreads; i++)
        {
            int index = i;
            threads[i] = new Thread(() =>
            {
                for (int op = 0; op < OpsPerThread; op++)
                {
                    _blocklist.IsBlocked(_mixedAddresses[(index + op) % _mixedAddresses.Length]);
                }
            });
            threads[i].Start();
        }

        for (int i = 0; i < ContendedThreads; i++)
        {
            threads[i].Join();
        }
    }
}
