using BenchmarkDotNet.Attributes;
using PeerSharp.Internals.Network;
using System.Net;
using System.Text;

namespace PeerSharp.Benchmarks;

/// <summary>
/// Loading a published blocklist, through to the first lookup that sorts and merges it. An
/// application loads one at startup, before restored torrents may accept peers, so its cost is
/// startup time: Peerfluence measured a 250k-range list at about 120 ms, the largest single cost of
/// its launch with real data.
/// </summary>
[MemoryDiagnoser]
public class IpBlocklistLoadBenchmarks
{
    private const int Ranges = 250_000;
    private byte[] _p2pList = null!;
    private IPAddress _probe = null!;

    /// <summary>The P2P format published lists use: <c>description:start-end</c>, one per line.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var text = new StringBuilder();
        for (int i = 0; i < Ranges; i++)
        {
            long start = 16777216L + ((long)i * 64);
            text.Append("Example Network Operator ").Append(i).Append(':')
                .Append(ToAddress(start)).Append('-').Append(ToAddress(start + 31)).Append('\n');
        }

        _p2pList = Encoding.UTF8.GetBytes(text.ToString());
        _probe = ToAddress(16777216L + ((long)(Ranges / 2) * 64) + 5);
    }

    private static IPAddress ToAddress(long value) => new(new[]
    {
        (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
    });

    [Benchmark(Description = "Load 250k P2P ranges and look one up")]
    public bool LoadP2PList()
    {
        var blocklist = new IpBlocklist();
        using var stream = new MemoryStream(_p2pList, writable: false);
        blocklist.LoadFromStream(stream);
        return blocklist.IsBlocked(_probe);
    }
}
