using PeerSharp.Internals.Bandwidth;

namespace PeerSharp.Internals;

/// <summary>
/// Manages configuration and limits for a specific torrent.
/// </summary>
internal sealed class TorrentConfiguration
{
    private readonly IBandwidthManager _bandwidth;
    private readonly ITorrent _torrent; // Back-reference needed for BandwidthManager calls

    private long _downloadLimitBytesPerSecond;
    private long _diskReadLimitBytesPerSecond;
    private long _diskWriteLimitBytesPerSecond;
    private long _uploadLimitBytesPerSecond;

    public TorrentConfiguration(ITorrent torrent, IBandwidthManager bandwidth)
    {
        _torrent = torrent;
        _bandwidth = bandwidth;
    }

    public long DownloadLimitBytesPerSecond
    {
        get => _downloadLimitBytesPerSecond;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _downloadLimitBytesPerSecond = value;
            _bandwidth.SetTorrentLimits(_torrent, _downloadLimitBytesPerSecond, _uploadLimitBytesPerSecond);
        }
    }

    public long DiskReadLimitBytesPerSecond
    {
        get => _diskReadLimitBytesPerSecond;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _diskReadLimitBytesPerSecond = value;
            _bandwidth.SetTorrentDiskLimits(_torrent, _diskReadLimitBytesPerSecond, _diskWriteLimitBytesPerSecond);
        }
    }

    public long DiskWriteLimitBytesPerSecond
    {
        get => _diskWriteLimitBytesPerSecond;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _diskWriteLimitBytesPerSecond = value;
            _bandwidth.SetTorrentDiskLimits(_torrent, _diskReadLimitBytesPerSecond, _diskWriteLimitBytesPerSecond);
        }
    }

    // Streaming
    public DownloadStrategy DownloadStrategy { get; set; } = DownloadStrategy.RarestFirst;

    public bool QueueAutoStart { get; set; } = true;
    public int QueuePriority { get; set; }
    public float? RatioLimit { get; set; }
    public TimeSpan? SeedTimeLimit { get; set; }

    public long UploadLimitBytesPerSecond
    {
        get => _uploadLimitBytesPerSecond;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _uploadLimitBytesPerSecond = value;
            _bandwidth.SetTorrentLimits(_torrent, _downloadLimitBytesPerSecond, _uploadLimitBytesPerSecond);
        }
    }
}
