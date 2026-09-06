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

    void ListenPortChangedAlert(int requestedPort, int actualPort, ListenTransport transport);

    void PeerBlockedAlert(ITorrent torrent, System.Net.IPEndPoint endpoint, PeerBlockReason reason);

    void PieceCompletedAlert(ITorrent torrent, int pieceIndex, int completedPieces, int totalPieces);

    void PieceHashFailedAlert(ITorrent torrent, int pieceIndex, int failures, System.Net.IPEndPoint? suspectedPeer);

    void PostAlert(Alert alert);

    void ProgressChangedAlert(ITorrent torrent, float progress, float selectionProgress, ulong finishedBytes, ulong totalBytes, int completedPieces, int totalPieces);

    void StateChangedAlert(ITorrent torrent, TorrentState previousState, TorrentState newState);

    void TorrentAlert(AlertId id, ITorrent torrent);

    void TorrentErrorAlert(ITorrent torrent, Exception exception);

    void TransferStatsAlert(ITorrent torrent, long downloaded, long uploaded, long downloadSpeed, long uploadSpeed, int connectedPeers);
}

internal class AlertsManager : IAlertsManager
{
    private readonly AlertSettings _settings;
    private readonly ConcurrentQueue<Alert> _alerts = new();

    private readonly Lock _lock = new();
    private readonly TimeProvider _timeProvider;
    private int _alertCount = 0;
    private uint _alertsMask = 0;

    // Drops since the last time a consumer was told, and the running total. Reported rather than
    // merely counted: an alert that vanishes without trace is indistinguishable from one that never
    // happened, and a consumer acting on the queue cannot tell that it has missed something.
    private long _droppedSinceReport;
    private long _droppedTotal;

    public AlertsManager(TimeProvider timeProvider)
        : this(timeProvider, new AlertSettings())
    {
    }

    public AlertsManager(TimeProvider timeProvider, AlertSettings settings)
    {
        _timeProvider = timeProvider;
        _settings = settings;
    }

    /// <inheritdoc />
    public long DroppedAlertCount => Interlocked.Read(ref _droppedTotal);

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

        // Cancellation is the only way out of the loop, and it always surfaces as
        // OperationCanceledException. Completing the enumeration gracefully instead would make
        // the outcome depend on whether the queue happened to be empty at the moment of
        // cancellation - the same call ending two different ways.
        cancellationToken.ThrowIfCancellationRequested();
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

    public void ListenPortChangedAlert(int requestedPort, int actualPort, ListenTransport transport)
    {
        if (!IsAlertRegistered(AlertId.ListenPortChanged))
        {
            return;
        }

        PostAlert(new ListenPortChangedAlert
        {
            Id = AlertId.ListenPortChanged,
            RequestedPort = requestedPort,
            ActualPort = actualPort,
            Transport = transport,
            Timestamp = _timeProvider.GetUtcNow()
        });
    }

    public void PeerBlockedAlert(ITorrent torrent, System.Net.IPEndPoint endpoint, PeerBlockReason reason)
    {
        if (!IsAlertRegistered(AlertId.PeerBlocked))
        {
            return;
        }

        PostAlert(new PeerBlockedAlert
        {
            Id = AlertId.PeerBlocked,
            Torrent = torrent,
            Endpoint = endpoint,
            Reason = reason,
            Timestamp = _timeProvider.GetUtcNow()
        });
    }

    public void PieceHashFailedAlert(ITorrent torrent, int pieceIndex, int failures, System.Net.IPEndPoint? suspectedPeer)
    {
        if (!IsAlertRegistered(AlertId.PieceHashFailed))
        {
            return;
        }

        PostAlert(new PieceHashFailedAlert
        {
            Id = AlertId.PieceHashFailed,
            Torrent = torrent,
            PieceIndex = pieceIndex,
            Failures = failures,
            SuspectedPeer = suspectedPeer,
            Timestamp = _timeProvider.GetUtcNow()
        });
    }

    public List<Alert> PopAlerts()
    {
        var result = new List<Alert>();

        // First, so the consumer learns it has a gap before it reads the alerts either side of it.
        // Built here rather than queued: a notice about a full queue cannot be one more thing that
        // needs room in it.
        long dropped = Interlocked.Exchange(ref _droppedSinceReport, 0);
        if (dropped > 0)
        {
            result.Add(new AlertsDroppedAlert
            {
                Id = AlertId.AlertsDropped,
                Dropped = dropped,
                TotalDropped = Interlocked.Read(ref _droppedTotal),
                Capacity = _settings.MaxQueueSize,
                Timestamp = _timeProvider.GetUtcNow()
            });
        }

        while (_alerts.TryDequeue(out var alert))
        {
            result.Add(alert);
            Interlocked.Decrement(ref _alertCount);
        }
        return result;
    }

    /// <summary>
    /// Queues an alert, enforcing <see cref="AlertSettings.MaxQueueSize"/> as a real limit.
    ///
    /// <para>
    /// Criticality decides who gives way, not who is exempt. This used to let a critical alert push
    /// the queue past its bound whenever the oldest entry was also critical - and the situation that
    /// fills the queue is a consumer that has stopped reading, so a run of critical alerts had
    /// nothing bounding it at all. Now a critical alert can still evict a droppable one ahead of it,
    /// but when everything queued is critical the configured policy settles it and the count holds.
    /// </para>
    /// </summary>
    public void PostAlert(Alert alert)
    {
        if (!IsAlertRegistered(alert.Id))
        {
            return;
        }

        int capacity = Math.Max(1, _settings.MaxQueueSize);
        int currentCount = Interlocked.Increment(ref _alertCount);
        if (currentCount > capacity && !TryMakeRoom(alert, capacity))
        {
            Interlocked.Decrement(ref _alertCount);
            RecordDrop();
            return;
        }

        _alerts.Enqueue(alert);
    }

    /// <summary>
    /// Frees one slot for <paramref name="incoming"/>, or reports that it should be refused instead.
    /// Returns true when a slot was freed.
    /// </summary>
    private bool TryMakeRoom(Alert incoming, int capacity)
    {
        // Drains to the capacity in force now, not by one. A capacity lowered while a backlog is
        // queued would otherwise only ever trade one alert for one, and the queue would sit at its
        // old size until a consumer happened to read it - which is exactly the consumer this bound
        // exists to survive.
        bool madeRoom = false;
        while (Interlocked.CompareExchange(ref _alertCount, 0, 0) > capacity)
        {
            if (!TryEvictOne(incoming))
            {
                return madeRoom;
            }

            madeRoom = true;
        }

        return true;
    }

    /// <summary>Discards one queued alert, or returns false if none may be discarded.</summary>
    private bool TryEvictOne(Alert incoming)
    {
        // A droppable alert at the head goes first, whatever the policy: nothing is served by
        // discarding a torrent-finished to keep a progress update that a later one supersedes.
        if (_alerts.TryPeek(out var oldest)
            && (!IsCritical(oldest.Id)
                || (_settings.OverflowPolicy == AlertOverflowPolicy.DropOldest && IsCritical(incoming.Id)))
            && _alerts.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _alertCount);
            RecordDrop();
            return true;
        }

        return false;
    }

    private void RecordDrop()
    {
        Interlocked.Increment(ref _droppedSinceReport);
        Interlocked.Increment(ref _droppedTotal);
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

    public void TransferStatsAlert(ITorrent torrent, long downloaded, long uploaded, long downloadSpeed, long uploadSpeed, int connectedPeers)
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
