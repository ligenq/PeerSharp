namespace PeerSharp.Core;

/// <summary>
/// Represents the selection state and priority of a file within a torrent.
/// </summary>
/// <param name="Selected">Whether the file is selected for download.</param>
/// <param name="Priority">The download priority for this file.</param>
public sealed record FileSelection(
    bool Selected = true,
    Priority Priority = Priority.Normal);

