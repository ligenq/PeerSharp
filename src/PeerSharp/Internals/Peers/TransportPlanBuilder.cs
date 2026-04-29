namespace PeerSharp.Internals.Peers;

internal enum TransportPreference
{
    Utp,
    Tcp
}

/// <summary>
/// Builds the ordered list of transports a connection attempt should try
/// based on settings, peer history, and transient state. Pure given inputs;
/// no side effects, no time provider, no logging.
/// </summary>
internal static class TransportPlanBuilder
{
    public readonly record struct Inputs(
        ConnectionSettings Settings,
        bool ForceUtp,
        bool UtpAvailable,
        bool UtpHinted,
        bool InWarmupPeriod,
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
                : Array.Empty<TransportPreference>();
        }

        if (!utpAllowed && !tcpAllowed)
        {
            return Array.Empty<TransportPreference>();
        }

        bool utpPreferred = settings.PreferUtp && utpAllowed;

        if (inputs.InWarmupPeriod && !inputs.UtpHinted)
        {
            utpPreferred = false;
            utpAllowed = false;
        }

        var plan = new List<TransportPreference>(2);

        if (utpPreferred && !inputs.UtpHinted && tcpAllowed)
        {
            plan.Add(TransportPreference.Tcp);
            plan.Add(TransportPreference.Utp);
            return plan;
        }

        if (utpPreferred)
        {
            int target = Math.Clamp(settings.PreferUtpRatioPercent, 0, 100);
            // Only sample the live uTP ratio in the branch that needs it; the
            // calculation iterates connected peers, so callers shouldn't pay
            // for it on plans that never reach the ratio decision.
            if (inputs.CurrentUtpRatioPercent() < target)
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
                if (utpAllowed)
                {
                    plan.Add(TransportPreference.Utp);
                }
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
