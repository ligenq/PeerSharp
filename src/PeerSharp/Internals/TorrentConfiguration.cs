using PeerSharp.Internals.Bandwidth;
using System.Collections.Concurrent;

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
    private int _maxConnections;
    private int _maxUploadSlots;
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

    // Caller-set priorities for individual pieces, overriding what the file selection implies. Read
    // by the picker on its hot path, hence concurrent - and checked for emptiness first, so a torrent
    // that never sets one pays a field read rather than a lookup per piece.
    public ConcurrentDictionary<int, Priority> PiecePriorities { get; } = new();

    // 0 means "use the engine-wide setting", matching how the rate limits treat 0.
    public int MaxConnections
    {
        get => Volatile.Read(ref _maxConnections);
        set => Volatile.Write(ref _maxConnections, value);
    }

    public int MaxUploadSlots
    {
        get => Volatile.Read(ref _maxUploadSlots);
        set => Volatile.Write(ref _maxUploadSlots, value);
    }

    public bool QueueAutoStart { get; set; } = true;
    public int QueuePriority { get; set; }
    public float? RatioLimit { get; set; }
    public TimeSpan? SeedTimeLimit { get; set; }

    // Lives here rather than on SuperSeedManager because that manager is rebuilt when a magnet's
    // metadata arrives, and the caller's choice has to survive the rebuild.
    public bool SuperSeeding { get; set; }


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
