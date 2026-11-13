using System.Net;

namespace PeerSharp.Internals.Dht;

internal sealed class DhtExternalIpVoteTracker
{
    /// <summary>Bounds the competing addresses tracked at once, so a flood of distinct lies costs nothing.</summary>
    private const int MaxCandidates = 8;

    private readonly Lock _lock = new();
    private readonly int _requiredVotes;
    private IPAddress? _confirmed;
    private readonly Dictionary<IPAddress, Candidate> _candidates = [];

    public DhtExternalIpVoteTracker(int requiredVotes = 3)
    {
        _requiredVotes = requiredVotes;
    }

    /// <summary>Creates a tracker that already trusts <paramref name="externalIp"/>.</summary>
    public static DhtExternalIpVoteTracker CreateConfirmed(IPAddress externalIp, int requiredVotes = 3)
    {
        ArgumentNullException.ThrowIfNull(externalIp);
        return new DhtExternalIpVoteTracker(requiredVotes) { _confirmed = externalIp };
    }

    /// <summary>
    /// The external address once enough independent sources agree on it, otherwise null.
    /// </summary>
    /// <remarks>
    /// Held back until the votes are in, deliberately. The first report is one stranger's opinion of
    /// where we are, and callers use this to derive values that decide which peers to keep - so a
    /// single spoofed report should not move them. For the same reason a confirmed address is kept
    /// until a challenger has gathered the same number of independent votes: one dissenting report
    /// does not unconfirm it.
    /// </remarks>
    public IPAddress? ConfirmedAddress
    {
        get
        {
            lock (_lock)
            {
                return _confirmed;
            }
        }
    }

    public DhtExternalIpVoteResult ProcessReport(ReadOnlySpan<byte> ipBytes)
    {
        return ProcessReport(TryParseReport(ipBytes));
    }

    public DhtExternalIpVoteResult ProcessReport(ReadOnlySpan<byte> ipBytes, IPAddress source)
    {
        return ProcessReport(TryParseReport(ipBytes), source);
    }

    public DhtExternalIpVoteResult ProcessReport(IPAddress? reportedIp)
        => ProcessReport(reportedIp, null);

    private DhtExternalIpVoteResult ProcessReport(IPAddress? reportedIp, IPAddress? source)
    {
        if (reportedIp == null || !DhtSecurity.ShouldValidate(reportedIp))
        {
            return DhtExternalIpVoteResult.Ignored;
        }

        lock (_lock)
        {
            if (reportedIp.Equals(_confirmed))
            {
                // Agreement with the confirmed address counts against every challenger, so replacing
                // it takes a sustained majority rather than a few reports spread over hours.
                DecayCandidates();
                return DhtExternalIpVoteResult.AlreadyConfirmed(reportedIp, _requiredVotes, _requiredVotes);
            }

            bool firstEver = _confirmed == null && _candidates.Count == 0;
            if (!_candidates.TryGetValue(reportedIp, out var candidate))
            {
                if (_candidates.Count >= MaxCandidates)
                {
                    _candidates.Remove(_candidates.MinBy(static c => c.Value.Votes).Key);
                }
                candidate = new Candidate();
                _candidates[reportedIp] = candidate;
            }

            if (source != null && !candidate.Sources.Add(source))
            {
                return DhtExternalIpVoteResult.Ignored;
            }

            candidate.Votes++;
            if (candidate.Votes >= _requiredVotes)
            {
                _confirmed = reportedIp;
                _candidates.Clear();
                return DhtExternalIpVoteResult.Confirmed(reportedIp, candidate.Votes, _requiredVotes);
            }

            if (candidate.Votes == 1)
            {
                return firstEver
                    ? DhtExternalIpVoteResult.FirstReport(reportedIp, candidate.Votes, _requiredVotes)
                    : DhtExternalIpVoteResult.Changed(reportedIp, candidate.Votes, _requiredVotes);
            }

            return DhtExternalIpVoteResult.Progress(reportedIp, candidate.Votes, _requiredVotes);
        }
    }

    private void DecayCandidates()
    {
        if (_candidates.Count == 0)
        {
            return;
        }

        foreach (var (address, candidate) in _candidates.ToList())
        {
            if (--candidate.Votes <= 0)
            {
                _candidates.Remove(address);
            }
        }
    }

    private static IPAddress? TryParseReport(ReadOnlySpan<byte> ipBytes)
    {
        try
        {
            // BEP 42 sends compact IP+port; the port is of no interest here.
            ipBytes = ipBytes.Length switch
            {
                4 or 16 => ipBytes,
                6 => ipBytes[..4],
                18 => ipBytes[..16],
                _ => [],
            };
            if (ipBytes.IsEmpty)
            {
                return null;
            }
            var address = new IPAddress(ipBytes);
            return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        }
        catch
        {
            return null;
        }
    }

    private sealed class Candidate
    {
        public int Votes { get; set; }
        public HashSet<IPAddress> Sources { get; } = [];
    }
}

internal enum DhtExternalIpVoteStatus
{
    Ignored,
    FirstReport,
    Changed,
    Progress,
    Confirmed,
    AlreadyConfirmed
}

internal readonly record struct DhtExternalIpVoteResult(
    DhtExternalIpVoteStatus Status,
    IPAddress? Address,
    int Votes,
    int RequiredVotes)
{
    public static DhtExternalIpVoteResult Ignored { get; } = new(DhtExternalIpVoteStatus.Ignored, null, 0, 0);

    public static DhtExternalIpVoteResult FirstReport(IPAddress address, int votes, int requiredVotes) =>
        new(DhtExternalIpVoteStatus.FirstReport, address, votes, requiredVotes);

    public static DhtExternalIpVoteResult Changed(IPAddress address, int votes, int requiredVotes) =>
        new(DhtExternalIpVoteStatus.Changed, address, votes, requiredVotes);

    public static DhtExternalIpVoteResult Progress(IPAddress address, int votes, int requiredVotes) =>
        new(DhtExternalIpVoteStatus.Progress, address, votes, requiredVotes);

    public static DhtExternalIpVoteResult Confirmed(IPAddress address, int votes, int requiredVotes) =>
        new(DhtExternalIpVoteStatus.Confirmed, address, votes, requiredVotes);

    public static DhtExternalIpVoteResult AlreadyConfirmed(IPAddress address, int votes, int requiredVotes) =>
        new(DhtExternalIpVoteStatus.AlreadyConfirmed, address, votes, requiredVotes);
}
