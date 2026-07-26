using System.Net;

namespace PeerSharp.Internals.Trackers;

internal class AnnounceResponse
{
    public uint Interval { get; set; } = 5 * 60;
    public uint? MinInterval { get; set; }
    public uint LeechCount { get; set; } = 0;
    public List<IPEndPoint> Peers { get; set; } = [];
    public uint SeedCount { get; set; } = 0;

    /// <summary>
    /// BEP 24: the address the tracker saw the announce arrive from, when it reports one.
    /// </summary>
    public IPAddress? ExternalIp { get; set; }

    /// <summary>
    /// BEP 31: the tracker's retry instruction, only ever set on a failed announce.
    /// </summary>
    public TrackerRetryHint? RetryHint { get; set; }
}
