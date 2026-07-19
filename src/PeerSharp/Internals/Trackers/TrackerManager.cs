using Microsoft.Extensions.Logging;
using PeerSharp.Internals.Peers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace PeerSharp.Internals.Trackers;

/// <summary>
/// Circuit breaker state for tracker failure handling.
/// Prevents hammering failed trackers with exponential backoff.
/// </summary>
internal enum CircuitBreakerState
{
    /// <summary>Normal operation - requests pass through.</summary>
    Closed,

    /// <summary>Circuit is open - requests are blocked.</summary>
    Open,

    /// <summary>Testing if service recovered - single request allowed.</summary>
    HalfOpen
}

internal class TrackerManager : IAsyncDisposable, ITrackerCallback, ITrackers
{
    private const double BackoffMultiplier = 2.0;
    private const int BaseBackoffSeconds = 60;
    private static readonly TimeSpan StopAnnounceTimeout = TimeSpan.FromSeconds(2);

    // Circuit breaker configuration
    private const int FailureThreshold = 3;

    // Initial backoff when circuit opens
    private const int MaxBackoffSeconds = 3600;

    private const int SuccessThresholdForReset = 5;

    // Bounds for a tracker-supplied announce interval. Clamping guards against a malformed
    // or hostile tracker sending a value that overflows int when cast (scheduling a negative
    // timer delay), hammers us with a near-zero interval, or effectively disables announces.
    private const int MinAnnounceIntervalSeconds = 30;
    private const int MaxAnnounceIntervalSeconds = 24 * 60 * 60;

    private readonly Lock _lock = new();

    private static int ClampAnnounceInterval(uint seconds)
    {
        return (int)Math.Clamp(seconds, (uint)MinAnnounceIntervalSeconds, (uint)MaxAnnounceIntervalSeconds);
    }
    private readonly ILogger<TrackerManager> _logger = TorrentLoggerFactory.CreateLogger<TrackerManager>();

    // Track tasks for removed trackers (cleanup)
    private readonly ConcurrentDictionary<Task, byte> _removalTasks = new();

    private readonly TimeProvider _timeProvider;
    private readonly Torrent _torrent;
    private readonly ITrackerFactory _trackerFactory;
    private readonly Dictionary<ITracker, TrackerInfo> _trackerLookup = [];
    private readonly List<TrackerInfo> _trackers = [];
    private readonly List<TrackerTier> _tiers = [];
    private readonly HashSet<string> _trackerUrls = new(StringComparer.OrdinalIgnoreCase); // O(1) URL dedup
    private AtomicDisposal _disposal = new();

    // O(1) tracker lookup
    private bool _started;
    private int _activeTierIndex = -1;

    // Failures before opening circuit

    // Successes before resetting backoff history
    // Maximum backoff (1 hour)
    // Exponential backoff multiplier

    public TrackerManager(Torrent torrent, ITrackerFactory trackerFactory, TimeProvider timeProvider)
    {
        _torrent = torrent;
        _trackerFactory = trackerFactory;
        _timeProvider = timeProvider;
    }

    public void AddTrackers(IEnumerable<IEnumerable<string>> tiers)
    {
        if (tiers == null)
        {
            return;
        }

        lock (_lock)
        {
            int tierIndex = 0;
            foreach (var tier in tiers)
            {
                foreach (var url in tier)
                {
                    AddTrackerInternal(url, tierIndex);
                }

                EnsureTier(tierIndex);
                tierIndex++;
            }

            if (_activeTierIndex < 0 && _tiers.Count > 0)
            {
                _activeTierIndex = 0;
            }
        }
    }

    public void AddTracker(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        lock (_lock)
        {
            int tierIndex = _tiers.Count > 0 ? Math.Max(0, _activeTierIndex) : -1;
            AddTrackerInternal(url, tierIndex);
        }
    }

    public async Task AnnounceAsync(string? url = null, CancellationToken cancellationToken = default)
    {
        List<TrackerInfo> toAnnounce = [];
        lock (_lock)
        {
            if (url != null)
            {
                var info = _trackers.FirstOrDefault(t => t.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
                if (info != null)
                {
                    toAnnounce.Add(info);
                }
            }
            else
            {
                toAnnounce.AddRange(GetActiveTrackersLocked());
            }

            foreach (var info in toAnnounce)
            {
                TrackedAnnounce(info, TrackerEvent.None);
            }
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public void AnnounceCompleted()
    {
        lock (_lock)
        {
            foreach (var info in GetActiveTrackersLocked())
            {
                TrackedAnnounce(info, TrackerEvent.Completed);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposal.MarkDisposed())
        {
            await StopAsync().ConfigureAwait(false);
        }
        GC.SuppressFinalize(this);
    }

    public IReadOnlyList<TrackerStatus> GetTrackers()
    {
        lock (_lock)
        {
            var result = new List<TrackerStatus>(_trackers.Count);
            foreach (var info in _trackers)
            {
                TrackerStatusType statusType;
                if (info.CircuitState == CircuitBreakerState.Open)
                {
                    statusType = TrackerStatusType.CircuitOpen;
                }
                else if (info.LastAnnounce == DateTimeOffset.MinValue)
                {
                    statusType = TrackerStatusType.Unknown;
                }
                else
                {
                    statusType = info.IsWorking ? TrackerStatusType.Working : TrackerStatusType.NotWorking;
                }

                DateTimeOffset nextRetry = DateTimeOffset.MinValue;
                if (info.NextRetryTime > DateTimeOffset.MinValue && info.CircuitState != CircuitBreakerState.Closed)
                {
                    nextRetry = info.NextRetryTime;
                }
                else if (info.LastAnnounce != DateTimeOffset.MinValue)
                {
                    nextRetry = info.LastAnnounce.AddSeconds(info.Interval);
                }

                result.Add(new TrackerStatus(
                    info.Url,
                    statusType,
                    info.LastAnnounce,
                    nextRetry,
                    info.Interval,
                    info.ConsecutiveFailures,
                    info.LastError,
                    info.SeedCount,
                    info.LeechCount));
            }
            return result.AsReadOnly();
        }
    }

    public void OnAnnounceResult(bool success, AnnounceResponse response, ITracker tracker, string? errorMessage = null)
    {
        lock (_lock)
        {
            // O(1) lookup instead of FirstOrDefault
            if (!_trackerLookup.TryGetValue(tracker, out var info))
            {
                return;
            }

            info.CurrentAnnounceCts?.Dispose();
            info.CurrentAnnounceCts = null;

            info.LastError = errorMessage;

            if (success)
            {
                info.IsWorking = true;
                info.LastAnnounce = _timeProvider.GetUtcNow();
                info.MinInterval = response.MinInterval.HasValue ? ClampAnnounceInterval(response.MinInterval.Value) : null;
                int effectiveInterval = ClampAnnounceInterval(response.Interval);
                if (info.MinInterval.HasValue)
                {
                    effectiveInterval = Math.Max(effectiveInterval, info.MinInterval.Value);
                }
                info.Interval = effectiveInterval;
                info.SeedCount = response.SeedCount;
                info.LeechCount = response.LeechCount;

                // Circuit breaker: Close circuit on success
                CloseCircuit(info);

                // Reset backoff history after multiple consecutive successes
                // (handled implicitly by not incrementing CircuitOpenCount)

                // Schedule next announce at normal interval
                if (_started)
                {
                    info.Timer.Change(TimeSpan.FromSeconds(info.Interval), Timeout.InfiniteTimeSpan);
                }

                MarkTierSuccess(info.TierIndex);

                // Add peers to peer manager
                try
                {
                    _torrent.PeersInternal.AddPeers(response.Peers, PeerSourceKind.Tracker, null);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to add peers"); }

                _logger.LogInformation("Tracker {Url} returned {PeersCount} peers", info.Url, response.Peers.Count);
            }
            else
            {
                info.IsWorking = false;
                info.ConsecutiveFailures++;

                // Circuit breaker logic
                if (info.CircuitState == CircuitBreakerState.HalfOpen || info.ConsecutiveFailures >= FailureThreshold)
                {
                    OpenCircuit(info);
                }

                // Schedule retry based on circuit state
                if (_started)
                {
                    TimeSpan interval;
                    if (info.CircuitState == CircuitBreakerState.Open)
                    {
                        // Use circuit breaker backoff
                        var backoffMs = (info.NextRetryTime - _timeProvider.GetUtcNow()).TotalMilliseconds;
                        interval = TimeSpan.FromMilliseconds(Math.Max(1000, backoffMs));
                    }
                    else
                    {
                        // Circuit still closed - use short retry
                        interval = TimeSpan.FromMinutes(1);
                    }
                    info.Timer.Change(interval, Timeout.InfiniteTimeSpan);
                }

                if (_activeTierIndex >= 0 && info.TierIndex == _activeTierIndex)
                {
                    var tier = GetTier(info.TierIndex);
                    if (tier != null && IsTierExhausted(tier))
                    {
                        AdvanceTierLocked();
                    }
                }
            }
        }
    }

    public void OnScrapeResult(bool success, ScrapeResponse response, ITracker tracker)
    {
        // Scrape results can be used to update tracker stats if needed
        lock (_lock)
        {
            if (_trackerLookup.TryGetValue(tracker, out var info))
            {
                info.CurrentScrapeCts?.Dispose();
                info.CurrentScrapeCts = null;

                if (success)
                {
                    info.SeedCount = response.SeedCount;
                    info.LeechCount = response.LeechCount;
                    _logger.LogInformation("Tracker {Url} scrape: {SeedCount} seeds, {LeechCount} leeches", info.Url, response.SeedCount, response.LeechCount);
                }
                else
                {
                    _logger.LogWarning("Tracker {Url} scrape failed", info.Url);
                }
            }
        }
    }

    public bool RemoveTracker(string url)
    {
        TrackerInfo? removed = null;
        bool shouldSendStopped = false;

        lock (_lock)
        {
            var info = _trackers.FirstOrDefault(t => t.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
            if (info != null)
            {
                shouldSendStopped = _started
                    || info.LastAnnounce != DateTimeOffset.MinValue
                    || info.CurrentAnnounceTask != null;
                info.Dispose();

                _trackers.Remove(info);
                _trackerUrls.Remove(url);
                _trackerLookup.Remove(info.Tracker);
                if (info.TierIndex >= 0)
                {
                    var tier = GetTier(info.TierIndex);
                    tier?.Trackers.Remove(info);
                }
                removed = info;
            }
        }

        if (removed == null)
        {
            return false;
        }

        if (!shouldSendStopped)
        {
            removed.Tracker.Deinit();
            return true;
        }

        // Fire and forget, but ensure Deinit happens AFTER announce
        var task = Task.Run(async () =>
        {
            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await removed.Tracker.AnnounceAsync(TrackerEvent.Stopped, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Log but otherwise ignore errors during removal
                _logger.LogTrace(ex, "Failed to send Stopped event for removed tracker {Url}", removed.Url);
            }
            finally
            {
                removed.Tracker.Deinit();
            }
        });

        _removalTasks.TryAdd(task, 0);
        _ = task.ContinueWith(t => _removalTasks.TryRemove(t, out _), TaskScheduler.Default);

        return true;
    }

    public Task StartAsync()
    {
        lock (_lock)
        {
            _started = true;
            foreach (var info in GetActiveTrackersLocked())
            {
                TrackedAnnounce(info, TrackerEvent.Started);
            }
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        List<TrackerInfo> toStop;
        List<CancellationTokenSource> ctsToCancel = [];

        lock (_lock)
        {
            _started = false;
            toStop = [.. _trackers];
            foreach (var info in toStop)
            {
                info.Timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

                if (info.CurrentAnnounceCts != null)
                {
                    ctsToCancel.Add(info.CurrentAnnounceCts);
                    info.CurrentAnnounceCts = null;
                }
                info.CurrentAnnounceTask = null;

                if (info.CurrentScrapeCts != null)
                {
                    ctsToCancel.Add(info.CurrentScrapeCts);
                    info.CurrentScrapeCts = null;
                }
            }
        }

        foreach (var cts in ctsToCancel)
        {
            try
            {
                await cts.CancelAsync().ConfigureAwait(false);
                cts.Dispose();
            }
            catch (ObjectDisposedException) { /* Already disposed */ }
        }

        foreach (var info in toStop)
        {
            var timeoutCts = new CancellationTokenSource(StopAnnounceTimeout);
            _ = Task.Run(async () =>
            {
                try
                {
                    await info.Tracker.AnnounceAsync(TrackerEvent.Stopped, timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { /* Expected on timeout */ }
                catch (Exception)
                {
                    // Ignore errors during stop announce
                }
                finally
                {
                    timeoutCts.Dispose();
                }
            });
        }

        _removalTasks.Clear();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Resets the circuit breaker completely (e.g., after prolonged success).
    /// </summary>
    private static void ResetCircuit(TrackerInfo info)
    {
        info.CircuitState = CircuitBreakerState.Closed;
        info.ConsecutiveFailures = 0;
        info.CircuitOpenCount = 0;
    }

    /// <summary>
    /// Closes the circuit breaker after a successful request.
    /// </summary>
    private void CloseCircuit(TrackerInfo info)
    {
        var previousState = info.CircuitState;
        info.CircuitState = CircuitBreakerState.Closed;
        info.ConsecutiveFailures = 0;
        info.ConsecutiveSuccesses++;

        if (previousState != CircuitBreakerState.Closed)
        {
            _logger.LogInformation("Circuit breaker CLOSED for {Url}", info.Url);
        }

        // Reset backoff history after sustained success
        if (info.ConsecutiveSuccesses >= SuccessThresholdForReset && info.CircuitOpenCount > 0)
        {
            ResetCircuit(info);
            _logger.LogInformation("Circuit breaker RESET for {Url} after {Threshold} consecutive successes", info.Url, SuccessThresholdForReset);
        }
    }

    /// <summary>
    /// Timer callback that respects circuit breaker state.
    /// </summary>
    private void OnTimerTick(object? state)
    {
        var info = (TrackerInfo)state!;
        lock (_lock)
        {
            if (!_started)
            {
                return;
            }

            switch (info.CircuitState)
            {
                case CircuitBreakerState.Closed:
                    // Normal operation - announce
                    TrackedAnnounce(info, TrackerEvent.None);
                    break;

                case CircuitBreakerState.Open:
                    // Check if we should transition to half-open
                    if (_timeProvider.GetUtcNow() >= info.NextRetryTime)
                    {
                        info.CircuitState = CircuitBreakerState.HalfOpen;
                        _logger.LogDebug("Circuit breaker for {Url} transitioning to HALF-OPEN", info.Url);
                        TrackedAnnounce(info, TrackerEvent.None);
                    }
                    else
                    {
                        // Still in backoff period - reschedule timer
                        var remaining = info.NextRetryTime - _timeProvider.GetUtcNow();
                        if (remaining <= TimeSpan.Zero)
                        {
                            remaining = TimeSpan.FromSeconds(1);
                        }

                        info.Timer.Change(remaining, Timeout.InfiniteTimeSpan);
                        _logger.LogDebug("Circuit breaker OPEN for {Url}, retry in {RetrySeconds}s", info.Url, (int)remaining.TotalSeconds);
                    }
                    break;

                case CircuitBreakerState.HalfOpen:
                    // Already testing - allow single request
                    TrackedAnnounce(info, TrackerEvent.None);
                    break;
            }
        }
    }

    public void OnMultiScrapeResult(bool success, MultiScrapeResponse response, ITracker tracker)
    {
        lock (_lock)
        {
            if (_trackerLookup.TryGetValue(tracker, out var info))
            {
                info.CurrentScrapeCts?.Dispose();
                info.CurrentScrapeCts = null;

                if (success)
                {
                    var key = _torrent.Hash.ToHexStringUpper();
                    if (response.Results.TryGetValue(key, out var scrape))
                    {
                        info.SeedCount = scrape.SeedCount;
                        info.LeechCount = scrape.LeechCount;
                        _logger.LogInformation("Tracker {Url} multi-scrape: {SeedCount} seeds, {LeechCount} leeches", info.Url, scrape.SeedCount, scrape.LeechCount);
                    }
                }
                else
                {
                    _logger.LogWarning("Tracker {Url} multi-scrape failed", info.Url);
                }
            }
        }
    }

    /// <summary>
    /// Opens the circuit breaker for a tracker after repeated failures.
    /// </summary>
    private void OpenCircuit(TrackerInfo info)
    {
        info.CircuitState = CircuitBreakerState.Open;
        info.CircuitOpenedAt = _timeProvider.GetUtcNow();
        info.CircuitOpenCount++;
        info.ConsecutiveSuccesses = 0; // Reset success counter on failure

        // Calculate exponential backoff
        int backoffSeconds = (int)(BaseBackoffSeconds * Math.Pow(BackoffMultiplier, info.CircuitOpenCount - 1));
        backoffSeconds = Math.Min(backoffSeconds, MaxBackoffSeconds);

        info.NextRetryTime = _timeProvider.GetUtcNow().AddSeconds(backoffSeconds);

        _logger.LogInformation("Circuit breaker OPENED for {Url} (failure #{Failures}, open count: {OpenCount}, backoff: {Backoff}s)",
            info.Url, info.ConsecutiveFailures, info.CircuitOpenCount, backoffSeconds);
    }

    private void TrackedAnnounce(TrackerInfo info, TrackerEvent evt)
    {
        lock (_lock)
        {
            if (_disposal.IsDisposed)
            {
                return;
            }

            if (_tiers.Count > 0 && info.TierIndex != _activeTierIndex)
            {
                return;
            }

            // Cancel existing announce for this tracker
            info.CurrentAnnounceCts?.Cancel();
            info.CurrentAnnounceCts?.Dispose();
            info.CurrentAnnounceCts = new CancellationTokenSource();

            var ct = info.CurrentAnnounceCts.Token;
            info.CurrentAnnounceTask = Task.Run(async () =>
            {
                try
                {
                    await info.Tracker.AnnounceAsync(evt, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { /* Expected */ }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unhandled exception in tracked announce for {Url}", info.Url);
                }
            }, ct);
        }
    }

    [ExcludeFromCodeCoverage]
    private sealed class TrackerInfo : IDisposable
    {
        private AtomicDisposal _disposal = new();
        public int CircuitOpenCount { get; set; }
        public int TierIndex { get; set; }

        // For exponential backoff
        public DateTimeOffset CircuitOpenedAt { get; set; }

        // Circuit breaker state
        public CircuitBreakerState CircuitState { get; set; } = CircuitBreakerState.Closed;

        public int ConsecutiveFailures { get; set; }
        public int ConsecutiveSuccesses { get; set; }
        public CancellationTokenSource? CurrentAnnounceCts { get; set; }
        public Task? CurrentAnnounceTask { get; set; }
        public CancellationTokenSource? CurrentScrapeCts { get; set; }
        public int Interval { get; set; } = 600;
        public int? MinInterval { get; set; }
        public bool IsWorking { get; set; }
        public DateTimeOffset LastAnnounce { get; set; }
        public string? LastError { get; set; }
        public uint LeechCount { get; set; }

        // Default 10 mins
        // For resetting backoff history
        public DateTimeOffset NextRetryTime { get; set; }

        public uint SeedCount { get; set; }
        public ITimer Timer { get; set; } = null!;
        public ITracker Tracker { get; set; } = null!;
        public string Url { get; set; } = string.Empty;

        public void Dispose()
        {
            if (_disposal.MarkDisposed())
            {
                Timer.Dispose();
                CurrentAnnounceCts?.Cancel();
                CurrentAnnounceCts?.Dispose();
                CurrentScrapeCts?.Cancel();
                CurrentScrapeCts?.Dispose();
            }
        }
    }

    [ExcludeFromCodeCoverage]
    private sealed class TrackerTier
    {
        public int Index { get; init; }
        public List<TrackerInfo> Trackers { get; } = [];
        public int ConsecutiveFailures { get; set; }
        public DateTimeOffset LastSuccess { get; set; } = DateTimeOffset.MinValue;
    }

    private void AddTrackerInternal(string url, int tierIndex)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        // O(1) deduplication check
        if (!_trackerUrls.Add(url))
        {
            return;
        }

        ITracker? tracker = _trackerFactory.CreateTracker(url, _timeProvider);
        if (tracker == null)
        {
            return;
        }

        tracker.Init(url, _torrent, this);

        var info = new TrackerInfo
        {
            Tracker = tracker,
            Url = url,
            TierIndex = tierIndex
        };

        info.Timer = _timeProvider.CreateTimer(OnTimerTick, info, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        _trackers.Add(info);
        _trackerLookup[tracker] = info;

        if (tierIndex >= 0)
        {
            EnsureTier(tierIndex).Trackers.Add(info);
        }

        if (_started)
        {
            // Announce immediately (or stagger)
            TrackedAnnounce(info, TrackerEvent.Started);
        }
    }

    private TrackerTier EnsureTier(int index)
    {
        while (_tiers.Count <= index)
        {
            _tiers.Add(new TrackerTier { Index = _tiers.Count });
        }

        return _tiers[index];
    }

    private TrackerTier? GetTier(int index)
    {
        if (index < 0 || index >= _tiers.Count)
        {
            return null;
        }

        return _tiers[index];
    }

    private IReadOnlyList<TrackerInfo> GetActiveTrackersLocked()
    {
        if (_tiers.Count == 0 || _activeTierIndex < 0 || _activeTierIndex >= _tiers.Count)
        {
            return _trackers;
        }

        return _tiers[_activeTierIndex].Trackers;
    }

    private void MarkTierSuccess(int tierIndex)
    {
        if (tierIndex < 0)
        {
            return;
        }

        var tier = GetTier(tierIndex);
        if (tier == null)
        {
            return;
        }

        tier.ConsecutiveFailures = 0;
        tier.LastSuccess = _timeProvider.GetUtcNow();
#pragma warning disable S3440 // Useless conditional
        if (_activeTierIndex != tierIndex)
        {
            _activeTierIndex = tierIndex;
        }
#pragma warning restore S3440 // Useless conditional
    }

    private static bool IsTierExhausted(TrackerTier tier)
    {
        if (tier.Trackers.Count == 0)
        {
            return true;
        }

        foreach (var info in tier.Trackers)
        {
            if (info.CircuitState != CircuitBreakerState.Open && info.ConsecutiveFailures < FailureThreshold)
            {
                return false;
            }
        }

        return true;
    }

    private void AdvanceTierLocked()
    {
        int start = _activeTierIndex >= 0 ? _activeTierIndex : 0;
        int next = (start + 1) % _tiers.Count;
        if (next == _activeTierIndex)
        {
            return;
        }

        _activeTierIndex = next;
        foreach (var info in _tiers[_activeTierIndex].Trackers)
        {
            TrackedAnnounce(info, TrackerEvent.Started);
        }
    }
}
