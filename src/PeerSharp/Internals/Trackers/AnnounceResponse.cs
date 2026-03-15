using System.Net;

namespace PeerSharp.Internals.Trackers;

internal class AnnounceResponse
{
    public uint Interval { get; set; } = 5 * 60;
    public uint? MinInterval { get; set; }
    public uint LeechCount { get; set; } = 0;
    public List<IPEndPoint> Peers { get; set; } = new();
    public uint SeedCount { get; set; } = 0;
}
