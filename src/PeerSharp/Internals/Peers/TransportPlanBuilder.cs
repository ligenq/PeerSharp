namespace PeerSharp.Internals.Peers;

internal enum TransportPreference
{
    Utp,
    Tcp
}

/// <summary>
/// Builds the ordered list of transports a connection attempt should try, from settings, peer
/// history and transient state. Pure given inputs; no side effects, no time provider, no logging.
/// </summary>
/// <remarks>
/// <para>
/// A peer nothing is known about is dialled over uTP first, which is what libtorrent does - its
/// torrent_peer starts with <c>supports_utp</c> set and the comment "assume peers support utp".
/// The reason is that LEDBAT is the whole point of uTP: it yields to the other traffic on the
/// user's uplink, and that benefit only exists if the bulk transfer actually runs over it. A client
/// that reaches most peers over TCP delivers the throughput without the courtesy.
/// </para>
/// <para>
/// This used to put TCP first for anything not already flagged by PEX, which meant uTP was reached
/// only when TCP failed - and TCP does not fail for a reachable peer. The settings said
/// <see cref="ConnectionSettings.PreferUtp"/> while the plan made uTP unreachable for a peer this
/// client had not met before.
/// </para>
/// <para>
/// It then targeted a share of connections instead, which libtorrent has no notion of. Its rule is
/// only about the peer in hand - use uTP if this peer is believed to support it, TCP otherwise -
/// and a quota can only overrule that, sending a peer known to speak uTP over TCP because of when
/// it happened to be dialled. Everything a share was meant to protect against is already answered
/// closer to the evidence: per-peer demotion when a peer will not take uTP, and
/// <see cref="ConnectionSettings.UtpColdStartFailureLimit"/> when the path carries no UDP at all.
/// </para>
/// <para>
/// What makes leading with uTP cheap here is the fallback: the plan is tried inside one dial with
/// the timeout budget split between its entries, so a blocked UDP path costs one capped attempt
/// rather than a lost peer. libtorrent has to spend a whole extra connection to fall back.
/// </para>
/// </remarks>
internal static class TransportPlanBuilder
{
    public readonly record struct Inputs(
        ConnectionSettings Settings,
        bool ForceUtp,
        bool UtpAvailable);

    public static IReadOnlyList<TransportPreference> Build(in Inputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs.Settings);

        var settings = inputs.Settings;

        bool utpAllowed = settings.EnableUtpOut && inputs.UtpAvailable;
        bool tcpAllowed = settings.EnableTcpOut;

        if (inputs.ForceUtp)
        {
            return utpAllowed
                ? new[] { TransportPreference.Utp }
                : [];
        }

        if (!utpAllowed && !tcpAllowed)
        {
            return [];
        }

        bool utpPreferred = settings.PreferUtp && utpAllowed;

        var plan = new List<TransportPreference>(2);

        if (utpPreferred)
        {
            plan.Add(TransportPreference.Utp);
            if (tcpAllowed)
            {
                plan.Add(TransportPreference.Tcp);
            }

            return plan;
        }

        if (tcpAllowed)
        {
            plan.Add(TransportPreference.Tcp);
        }

        if (utpAllowed)
        {
            plan.Add(TransportPreference.Utp);
        }

        return plan;
    }
}
