using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace PeerSharp.Internals;

/// <summary>
/// Interface for alerts management. Enables dependency injection and testing.
/// </summary>
internal interface IAlertsManager : IAlerts
{
    void ConfigAlert(AlertId id, string configType);

    void MetadataAlert(AlertId id, ITorrent torrent);

    void MetadataProgressAlert(ITorrent torrent, float progress, int receivedPieces, int totalPieces);

    void PieceCompletedAlert(ITorrent torrent, int pieceIndex, int completedPieces, int totalPieces);

    void PostAlert(Alert alert);

    void ProgressChangedAlert(ITorrent torrent, float progress, float selectionProgress, ulong finishedBytes, ulong totalBytes, int completedPieces, int totalPieces);

    void StateChangedAlert(ITorrent torrent, TorrentState previousState, TorrentState newState);

    void TorrentAlert(AlertId id, ITorrent torrent);

    void TorrentErrorAlert(ITorrent torrent, Exception exception);

    void TransferStatsAlert(ITorrent torrent, long downloaded, long uploaded, int downloadSpeed, int uploadSpeed, int connectedPeers);
}

internal class AlertsManager : IAlertsManager
{
    private const int MaxAlertQueueSize = 10000;

    private readonly ConcurrentQueue<Alert> _alerts = new();

    private readonly Lock _lock = new();
    private readonly TimeProvider _timeProvider;
    private int _alertCount = 0;

    // Prevent unbounded growth
    private uint _alertsMask = 0;

    public AlertsManager(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    private static bool IsCritical(AlertId id)
    {
        const AlertId criticalMask =
            AlertId.TorrentAdded |
            AlertId.TorrentRemoved |
            AlertId.TorrentError |
            AlertId.MetadataInitialized |
            AlertId.TorrentFinished;

        return (id & criticalMask) != 0;
    }

    public void ConfigAlert(AlertId id, string configType)
    {
        if (!IsAlertRegistered(id))
        {
            return;
        }

        PostAlert(new ConfigAlert
        {
            Id = id,
            ConfigType = configType,
            Timestamp = _timeProvider.GetUtcNow()
        });
    }

    public async IAsyncEnumerable<Alert> GetAlertsAsync(
        TimeSpan? pollingInterval = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var interval = pollingInterval ?? TimeSpan.FromMilliseconds(100);

        while (!cancellationToken.IsCancellationRequested)
        {
            var alerts = PopAlerts();
            if (alerts.Count > 0)
            {
                foreach (var alert in alerts)
                {
                    yield return alert;
                }
            }
            else
            {
                await Task.Delay(interval, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public void MetadataAlert(AlertId id, ITorrent torrent)
    {
        if (!IsAlertRegistered(id))
        {
            return;
        }

        PostAlert(new SimpleMetadataAlert
        {
            Id = id,
            Torrent = torrent,
            Timestamp = _timeProvider.GetUtcNow()
        });
    }

    public void MetadataProgressAlert(ITorrent torrent, float progress, int receivedPieces, int totalPieces)
    {
        if (!IsAlertRegistered(AlertId.MetadataProgressChanged))
        {
            return;
        }

        PostAlert(new MetadataProgressAlert
        {
            Id = AlertId.MetadataProgressChanged,
            Torrent = torrent,
            Progress = progress,
            ReceivedPieces = receivedPieces,
            TotalPieces = totalPieces,
            Timestamp = _timeProvider.GetUtcNow()
        });
    }

    public void PieceCompletedAlert(ITorrent torrent, int pieceIndex, int completedPieces, int totalPieces)
    {
        if (!IsAlertRegistered(AlertId.PieceCompleted))
        {
            return;
        }

        PostAlert(new PieceCompletedAlert
        {
            Id = AlertId.PieceCompleted,
            Torrent = torrent,
            PieceIndex = pieceIndex,
            CompletedPieces = completedPieces,
            TotalPieces = totalPieces,
            Timestamp = _timeProvider.GetUtcNow()
        });
    }

    public List<Alert> PopAlerts()
    {
        var result = new List<Alert>();
        while (_alerts.TryDequeue(out var alert))
        {
            result.Add(alert);
            Interlocked.Decrement(ref _alertCount);
        }
        return result;
    }

    public void PostAlert(Alert alert)
    {
        if (IsAlertRegistered(alert.Id))
        {
            int currentCount = Interlocked.Increment(ref _alertCount);
            if (currentCount > MaxAlertQueueSize)
            {
                // If queue is full, try to drop the oldest if it's not critical
                if (_alerts.TryPeek(out var oldest) && !IsCritical(oldest.Id))
                {
                    if (_alerts.TryDequeue(out _))
                    {
                        Interlocked.Decrement(ref _alertCount);
                    }
                }
                else if (!IsCritical(alert.Id))
                {
                    // If we can't drop the oldest (it's critical) and we are not critical, drop the new alert
                    Interlocked.Decrement(ref _alertCount);
                    return;
                }
                // If we are critical, we allow the queue to grow temporarily (no return)
            }

            _alerts.Enqueue(alert);
        }
    }

    public void ProgressChangedAlert(ITorrent torrent, float progress, float selectionProgress, ulong finishedBytes, ulong totalBytes, int completedPieces, int totalPieces)
    {
        if (!IsAlertRegistered(AlertId.ProgressChanged))
        {
            return;
        }

        PostAlert(new ProgressChangedAlert
        {
            Id = AlertId.ProgressChanged,
            Torrent = torrent,
            Progress = progress,
            SelectionProgress = selectionProgress,
            FinishedBytes = finishedBytes,
            TotalBytes = totalBytes,
            CompletedPieces = completedPieces,
            TotalPieces = totalPieces,
            Timestamp = _timeProvider.GetUtcNow()
        });
    }

    public void RegisterAlerts(uint alertMask)
    {
        lock (_lock)
        {
            _alertsMask = alertMask;
        }
    }

    public void StateChangedAlert(ITorrent torrent, TorrentState previousState, TorrentState newState)
    {
        if (!IsAlertRegistered(AlertId.TorrentStateChanged))
        {
            return;
        }

        PostAlert(new StateChangedAlert
        {
            Id = AlertId.TorrentStateChanged,
            Torrent = torrent,
            PreviousState = previousState,
            NewState = newState,
            Timestamp = _timeProvider.GetUtcNow()
        });
    }

    public void TorrentAlert(AlertId id, ITorrent torrent)
    {
        if (!IsAlertRegistered(id))
        {
            return;
        }

        PostAlert(new SimpleTorrentAlert
        {
            Id = id,
            Torrent = torrent,
            Timestamp = _timeProvider.GetUtcNow()
        });
    }

    public void TorrentErrorAlert(ITorrent torrent, Exception exception)
    {
        if (!IsAlertRegistered(AlertId.TorrentError))
        {
            return;
        }

        PostAlert(new TorrentErrorAlert
        {
            Id = AlertId.TorrentError,
            Torrent = torrent,
            Exception = exception,
            Timestamp = _timeProvider.GetUtcNow()
        });
    }

    public void TransferStatsAlert(ITorrent torrent, long downloaded, long uploaded, int downloadSpeed, int uploadSpeed, int connectedPeers)
    {
        if (!IsAlertRegistered(AlertId.TransferStatsUpdated))
        {
            return;
        }

        PostAlert(new TransferStatsAlert
        {
            Id = AlertId.TransferStatsUpdated,
            Torrent = torrent,
            Downloaded = downloaded,
            Uploaded = uploaded,
            DownloadSpeed = downloadSpeed,
            UploadSpeed = uploadSpeed,
            ConnectedPeers = connectedPeers,
            Timestamp = _timeProvider.GetUtcNow()
        });
    }

    private bool IsAlertRegistered(AlertId id)
    {
        lock (_lock)
        {
            if (_alertsMask == 0)
            {
                return false;
            }

            return ((uint)id & _alertsMask) != 0;
        }
    }
}
