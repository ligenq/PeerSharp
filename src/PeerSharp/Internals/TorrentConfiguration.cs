using PeerSharp.Internals.Bandwidth;

namespace PeerSharp.Internals;

/// <summary>
/// Manages configuration and limits for a specific torrent.
/// </summary>
internal sealed class TorrentConfiguration
{
    private readonly IBandwidthManager _bandwidth;
    private readonly ITorrent _torrent; // Back-reference needed for BandwidthManager calls

    private int _downloadLimitBytesPerSecond;
    private int _diskReadLimitBytesPerSecond;
    private int _diskWriteLimitBytesPerSecond;
    private int _uploadLimitBytesPerSecond;

    public TorrentConfiguration(ITorrent torrent, IBandwidthManager bandwidth)
    {
        _torrent = torrent;
        _bandwidth = bandwidth;
    }

    public int DownloadLimitBytesPerSecond
    {
        get => _downloadLimitBytesPerSecond;
        set
        {
            _downloadLimitBytesPerSecond = Math.Max(0, value);
            _bandwidth.SetTorrentLimits(_torrent, _downloadLimitBytesPerSecond, _uploadLimitBytesPerSecond);
        }
    }

    public int DiskReadLimitBytesPerSecond
    {
        get => _diskReadLimitBytesPerSecond;
        set
        {
            _diskReadLimitBytesPerSecond = Math.Max(0, value);
            _bandwidth.SetTorrentDiskLimits(_torrent, _diskReadLimitBytesPerSecond, _diskWriteLimitBytesPerSecond);
        }
    }

    public int DiskWriteLimitBytesPerSecond
    {
        get => _diskWriteLimitBytesPerSecond;
        set
        {
            _diskWriteLimitBytesPerSecond = Math.Max(0, value);
            _bandwidth.SetTorrentDiskLimits(_torrent, _diskReadLimitBytesPerSecond, _diskWriteLimitBytesPerSecond);
        }
    }

    // Streaming
    public DownloadStrategy DownloadStrategy { get; set; } = DownloadStrategy.RarestFirst;

    public bool QueueAutoStart { get; set; } = true;
    public int QueuePriority { get; set; }
    public float? RatioLimit { get; set; }
    public TimeSpan? SeedTimeLimit { get; set; }

    public int UploadLimitBytesPerSecond
    {
        get => _uploadLimitBytesPerSecond;
        set
        {
            _uploadLimitBytesPerSecond = Math.Max(0, value);
            _bandwidth.SetTorrentLimits(_torrent, _downloadLimitBytesPerSecond, _uploadLimitBytesPerSecond);
        }
    }
}
