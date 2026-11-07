namespace PeerSharp.Internals;

/// <summary>
/// The session pause as a save needs to see it.
/// </summary>
internal interface ISessionPauseState
{
    /// <summary>Whether the pause stopped this torrent, so that resuming will start it again.</summary>
    bool IsHolding(Torrent torrent);
}
