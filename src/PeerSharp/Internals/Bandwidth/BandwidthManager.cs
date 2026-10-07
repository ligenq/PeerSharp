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

        _lastTick = _timeProvider.GetTimestamp();

        _logger.LogDebug("BandwidthManager initialized with {UpdateInterval}ms update interval", _updateIntervalMs);
    }

    /// <summary>
    /// Configures the update interval. Must be called before Start().
    /// </summary>
    public void Configure(int updateIntervalMs)
    {
        lock (_lock)
        {
            _disposal.ThrowIfDisposed(this);
            if (_started == 1)
            {
                throw new InvalidOperationException("Cannot configure BandwidthManager after Start() has been called");
            }

            _updateIntervalMs = Math.Clamp(updateIntervalMs, 1, 100);
            _logger.LogDebug("BandwidthManager update interval configured to {UpdateInterval}ms", _updateIntervalMs);
        }
    }

    public async ValueTask DisposeAsync()
    {
        ITimer? timer;
        lock (_lock)
        {
            if (!_disposal.MarkDisposed()) return;
            timer = _timer;
            foreach (var request in _pendingRequests.Values.SelectMany(queue => queue))
                request.Tcs.TrySetException(new ObjectDisposedException(nameof(BandwidthManager)));
            _pendingRequests.Clear();
            _roundRobinQueue.Clear();
            _activeUsers.Clear();
        }
        if (timer != null) await timer.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    public BandwidthChannel GetChannel(string name)
    {
        return _channels.GetOrAdd(name, _ => new BandwidthChannel(_timeProvider));
    }

    internal static string GetTorrentChannelKey(ITorrent torrent)
    {
        if (torrent is Torrent owned) return owned.SessionHash.ToHexStringUpper();
        return (torrent.Hash.IsEmpty ? torrent.HashV2 : torrent.Hash).ToHexStringUpper();
    }

    public (long DownloadLimit, long UploadLimit) GetTorrentLimits(ITorrent torrent)
    {
        string hash = GetTorrentChannelKey(torrent);
        return (
            GetChannel($"{hash}_DL").GetLimit(),
            GetChannel($"{hash}_UL").GetLimit()
        );
    }

    public (long ReadLimit, long WriteLimit) GetTorrentDiskLimits(ITorrent torrent)
    {
        string hash = GetTorrentChannelKey(torrent);
        return (
            GetChannel($"{hash}_DR").GetLimit(),
            GetChannel($"{hash}_DW").GetLimit()
        );
    }

    public Task<int> RequestBandwidthAsync(IBandwidthUser user, int amount, int priority, string[] channelNames, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(channelNames);
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        if (ct.IsCancellationRequested) return Task.FromCanceled<int>(ct);
        BandwidthRequest request;
        lock (_lock)
        {
            _disposal.ThrowIfDisposed(this);
            if (amount == 0) return Task.FromResult(0);
            var channels = channelNames.Distinct().Select(GetChannel).ToArray();
            // Check and reserve as one manager operation. New callers must not spend quota
            // ahead of users already waiting, even when they belong to a different peer.
            bool competing = _pendingRequests.Values.SelectMany(queue => queue)
                .Any(waiter => waiter.Channels.Any(channel => channel.GetLimit() > 0 && channels.Contains(channel)));
            if (!competing && TryReserve(channels, amount)) return Task.FromResult(amount);
            request = new BandwidthRequest
            {
                User = user,
                Amount = amount,
                Priority = priority,
                Channels = channels,
                Tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            if (!_pendingRequests.TryGetValue(user, out var queue))
                _pendingRequests[user] = queue = new Queue<BandwidthRequest>();
            queue.Enqueue(request);
            if (_activeUsers.Add(user)) _roundRobinQueue.Enqueue(user);
        }
        // Register after enqueueing, outside the accounting lock: an already-cancelled
        // token invokes its callback inline, and unregistering may wait for that callback.
        return WaitForRequestAsync(request, ct);
    }

    private Task<int> WaitForRequestAsync(BandwidthRequest request, CancellationToken ct)
    {
        var registration = ct.Register(() =>
        {
            lock (_lock)
            {
                if (!_pendingRequests.TryGetValue(request.User, out var queue)) return;
                var remaining = queue.Where(item => !ReferenceEquals(item, request)).ToArray();
                if (remaining.Length == queue.Count) return;
                if (remaining.Length == 0)
                {
                    _pendingRequests.Remove(request.User);
                    _activeUsers.Remove(request.User);
                    var users = _roundRobinQueue.Where(user => !ReferenceEquals(user, request.User)).ToArray();
                    _roundRobinQueue.Clear();
                    foreach (var user in users) _roundRobinQueue.Enqueue(user);
                }
                else _pendingRequests[request.User] = new Queue<BandwidthRequest>(remaining);
                request.Tcs.TrySetCanceled(ct);
            }
        });
        _ = request.Tcs.Task.ContinueWith(static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
            registration, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return request.Tcs.Task;
    }

    private static bool TryReserve(BandwidthChannel[] channels, int amount)
    {
        for (int i = 0; i < channels.Length; i++)
        {
            if (channels[i].TryUseQuota(amount)) continue;
            for (int previous = 0; previous < i; previous++) channels[previous].ReturnQuota(amount);
            return false;
        }
        return true;
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

        foreach (var name in channelNames.Distinct())
        {
            if (_channels.TryGetValue(name, out var channel)) channel.ReturnQuota(amount);
        }
    }

    public void RemoveTorrentChannels(ITorrent torrent)
    {
        string hash = GetTorrentChannelKey(torrent);
        lock (_lock)
        {
            var removed = new HashSet<BandwidthChannel>();
            foreach (string suffix in new[] { "DL", "UL", "DR", "DW" })
                if (_channels.TryRemove($"{hash}_{suffix}", out var channel)) removed.Add(channel);
            foreach (var (user, queue) in _pendingRequests.ToArray())
            {
                var retained = new Queue<BandwidthRequest>();
                foreach (var request in queue)
                {
                    if (request.Channels.Any(removed.Contains)) request.Tcs.TrySetException(new ObjectDisposedException("Torrent bandwidth channels"));
                    else retained.Enqueue(request);
                }
                if (retained.Count == 0) _pendingRequests.Remove(user);
                else _pendingRequests[user] = retained;
            }
        }
    }

    public void SetGlobalLimits(long downloadLimit, long uploadLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(downloadLimit);
        ArgumentOutOfRangeException.ThrowIfNegative(uploadLimit);
        GetChannel(GlobalDownload).SetLimit(downloadLimit);
        GetChannel(GlobalUpload).SetLimit(uploadLimit);
    }

    public void SetGlobalDiskLimits(long readLimit, long writeLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(readLimit);
        ArgumentOutOfRangeException.ThrowIfNegative(writeLimit);
        GetChannel(GlobalDiskRead).SetLimit(readLimit);
        GetChannel(GlobalDiskWrite).SetLimit(writeLimit);
    }

    public void SetTorrentLimits(ITorrent torrent, long downloadLimit, long uploadLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(downloadLimit);
        ArgumentOutOfRangeException.ThrowIfNegative(uploadLimit);
        string hash = GetTorrentChannelKey(torrent);
        GetChannel($"{hash}_DL").SetLimit(downloadLimit);
        GetChannel($"{hash}_UL").SetLimit(uploadLimit);
    }

    public void SetTorrentDiskLimits(ITorrent torrent, long readLimit, long writeLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(readLimit);
        ArgumentOutOfRangeException.ThrowIfNegative(writeLimit);
        string hash = GetTorrentChannelKey(torrent);
        GetChannel($"{hash}_DR").SetLimit(readLimit);
        GetChannel($"{hash}_DW").SetLimit(writeLimit);
    }

    public void Start()
    {
        lock (_lock)
        {
            _disposal.ThrowIfDisposed(this);
            if (_started == 1) return;
            _started = 1;
            _timer = _timeProvider.CreateTimer(Update, null, TimeSpan.FromMilliseconds(_updateIntervalMs), TimeSpan.FromMilliseconds(_updateIntervalMs));
        }
    }

    /// <summary>
    /// Timer callback that replenishes quota and hands it out.
    ///
    /// <para>
    /// Wrapped because this runs on a timer thread with nothing above it: an exception escaping a timer
    /// callback is unhandled, and in most hosts that ends the process. Bandwidth accounting is not
    /// worth taking an application down for, so a failed tick is logged and the next one carries on.
    /// The disposal check keeps a callback already in flight when Dispose ran from touching torn down
    /// state.
    /// </para>
    /// </summary>
    internal void Update(object? state)
    {
        if (_disposal.IsDisposed)
        {
            return;
        }

        try
        {
            UpdateCore();
        }
        catch (ObjectDisposedException)
        {
            // Raced shutdown; the timer is on its way out.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bandwidth update tick failed; quota accounting will resume on the next tick");
        }
    }

    private void UpdateCore()
    {
        lock (_lock)
        {
            if (_disposal.IsDisposed) return;
            long now = _timeProvider.GetTimestamp();
            int dt = (int)Math.Clamp(_timeProvider.GetElapsedTime(_lastTick, now).TotalMilliseconds, 0, int.MaxValue);
            if (dt <= 0) return;
            _lastTick = now;
            foreach (var channel in _channels.Values) channel.UpdateQuota(dt);

            while (_roundRobinQueue.Count > 0)
            {
                int roundSize = _roundRobinQueue.Count;
                var grantedUsers = new List<IBandwidthUser>();
                for (int i = 0; i < roundSize; i++)
                {
                    var user = _roundRobinQueue.Dequeue();
                    _activeUsers.Remove(user);
                    if (!_pendingRequests.TryGetValue(user, out var queue) || queue.Count == 0) continue;
                    var request = queue.Peek();
                    int grant = request.Amount;
                    foreach (var channel in request.Channels) grant = Math.Min(grant, channel.AvailableQuota);
                    if (grant > 0 && TryReserve(request.Channels, grant))
                    {
                        queue.Dequeue();
                        request.Tcs.TrySetResult(grant);
                        _totalGranted++;
                        if (queue.Count == 0) _pendingRequests.Remove(user);
                        else grantedUsers.Add(user);
                    }
                    else
                    {
                        _activeUsers.Add(user);
                        _roundRobinQueue.Enqueue(user);
                    }
                }
                // Users that got quota go behind blocked users. Keeping the original order
                // after an unsuccessful pass starves other peers at low refill rates.
                foreach (var user in grantedUsers)
                    if (_activeUsers.Add(user)) _roundRobinQueue.Enqueue(user);
                if (grantedUsers.Count == 0) break;
            }
        }

        lock (_lock)
        {
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
