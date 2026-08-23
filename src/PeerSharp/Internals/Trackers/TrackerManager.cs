using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Peers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;

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

    /// <summary>
    /// Bounds a BEP 31 <c>retry in</c> hint. The point of the extension is to obey the tracker, so
    /// the ceiling is the same 24 hours we already allow a tracker to ask for via <c>interval</c>
    /// rather than the circuit breaker's one hour - but it is still a ceiling, because the value is
    /// attacker-controlled and an unbounded one would silence this tracker for the process lifetime.
    /// </summary>
    private static TimeSpan ClampRetryHint(TimeSpan retryIn)
    {
        double seconds = Math.Clamp(retryIn.TotalSeconds, MinAnnounceIntervalSeconds, MaxAnnounceIntervalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }
    private readonly ILogger<TrackerManager> _logger;

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
        : this(torrent, trackerFactory, timeProvider, NullLogger<TrackerManager>.Instance)
    {
    }

    internal TrackerManager(Torrent torrent, ITrackerFactory trackerFactory, TimeProvider timeProvider, ILogger<TrackerManager> logger)
    {
        _logger = logger;
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

    /// <inheritdoc />
    public void AddTracker(string url)
    {
        // BEP 27: a private torrent announces only to the trackers named in its own metadata.
        // Announcing it anywhere else publishes a swarm that is meant to stay closed, which is
        // what private trackers ban accounts for - so refuse here rather than trust every caller
        // to check the flag first. This is the same gate already applied to the DHT, PEX and LSD;
        // the tracker path was the one way left to widen a private swarm from outside.
        if (_torrent.InfoFile.Info.IsPrivate)
        {
            _logger.LogDebug(
                "Refused tracker {Url}: {TorrentName} is a private torrent", url, _torrent.Name);
            return;
        }

        AddTrackerFromMetadata(url);
    }

    /// <summary>
    /// Adds a tracker that came from the torrent's own metadata.
    ///
    /// <para>
    /// Deliberately not subject to the private-torrent check in <see cref="AddTracker"/>: these are
    /// precisely the trackers a private torrent is supposed to use, so gating them too would leave it
    /// with nowhere to announce at all.
    /// </para>
    /// </summary>
    internal void AddTrackerFromMetadata(string url)
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

    public Task AnnounceAsync(string? url = null, CancellationToken cancellationToken = default)
    {
        // This only schedules announces - each one then runs on the tracker's own timeout and is
        // cancelled by StopAsync, not by this token. Honour the token for the scheduling step so
        // an already-cancelled caller does not silently get work queued on its behalf.
        cancellationToken.ThrowIfCancellationRequested();

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

        return Task.CompletedTask;
    }

    public async Task ScrapeAsync(string? url = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<TrackerInfo> toScrape = [];
        lock (_lock)
        {
            if (_disposal.IsDisposed)
            {
                return;
            }

            if (url != null)
            {
                var info = _trackers.FirstOrDefault(t => t.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
                if (info != null)
                {
                    toScrape.Add(info);
                }
            }
            else
            {
                toScrape.AddRange(GetActiveTrackersLocked());
            }
        }

        if (toScrape.Count == 0)
        {
            return;
        }

        // Awaited rather than scheduled, unlike an announce: the caller asked for counts and has
        // nowhere to read them from until the replies land.
        await Task.WhenAll(toScrape.Select(info => ScrapeOneAsync(info, cancellationToken))).ConfigureAwait(false);
    }

    private async Task ScrapeOneAsync(TrackerInfo info, CancellationToken cancellationToken)
    {
        try
        {
            await info.Tracker.ScrapeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One tracker refusing or not implementing scrape says nothing about the others, and the
            // counts a caller does get are still worth having. The failure is already reflected in
            // the tracker's status.
            Defect.ReportIfDefect(ex, $"scrape of {info.Url}", _logger);
            _logger.LogDebug(ex, "Scrape of {TrackerUrl} failed", info.Url);
        }
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
                if (info.RetryDisabled)
                {
                    statusType = TrackerStatusType.Disabled;
                }
                else if (info.CircuitState == CircuitBreakerState.Open)
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

                // A tracker disabled by BEP 31 has no next announce to report; leaving this at
                // MinValue is how the existing "nothing scheduled" case is already expressed.
                DateTimeOffset nextRetry = DateTimeOffset.MinValue;
                if (!info.RetryDisabled)
                {
                    if (info.NextRetryTime > DateTimeOffset.MinValue && info.CircuitState != CircuitBreakerState.Closed)
                    {
                        nextRetry = info.NextRetryTime;
                    }
                    else if (info.LastAnnounce != DateTimeOffset.MinValue)
                    {
                        nextRetry = info.LastAnnounce.AddSeconds(info.Interval);
                    }
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

                if (response.ExternalAddresses.Count > 0)
                {
                    foreach (var address in response.ExternalAddresses)
                    {
                        ReportExternalIp(info, address);
                    }
                }
                else
                {
                    ReportExternalIp(info, response.ExternalIp);
                }

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

                // BEP 31: "retry in: never" means stop asking this tracker altogether. Honour it for
                // the rest of the session but never persist it - a tracker that answers "Not a
                // tracker" today may be reconfigured tomorrow, and a stale permanent block would be
                // indistinguishable from a broken client.
                if (response.RetryHint is { Never: true })
                {
                    info.RetryDisabled = true;
                    info.Timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    _logger.LogInformation(
                        "Tracker {Url} asked us not to retry (BEP 31); no further announces this session", info.Url);
                }
                else if (_started)
                {
                    // Schedule retry based on circuit state
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

                    // BEP 31: a numeric hint replaces our guess, but only ever to wait longer. The
                    // tracker knows when it will be ready; it does not get to shorten a backoff we
                    // opened to protect it from us.
                    if (response.RetryHint is { Never: false } hint)
                    {
                        var requested = ClampRetryHint(hint.RetryIn);
                        if (requested > interval)
                        {
                            interval = requested;
                        }

                        info.NextRetryTime = _timeProvider.GetUtcNow() + interval;
                        _logger.LogDebug(
                            "Tracker {Url} asked us to retry in {RequestedSeconds}s (BEP 31); next attempt in {IntervalSeconds}s",
                            info.Url, (int)requested.TotalSeconds, (int)interval.TotalSeconds);
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

    /// <summary>
    /// BEP 24: forwards a tracker-reported external address to the DHT, where it counts as one vote
    /// towards the BEP 42 secure node ID.
    ///
    /// A tracker only ever gets one vote per distinct address. Without that, a single hostile or
    /// misconfigured tracker announcing every few minutes would reach the vote threshold on its own
    /// and drive a node ID regeneration; requiring a changed value means agreement has to come from
    /// distinct sources, which is the only reason this is worth wiring up at all given the DHT
    /// already reports our address.
    /// </summary>
    private void ReportExternalIp(TrackerInfo info, IPAddress? address)
    {
        if (address == null || !info.ReportedExternalIps.Add(address))
        {
            return;
        }

        var dht = _torrent.DhtManager;
        if (dht == null)
        {
            return;
        }

        try
        {
            dht.ReportExternalIp(address);
        }
        catch (Exception ex)
        {
            // Never let an advisory address report break an otherwise successful announce.
            _logger.LogDebug(ex, "Failed to report external IP {ExternalIP} from tracker {Url}", address, info.Url);
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
                shouldSendStopped = !info.RetryDisabled
                    && (_started
                        || info.LastAnnounce != DateTimeOffset.MinValue
                        || info.CurrentAnnounceTask != null);
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

        // Fire and forget, but ensure Deinit happens AFTER announce. Task.Run keeps the announce
        // off this (synchronous) caller's thread and pins the continuations to TaskScheduler.Default
        // rather than whatever SynchronizationContext the caller happens to have.
        var task = Task.Run(() => RunRemovedTrackerStopAsync(removed), CancellationToken.None);

        _removalTasks.TryAdd(task, 0);
        _ = task.ContinueWith(t => _removalTasks.TryRemove(t, out _), TaskScheduler.Default);

        return true;
    }

    private async Task RunRemovedTrackerStopAsync(TrackerInfo removed)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await removed.Tracker.AnnounceAsync(TrackerEvent.Stopped, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Failed to send Stopped event for removed tracker {Url}", removed.Url);
        }
        finally
        {
            removed.Tracker.Deinit();
        }
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

    /// <summary>
    /// Stops announcing and drains anything in flight.
    /// </summary>
    /// <param name="sendStoppedAnnounce">
    /// Whether to send the BEP 3 courtesy <c>stopped</c> announce. False when the torrent is only being
    /// rebuilt rather than actually stopped - after metadata arrives, for instance, where the info hash
    /// is unchanged and a <c>started</c> announce follows immediately. Saying we stopped and then that
    /// we started, milliseconds apart, is untrue and not free: the announce is bounded by
    /// StopAnnounceTimeout, so one unresponsive UDP tracker costs the full two seconds. Measured on a
    /// real magnet, 2.5 of the 5.75 seconds between the last metadata byte and the first block.
    /// </param>
    public async Task StopAsync(bool sendStoppedAnnounce = true)
    {
        List<TrackerInfo> toStop;
        List<CancellationTokenSource> ctsToCancel = [];
        List<Task> announcesToDrain = [];

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
                if (info.CurrentAnnounceTask != null)
                {
                    announcesToDrain.Add(info.CurrentAnnounceTask);
                }
                info.CurrentAnnounceTask = null;

                if (info.CurrentScrapeCts != null)
                {
                    ctsToCancel.Add(info.CurrentScrapeCts);
                    info.CurrentScrapeCts = null;
                }
            }
        }

        if (ctsToCancel.Count > 0)
        {
            // Materialise every CancelAsync call before awaiting: a lazily enumerated Select
            // would stop at the first source that throws synchronously, silently leaving the
            // remaining announces running - and the drain below would then wait on them.
            List<Task> cancellations = new(ctsToCancel.Count);
            foreach (var cts in ctsToCancel)
            {
                try
                {
                    cancellations.Add(cts.CancelAsync());
                }
                catch (ObjectDisposedException) { /* Already disposed - nothing left to cancel */ }
            }

            try
            {
                await Task.WhenAll(cancellations).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A registered cancellation callback threw; cancellation itself still happened.
                _logger.LogTrace(ex, "Error while cancelling in-flight tracker operations during stop");
            }
            finally
            {
                foreach (var cts in ctsToCancel)
                {
                    cts.Dispose();
                }
            }
        }

        if (announcesToDrain.Count > 0)
        {
            // Bounded like every other wait in this method. The announces were just cancelled,
            // but a tracker implementation that ignores its token must not be able to block
            // engine shutdown indefinitely.
            try
            {
                await Task.WhenAll(announcesToDrain).WaitAsync(StopAnnounceTimeout, _timeProvider).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                _logger.LogDebug(ex, "Timed out draining {Count} in-flight tracker announces during stop", announcesToDrain.Count);
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogTrace(ex, "Current tracker announces were cancelled during stop");
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Current tracker announces ended with errors during stop");
            }
        }

        Task[] stopAnnounces = sendStoppedAnnounce
            ? [.. toStop.Select(info => SendStoppedAnnounceAsync(info, _timeProvider))]
            : [];
        Task[] removals = [.. _removalTasks.Keys];
        await Task.WhenAll(stopAnnounces.Concat(removals)).ConfigureAwait(false);
        _removalTasks.Clear();
    }

    /// <summary>
    /// Sends the courtesy Stopped announce, bounded so a tracker that ignores its token cannot hold up
    /// shutdown. The bound runs on the injected clock like every other wait here, which is what lets a
    /// test drive it rather than measure it - the previous version used the system clock, so its test
    /// could only assert that a wall-clock stopwatch landed in a range, and a busy CI machine put it
    /// outside that range.
    /// </summary>
    private static async Task SendStoppedAnnounceAsync(TrackerInfo info, TimeProvider timeProvider)
    {
        // BEP 31: "never send this query again" includes the courtesy Stopped announce.
        if (info.RetryDisabled)
        {
            return;
        }

        using var timeoutCts = new CancellationTokenSource(StopAnnounceTimeout, timeProvider);
        try
        {
            await info.Tracker.AnnounceAsync(TrackerEvent.Stopped, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* Expected on timeout */ }
        catch (Exception) { /* Stop announces are best effort. */ }
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
            if (!_started || info.RetryDisabled)
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

            // BEP 31: the single choke point for every scheduled announce - timer ticks, manual
            // AnnounceAsync, StartAsync and tier advancement all land here - so honouring
            // "retry in: never" once covers all of them.
            if (info.RetryDisabled)
            {
                return;
            }

            // Cancel existing announce for this tracker
            info.CurrentAnnounceCts?.Cancel();
            info.CurrentAnnounceCts?.Dispose();
            info.CurrentAnnounceCts = new CancellationTokenSource();

            var ct = info.CurrentAnnounceCts.Token;

            // Task.Run, not Task.Yield: YieldAwaitable has no ConfigureAwait, so it resumes on
            // SynchronizationContext.Current when the caller has one. In a UI host that would
            // drag tracker network continuations onto the UI thread - and StopAsync awaits these
            // tasks, so a UI thread blocked on shutdown would deadlock. TaskScheduler.Default
            // also keeps tracker code off this lock.
            info.CurrentAnnounceTask = Task.Run(() => RunTrackedAnnounceAsync(info, evt, ct), CancellationToken.None);
        }
    }

    private async Task RunTrackedAnnounceAsync(TrackerInfo info, TrackerEvent evt, CancellationToken ct)
    {
        try
        {
            await info.Tracker.AnnounceAsync(evt, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            _logger.LogTrace(ex, "Tracked announce for {Url} was cancelled", info.Url);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unhandled exception in tracked announce for {Url}", info.Url);
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

        /// <summary>
        /// BEP 31: set when the tracker answered <c>retry in: never</c>. Session-scoped, never
        /// persisted.
        /// </summary>
        public bool RetryDisabled { get; set; }

        /// <summary>
        /// BEP 24: the last external address this tracker reported, so a single tracker can only
        /// contribute one vote per distinct address.
        /// </summary>
        public HashSet<IPAddress> ReportedExternalIps { get; } = [];

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
            // A tracker that answered BEP 31 "never" is spent, however few failures it recorded -
            // otherwise a tier holding one such tracker would never look exhausted and we would
            // never fall through to the next one.
            if (!info.RetryDisabled && info.CircuitState != CircuitBreakerState.Open && info.ConsecutiveFailures < FailureThreshold)
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
