namespace PeerSharp.Core;

/// <summary>
/// Download strategy for piece selection.
/// </summary>
public enum DownloadStrategy
{
    /// <summary>
    /// Default strategy - prioritizes rarest pieces for swarm health.
    /// </summary>
    RarestFirst,

    /// <summary>
    /// Downloads pieces in sequential order from the beginning.
    /// </summary>
    Sequential,

    /// <summary>
    /// Dynamic prioritization based on playhead position for video streaming.
    /// </summary>
    Streaming
}

