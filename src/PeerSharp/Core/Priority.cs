namespace PeerSharp.Core;

/// <summary>
/// Specifies the download priority for a file within a torrent.
/// </summary>
public enum Priority
{
    /// <summary>File will not be downloaded.</summary>
    DoNotDownload = 0,

    /// <summary>Low priority - downloaded after normal and high priority files.</summary>
    Low = 1,

    /// <summary>Normal priority - default download order.</summary>
    Normal = 2,

    /// <summary>High priority - downloaded before normal and low priority files.</summary>
    High = 3
}

