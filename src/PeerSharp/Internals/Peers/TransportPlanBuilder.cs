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
/// <see cref="ConnectionSettings.PreferUtp"/> and targeted a 70% uTP share while the plan made both
/// unreachable for a peer this client had not met before.
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
        bool UtpAvailable,
        bool UtpHinted,
        Func<int> CurrentUtpRatioPercent);

    public static IReadOnlyList<TransportPreference> Build(in Inputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs.Settings);
        ArgumentNullException.ThrowIfNull(inputs.CurrentUtpRatioPercent);

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
            // A peer already known to speak uTP is dialled over it, whatever the current share.
            // The ratio governs how much this client is willing to guess, and there is nothing left
            // to guess about a peer that has answered a uTP dial or been flagged by PEX.
            bool utpFirst = inputs.UtpHinted;

            if (!utpFirst)
            {
                int target = Math.Clamp(settings.PreferUtpRatioPercent, 0, 100);
                // Only sample the live uTP ratio in the branch that needs it; the calculation
                // iterates connected peers, so callers shouldn't pay for it on plans that never
                // reach the ratio decision.
                utpFirst = inputs.CurrentUtpRatioPercent() < target;
            }

            if (utpFirst)
            {
                plan.Add(TransportPreference.Utp);
                if (tcpAllowed)
                {
                    plan.Add(TransportPreference.Tcp);
                }
            }
            else
            {
                if (tcpAllowed)
                {
                    plan.Add(TransportPreference.Tcp);
                }

                plan.Add(TransportPreference.Utp);
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
