using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;

namespace PeerSharp.Internals.Bandwidth;

/// <summary>
/// Interface for bandwidth management. Enables dependency injection and testing.
/// </summary>
internal interface IBandwidthManager : IBandwidth, IAsyncDisposable
{
    void Configure(int updateIntervalMs);

    BandwidthChannel GetChannel(string name);

    Task<int> RequestBandwidthAsync(IBandwidthUser user, int amount, int priority, string[] channelNames, CancellationToken ct = default);

    /// <summary>
    /// Returns unused bandwidth back to the specified channels.
    /// CRITICAL: Must be called when reserved bandwidth is not used (e.g., cancellation, timeout, error).
    /// </summary>
    void ReturnBandwidth(int amount, string[] channelNames);

    /// <summary>
    /// Removes all channels associated with a torrent to prevent memory leaks when torrents are stopped/removed.
    /// </summary>
    void RemoveTorrentChannels(ITorrent torrent);

    void Start();
}

internal class BandwidthManager : IBandwidthManager
{
    public const string GlobalDownload = "GlobalDownload";
    public const string GlobalUpload = "GlobalUpload";
    public const string GlobalDiskRead = "GlobalDiskRead";
    public const string GlobalDiskWrite = "GlobalDiskWrite";
    private readonly HashSet<IBandwidthUser> _activeUsers = [];
    private readonly ConcurrentDictionary<string, BandwidthChannel> _channels = new();

    // To prevent duplicates in RR queue
    private readonly Lock _lock = new();

    private readonly ILogger<BandwidthManager> _logger;

    // Fairness structures
    private readonly Dictionary<IBandwidthUser, Queue<BandwidthRequest>> _pendingRequests = [];

    private readonly Queue<IBandwidthUser> _roundRobinQueue = new();
    private readonly TimeProvider _timeProvider;

    // Lock-free tracking for fast path optimization
    // Tracks users with pending requests to avoid lock in common case
    private readonly ConcurrentDictionary<IBandwidthUser, byte> _usersWithPendingRequests = new();

    private AtomicDisposal _disposal = new();
    private DateTimeOffset _lastStatusLog = DateTimeOffset.MinValue;
    private long _lastTick;
    private int _started;
    private ITimer? _timer;
    private int _totalGranted = 0;
    private int _updateIntervalMs;

    /// <summary>
    /// THROUGHPUT OPTIMIZATION: Configurable update interval
    /// Lower interval = lower latency, higher throughput, slightly higher CPU usage
    /// 10ms (default) = optimized for gigabit+ connections
    /// 100ms (old default) = lower CPU, higher latency
    /// </summary>
    public BandwidthManager(int updateIntervalMs, TimeProvider timeProvider)
        : this(updateIntervalMs, timeProvider, NullLoggerFactory.Instance)
    {
    }

    public BandwidthManager(int updateIntervalMs, TimeProvider timeProvider, ILoggerFactory loggerFactory)
    {
        _updateIntervalMs = Math.Clamp(updateIntervalMs, 1, 100);
        _timeProvider = timeProvider;
        _logger = loggerFactory.CreateLogger<BandwidthManager>();
        _channels[GlobalDownload] = new BandwidthChannel(_timeProvider);
        _channels[GlobalUpload] = new BandwidthChannel(_timeProvider);
        _channels[GlobalDiskRead] = new BandwidthChannel(_timeProvider);
        _channels[GlobalDiskWrite] = new BandwidthChannel(_timeProvider);

        _lastTick = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        _logger.LogDebug("BandwidthManager initialized with {UpdateInterval}ms update interval", _updateIntervalMs);
    }

    /// <summary>
    /// Configures the update interval. Must be called before Start().
    /// </summary>
    public void Configure(int updateIntervalMs)
    {
        if (_started == 1)
        {
            throw new InvalidOperationException("Cannot configure BandwidthManager after Start() has been called");
        }

        _updateIntervalMs = Math.Clamp(updateIntervalMs, 1, 100);
        _logger.LogDebug("BandwidthManager update interval configured to {UpdateInterval}ms", _updateIntervalMs);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposal.MarkDisposed() && _timer != null)
        {
            await _timer.DisposeAsync().ConfigureAwait(false);
        }
        GC.SuppressFinalize(this);
    }

    public BandwidthChannel GetChannel(string name)
    {
        return _channels.GetOrAdd(name, _ => new BandwidthChannel(_timeProvider));
    }

    public (int DownloadLimit, int UploadLimit) GetTorrentLimits(ITorrent torrent)
    {
        string hash = torrent.Hash.ToHexStringUpper();
        return (
            GetChannel($"{hash}_DL").GetLimit(),
            GetChannel($"{hash}_UL").GetLimit()
        );
    }

    public (int ReadLimit, int WriteLimit) GetTorrentDiskLimits(ITorrent torrent)
    {
        string hash = torrent.Hash.ToHexStringUpper();
        return (
            GetChannel($"{hash}_DR").GetLimit(),
            GetChannel($"{hash}_DW").GetLimit()
        );
    }

    public Task<int> RequestBandwidthAsync(IBandwidthUser user, int amount, int priority, string[] channelNames, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<int>(ct);
        }

        var channels = channelNames.Select(GetChannel).ToArray();

        // LOCK-FREE FAST PATH: Check without lock first
        // If user has no pending requests and all channels have quota, we can skip the lock entirely
        // This dramatically reduces contention under high load (100+ peers)
        if (!_usersWithPendingRequests.ContainsKey(user))
        {
            bool fastPath = true;
            foreach (var ch in channels)
            {
                if (ch.AvailableQuota < amount)
                {
                    fastPath = false;
                    break;
                }
            }

            if (fastPath)
            {
                // Use quota (lock-free Interlocked operations in BandwidthChannel)
                foreach (var ch in channels)
                {
                    ch.UseQuota(amount);
                }
                return Task.FromResult(amount);
            }
        }

        // Slow path - need to queue (requires lock for fairness structures)
        lock (_lock)
        {
            // Double-check: user might have been cleared while we waited for lock
            bool hasQueued = _pendingRequests.ContainsKey(user) && _pendingRequests[user].Count > 0;

            if (!hasQueued)
            {
                // Retry fast path check under lock (quota might have replenished)
                bool fastPath = true;
                foreach (var ch in channels)
                {
                    if (ch.AvailableQuota < amount)
                    {
                        fastPath = false;
                        break;
                    }
                }

                if (fastPath)
                {
                    foreach (var ch in channels)
                    {
                        ch.UseQuota(amount);
                    }
                    return Task.FromResult(amount);
                }
            }

            // Queue the request
            if (!_pendingRequests.ContainsKey(user))
            {
                _pendingRequests[user] = new Queue<BandwidthRequest>();
            }

            // Mark user as having pending requests (lock-free visibility)
            _usersWithPendingRequests.TryAdd(user, 0);

            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var request = new BandwidthRequest
            {
                User = user,
                Amount = amount,
                Priority = priority,
                Channels = channels,
                Tcs = tcs
            };

            if (ct.CanBeCanceled)
            {
                ct.Register(() =>
                {
                    lock (_lock)
                    {
                        if (_pendingRequests.TryGetValue(user, out var queue))
                        {
                            // Note: This is O(N) but queue should be very small per user
                            // Use a more efficient way if needed, but for now this is safe
                            var list = queue.ToList();
                            if (list.Remove(request))
                            {
                                if (list.Count == 0)
                                {
                                    _pendingRequests.Remove(user);
                                    _usersWithPendingRequests.TryRemove(user, out _);
                                }
                                else
                                {
                                    _pendingRequests[user] = new Queue<BandwidthRequest>(list);
                                }
                                tcs.TrySetCanceled(ct);
                            }
                        }
                    }
                });
            }

            _pendingRequests[user].Enqueue(request);

            // Add to RR queue if not already pending processing
            if (_activeUsers.Add(user))
            {
                _roundRobinQueue.Enqueue(user);
            }

            return tcs.Task;
        }
    }

    /// <summary>
    /// Returns unused bandwidth back to the specified channels.
    /// </summary>
    public void ReturnBandwidth(int amount, string[] channelNames)
    {
        if (amount <= 0)
        {
            return;
        }

        foreach (var name in channelNames)
        {
            var channel = GetChannel(name);
            channel.ReturnQuota(amount);
        }
    }

    public void RemoveTorrentChannels(ITorrent torrent)
    {
        string hash = torrent.Hash.ToHexStringUpper();
        _channels.TryRemove($"{hash}_DL", out _);
        _channels.TryRemove($"{hash}_UL", out _);
        _channels.TryRemove($"{hash}_DR", out _);
        _channels.TryRemove($"{hash}_DW", out _);
    }

    public void SetGlobalLimits(int downloadLimit, int uploadLimit)
    {
        GetChannel(GlobalDownload).SetLimit(downloadLimit);
        GetChannel(GlobalUpload).SetLimit(uploadLimit);
    }

    public void SetGlobalDiskLimits(int readLimit, int writeLimit)
    {
        GetChannel(GlobalDiskRead).SetLimit(readLimit);
        GetChannel(GlobalDiskWrite).SetLimit(writeLimit);
    }

    public void SetTorrentLimits(ITorrent torrent, int downloadLimit, int uploadLimit)
    {
        string hash = torrent.Hash.ToHexStringUpper();
        GetChannel($"{hash}_DL").SetLimit(downloadLimit);
        GetChannel($"{hash}_UL").SetLimit(uploadLimit);
    }

    public void SetTorrentDiskLimits(ITorrent torrent, int readLimit, int writeLimit)
    {
        string hash = torrent.Hash.ToHexStringUpper();
        GetChannel($"{hash}_DR").SetLimit(readLimit);
        GetChannel($"{hash}_DW").SetLimit(writeLimit);
    }

    public void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
        {
            _timer = _timeProvider.CreateTimer(Update, null, TimeSpan.FromMilliseconds(_updateIntervalMs), TimeSpan.FromMilliseconds(_updateIntervalMs));
        }
    }

    internal void Update(object? state)
    {
        long now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        int dt = (int)(now - _lastTick);
        if (dt <= 0)
        {
            return;
        }

        _lastTick = now;

        // Update quotas
        foreach (var ch in _channels.Values)
        {
            ch.UpdateQuota(dt);
        }

        lock (_lock)
        {
            int initialQueueCount = _roundRobinQueue.Count;
            int cycles = 0;
            int grantsInCycle = 0;

            // Process in rounds until no bandwidth left or no requests pending
            while (_roundRobinQueue.Count > 0)
            {
                var user = _roundRobinQueue.Dequeue();
                _activeUsers.Remove(user); // Temporarily remove, will re-add if re-queued

                if (!_pendingRequests.TryGetValue(user, out var queue) || queue.Count == 0)
                {
                    // No requests for this user, drop them
                    continue;
                }

                var req = queue.Peek();

                // Check satisfaction
                int grant = req.Amount;
                foreach (var ch in req.Channels)
                {
                    if (ch.AvailableQuota < grant)
                    {
                        grant = ch.AvailableQuota;
                    }
                }

                if (grant > 0)
                {
                    // Satisfied (fully or partially)
                    foreach (var ch in req.Channels)
                    {
                        ch.UseQuota(grant);
                    }

                    req.Tcs.TrySetResult(grant);

                    queue.Dequeue(); // Remove the satisfied request
                    grantsInCycle++;
                    _totalGranted++;

                    // User still has active requests?
                    if (queue.Count > 0)
                    {
                        if (_activeUsers.Add(user))
                        {
                            _roundRobinQueue.Enqueue(user);
                        }
                    }
                    else
                    {
                        _pendingRequests.Remove(user);
                        // Clear lock-free tracking so fast path works for this user again
                        _usersWithPendingRequests.TryRemove(user, out _);
                    }
                }
                else
                {
                    // Not satisfied (0 bandwidth)
                    // Must re-enqueue user to back of line to allow others to try
                    if (_activeUsers.Add(user))
                    {
                        _roundRobinQueue.Enqueue(user);
                    }
                }

                // Safety break for single pass logic or if we are spinning
                // If we cycled through everyone and gave nothing, stop to wait for next tick
                cycles++;
                if (cycles >= initialQueueCount + 10) // +10 buffer
                {
                    if (grantsInCycle == 0)
                    {
                        break; // All blocked
                    }

                    cycles = 0;
                    grantsInCycle = 0;
                    initialQueueCount = _roundRobinQueue.Count;
                }
            }
        }

        // Log periodic status every 5 seconds
        var nowTime = _timeProvider.GetUtcNow();
        if ((nowTime - _lastStatusLog).TotalSeconds >= 5)
        {
            _lastStatusLog = nowTime;
            var dlChannel = GetChannel(GlobalDownload);
            var ulChannel = GetChannel(GlobalUpload);

            if (dlChannel.GetLimit() > 0 || ulChannel.GetLimit() > 0 || _activeUsers.Count > 0)
            {
                _logger.LogTrace("Bandwidth status: active_users={ActiveUsers}, pending_users={PendingUsers}, granted={Granted}, DL quota={DLQuota}/{DLLimit}, UL quota={ULQuota}/{ULLimit}",
                    _activeUsers.Count, _roundRobinQueue.Count, _totalGranted, dlChannel.AvailableQuota, dlChannel.GetLimit(), ulChannel.AvailableQuota, ulChannel.GetLimit());
            }
            _totalGranted = 0;
        }
    }

    private sealed class BandwidthRequest
    {
        public int Amount { get; set; }
        public required BandwidthChannel[] Channels { get; set; }
        public int Priority { get; set; }
        public required TaskCompletionSource<int> Tcs { get; set; }
        public required IBandwidthUser User { get; set; }
    }
}
