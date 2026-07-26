namespace PeerSharp.Internals.Trackers;

internal enum TrackerEvent
{
    None = 0,
    Completed = 1,
    Started = 2,
    Stopped = 3,

    /// <summary>
    /// BEP 21: sent by a partial seed - a peer that is incomplete but has everything it intends to
    /// download. HTTP only; BEP 15 has no numeric event for it.
    /// </summary>
    Paused = 4
}
