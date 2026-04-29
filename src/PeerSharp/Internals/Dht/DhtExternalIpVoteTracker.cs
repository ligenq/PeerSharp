using System.Net;

namespace PeerSharp.Internals.Dht;

internal sealed class DhtExternalIpVoteTracker
{
    private readonly Lock _lock = new();
    private readonly int _requiredVotes;
    private IPAddress? _externalIp;
    private int _votes;

    public DhtExternalIpVoteTracker(IPAddress? externalIp = null, int initialVotes = 0, int requiredVotes = 3)
    {
        _externalIp = externalIp;
        _votes = initialVotes;
        _requiredVotes = requiredVotes;
    }

    public DhtExternalIpVoteResult ProcessReport(ReadOnlySpan<byte> ipBytes)
    {
        IPAddress? reportedIp = TryParseReport(ipBytes);
        if (reportedIp == null || !DhtSecurity.ShouldValidate(reportedIp))
        {
            return DhtExternalIpVoteResult.Ignored;
        }

        lock (_lock)
        {
            if (_externalIp == null)
            {
                _externalIp = reportedIp;
                _votes = 1;
                return DhtExternalIpVoteResult.FirstReport(reportedIp, _votes, _requiredVotes);
            }

            if (!_externalIp.Equals(reportedIp))
            {
                _externalIp = reportedIp;
                _votes = 1;
                return DhtExternalIpVoteResult.Changed(reportedIp, _votes, _requiredVotes);
            }

            if (_votes >= _requiredVotes)
            {
                return DhtExternalIpVoteResult.AlreadyConfirmed(reportedIp, _votes, _requiredVotes);
            }

            _votes++;
            return _votes >= _requiredVotes
                ? DhtExternalIpVoteResult.Confirmed(reportedIp, _votes, _requiredVotes)
                : DhtExternalIpVoteResult.Progress(reportedIp, _votes, _requiredVotes);
        }
    }

    private static IPAddress? TryParseReport(ReadOnlySpan<byte> ipBytes)
    {
        try
        {
            return ipBytes.Length is 4 or 16 ? new IPAddress(ipBytes) : null;
        }
        catch
        {
            return null;
        }
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
