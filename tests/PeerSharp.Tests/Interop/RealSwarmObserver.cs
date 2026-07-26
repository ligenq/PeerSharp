using System.Net;
using System.Text;

namespace PeerSharp.Tests.Interop;

/// <summary>
/// Samples a live torrent's peer list and accumulates what actually happened with each remote client
/// implementation.
///
/// <para>
/// The question this exists to answer is not "does the protocol encode correctly" - the loopback and
/// unit tests cover that - but "do real libtorrent, qBittorrent and Transmission peers treat us as a
/// legitimate peer". Those failures are invisible locally: a peer that quietly never unchokes us, or
/// drops the connection a second after the handshake, produces a client that appears to work and
/// downloads at a fraction of the speed it should.
/// </para>
///
/// <para>
/// Everything here is measured through the public <see cref="IPeers"/> surface, so what it reports is
/// what a consumer of the library could see for themselves.
/// </para>
/// </summary>
internal sealed class RealSwarmObserver
{
    private readonly Dictionary<IPEndPoint, PeerObservation> _peers = [];

    public int Samples { get; private set; }

    public int PeakConcurrentPeers { get; private set; }

    public IReadOnlyDictionary<IPEndPoint, PeerObservation> Peers => _peers;

    /// <summary>
    /// Folds one snapshot of the peer list into the running totals.
    /// </summary>
    public void Sample(IReadOnlyList<PeerInfo> connected)
    {
        Samples++;
        PeakConcurrentPeers = Math.Max(PeakConcurrentPeers, connected.Count);

        foreach (var peer in connected)
        {
            if (!_peers.TryGetValue(peer.EndPoint, out var observation))
            {
                observation = new PeerObservation(peer.EndPoint, peer.ClientName);
                _peers[peer.EndPoint] = observation;
            }

            observation.Update(peer, Samples);
        }
    }

    /// <summary>
    /// Groups observations by remote client implementation. This is the breakdown that matters: a
    /// figure averaged across the whole swarm hides the case where one implementation works perfectly
    /// and another never unchokes us at all, which is exactly the shape an interop bug takes.
    /// </summary>
    public IReadOnlyList<ClientSummary> SummariseByClient()
    {
        return
        [
            .. _peers.Values
                .GroupBy(static p => NormaliseClient(p.ClientName))
                .Select(static group => new ClientSummary(
                    Client: group.Key,
                    PeersMet: group.Count(),
                    UnchokedUs: group.Count(static p => p.EverUnchokedUs),
                    WeWereInterested: group.Count(static p => p.WeWereInterested),
                    SentUsData: group.Count(static p => p.BytesDownloaded > 0),
                    RequestedFromUs: group.Count(static p => p.EverInterestedInUs),
                    WeSentThemData: group.Count(static p => p.BytesUploaded > 0),
                    BytesDownloaded: group.Sum(static p => p.BytesDownloaded),
                    BytesUploaded: group.Sum(static p => p.BytesUploaded),
                    SingleSampleConnections: group.Count(static p => p.SampleCount == 1),
                    Utp: group.Count(static p => p.UsedUtp),
                    Encrypted: group.Count(static p => p.WasEncrypted)))
                .OrderByDescending(static s => s.PeersMet)
                .ThenBy(static s => s.Client, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    /// Collapses version suffixes so "libtorrent 2.0.9" and "libtorrent 1.2.19" aggregate together.
    /// The implementation is what determines how we are treated; the point release rarely is.
    /// </summary>
    private static string NormaliseClient(string clientName)
    {
        if (string.IsNullOrWhiteSpace(clientName))
        {
            return "Unknown";
        }

        int space = clientName.IndexOf(' ', StringComparison.Ordinal);
        return space > 0 ? clientName[..space] : clientName;
    }

    public string BuildReport(string title)
    {
        var report = new StringBuilder();
        report.AppendLine();
        report.AppendLine($"=== {title} ===");
        report.AppendLine($"samples taken          : {Samples}");
        report.AppendLine($"distinct peers met     : {_peers.Count}");
        report.AppendLine($"peak concurrent peers  : {PeakConcurrentPeers}");

        var summaries = SummariseByClient();
        if (summaries.Count == 0)
        {
            report.AppendLine();
            report.AppendLine("No peers were seen at all. Check connectivity before reading anything else here.");
            return report.ToString();
        }

        long totalDown = summaries.Sum(static s => s.BytesDownloaded);
        long totalUp = summaries.Sum(static s => s.BytesUploaded);
        report.AppendLine($"bytes downloaded       : {totalDown:N0}");
        report.AppendLine($"bytes uploaded         : {totalUp:N0}");
        report.AppendLine();

        report.AppendLine("per remote client implementation:");
        report.AppendLine();
        report.AppendLine($"  {"client",-18} {"met",5} {"unchoked us",13} {"sent data",11} {"wanted ours",12} {"we served",10}");
        report.AppendLine($"  {new string('-', 18)} {new string('-', 5)} {new string('-', 13)} {new string('-', 11)} {new string('-', 12)} {new string('-', 10)}");

        foreach (var s in summaries)
        {
            report.AppendLine(
                $"  {s.Client,-18} {s.PeersMet,5} {Ratio(s.UnchokedUs, s.PeersMet),13} {Ratio(s.SentUsData, s.PeersMet),11} " +
                $"{Ratio(s.RequestedFromUs, s.PeersMet),12} {Ratio(s.WeSentThemData, s.PeersMet),10}");
        }

        report.AppendLine();
        report.AppendLine("transport and short-lived connections:");
        foreach (var s in summaries)
        {
            report.AppendLine(
                $"  {s.Client,-18} uTP {s.Utp,3}/{s.PeersMet,-3}  encrypted {s.Encrypted,3}/{s.PeersMet,-3}  " +
                $"seen once {s.SingleSampleConnections,3}  down {s.BytesDownloaded,14:N0}  up {s.BytesUploaded,14:N0}");
        }

        return report.ToString();
    }

    private static string Ratio(int part, int total)
    {
        return total == 0 ? "-" : $"{part}/{total} ({100.0 * part / total:F0}%)";
    }

    /// <summary>
    /// What one remote peer did over the life of the run.
    /// </summary>
    internal sealed class PeerObservation(IPEndPoint endPoint, string clientName)
    {
        public IPEndPoint EndPoint { get; } = endPoint;

        public string ClientName { get; } = clientName;

        /// <summary>
        /// Whether the peer ever stopped choking us. The single most useful interop signal: a peer
        /// that connects, stays, and never unchokes is either rate limiting us or does not consider us
        /// a peer worth serving.
        /// </summary>
        public bool EverUnchokedUs { get; private set; }

        /// <summary>Whether we ever wanted anything from it, without which choke state means nothing.</summary>
        public bool WeWereInterested { get; private set; }

        public bool EverInterestedInUs { get; private set; }

        /// <summary>Per-peer byte counters are cumulative, so the largest value seen is the total.</summary>
        public long BytesDownloaded { get; private set; }

        public long BytesUploaded { get; private set; }

        public bool UsedUtp { get; private set; }

        public bool WasEncrypted { get; private set; }

        /// <summary>
        /// How many samples this peer appeared in. One means the connection did not outlive a single
        /// sampling interval - a coarse proxy for being dropped straight after the handshake.
        /// </summary>
        public int SampleCount { get; private set; }

        public void Update(PeerInfo info, int sampleIndex)
        {
            SampleCount++;
            EverUnchokedUs |= !info.PeerChoking;
            WeWereInterested |= info.AmInterested;
            EverInterestedInUs |= info.PeerInterested;
            UsedUtp |= info.IsUtp;
            WasEncrypted |= info.IsEncrypted;
            BytesDownloaded = Math.Max(BytesDownloaded, info.Downloaded);
            BytesUploaded = Math.Max(BytesUploaded, info.Uploaded);
        }
    }

    /// <param name="Client">Remote client implementation, with version stripped.</param>
    /// <param name="PeersMet">Distinct peers of this implementation seen.</param>
    /// <param name="UnchokedUs">How many of them ever unchoked us.</param>
    /// <param name="WeWereInterested">How many we ever wanted data from.</param>
    /// <param name="SentUsData">How many actually sent bytes.</param>
    /// <param name="RequestedFromUs">How many became interested in what we hold.</param>
    /// <param name="WeSentThemData">How many we actually served bytes to.</param>
    /// <param name="SingleSampleConnections">How many lasted less than one sampling interval.</param>
    internal readonly record struct ClientSummary(
        string Client,
        int PeersMet,
        int UnchokedUs,
        int WeWereInterested,
        int SentUsData,
        int RequestedFromUs,
        int WeSentThemData,
        long BytesDownloaded,
        long BytesUploaded,
        int SingleSampleConnections,
        int Utp,
        int Encrypted);
}
