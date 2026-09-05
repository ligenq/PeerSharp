namespace PeerSharp.Internals.Framework;

/// <summary>
/// Receives notice that the concurrency limits on <see cref="Config.TransferSettings"/> have changed,
/// so a component holding live limiters can re-read them.
/// </summary>
/// <remarks>
/// A callback interface rather than an event: the registration is explicit, so the lifetime of the
/// listener and the obligation to deregister it are both visible at the call site.
/// </remarks>
internal interface IConcurrencyLimitListener
{
    /// <summary>Called after a concurrency limit was written. Implementations re-read what they need.</summary>
    void OnConcurrencyLimitsChanged();
}
