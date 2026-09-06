namespace PeerSharp.Internals.Peers;

/// <summary>
/// Shares one connection attempt's timeout budget across the transports it may try.
///
/// <para>
/// A dial to a peer has a single budget covering every transport in its plan. If the first attempt is
/// not time-boxed it can spend the lot, and the fallback is left with the configured minimum - one
/// second by default, which is not long enough to reach most peers over the open internet. The fallback
/// then fails for a reason that has nothing to do with the peer, and the transport it was meant to
/// rescue never gets a fair try.
/// </para>
///
/// <para>
/// Only the uTP branch used to be boxed, so a Utp-&gt;Tcp plan split ten seconds sensibly into three and
/// seven while the reverse gave TCP all ten and uTP the floor. Measured on a live swarm, 168 connections
/// took the second path and 98% of them timed out - about half of every failed connection attempt in
/// the run.
/// </para>
/// </summary>
internal static class ConnectionBudgetCalculator
{
    /// <summary>
    /// How long the current attempt may take. Attempts with another transport behind them are capped so
    /// that something is left for it; the final attempt may use whatever remains.
    /// </summary>
    public static int ForAttempt(int remainingMs, bool hasFallback, int fallbackCapMs)
    {
        return hasFallback ? Math.Min(remainingMs, fallbackCapMs) : remainingMs;
    }

    /// <summary>
    /// What is left after an attempt, never below the floor so a fallback always gets some chance.
    /// </summary>
    public static int Remaining(int remainingMs, int usedMs, int minimumMs)
    {
        return Math.Max(minimumMs, remainingMs - usedMs);
    }

    /// <summary>
    /// The cap for a uTP attempt, which depends on whether this peer is known to speak uTP or is
    /// only assumed to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every peer is assumed to support uTP until something says otherwise, and on a public swarm
    /// that assumption is wrong about three times in four. The full budget is the right one for a
    /// peer that answers; charging it to every peer that does not is what makes leading with uTP
    /// expensive, because the fallback cannot start until it has been spent.
    /// </para>
    /// <para>
    /// The speculative cap can only shorten the attempt, never lengthen it: a value above the proven
    /// budget would otherwise give a peer nothing is known about more time than one that has already
    /// answered.
    /// </para>
    /// </remarks>
    public static int UtpCap(bool utpProven, int provenCapMs, int speculativeCapMs)
    {
        return utpProven ? provenCapMs : Math.Min(provenCapMs, speculativeCapMs);
    }

    /// <summary>
    /// The cap for a non-final attempt, honouring the configured floor.
    /// </summary>
    public static int FallbackCap(int totalBudgetMs, int configuredCapMs, int minimumMs)
    {
        int cap = Math.Min(totalBudgetMs, configuredCapMs);
        return cap < minimumMs ? minimumMs : cap;
    }
}
