using System.Net;

namespace PeerSharp.Internals.Peers;

/// <summary>
/// Provides adaptive timeout calculations based on observed network conditions.
/// Uses exponential moving average (EMA) with RTT variance tracking similar to TCP.
/// </summary>
internal class AdaptiveTimeout
{
    private const int EndpointStatsExpiryMinutes = 30;
    private const int MaxEndpointEntries = 1000;
    private const int MaxRecentSamples = 100;
    private readonly double _alpha;

    // Smoothing factor for SRTT (0.125 = 1/8 per RFC 6298)
    private readonly double _beta;

    // Per-endpoint statistics for more granular adaptation
    private readonly Dictionary<string, EndpointStats> _endpointStats = new();

    private readonly int _initialTimeoutMs;

    // Smoothing factor for RTTVAR (0.25 = 1/4 per RFC 6298)
    private readonly double _k;

    private readonly Lock _lock = new();

    private readonly int _maxTimeoutMs;

    // Configuration
    private readonly int _minTimeoutMs;

    // Multiplier for variance (4 per RFC 6298)

    // Prevent unbounded growth
    // Sliding window for recent samples
    private readonly Queue<TimedSample> _recentSamples = new();

    private readonly TimeProvider _timeProvider;

    private bool _initialized;

    private double _rttVariance;

    private int _sampleCount;

    // Global statistics (across all peers)
    private double _smoothedRtt;

    /// <summary>
    /// Creates an adaptive timeout manager with default settings.
    /// </summary>
    public AdaptiveTimeout(TimeProvider timeProvider)
        : this(
            minTimeoutMs: 1000,      // 1 second minimum
            maxTimeoutMs: 30000,     // 30 seconds maximum
            initialTimeoutMs: 10000, // 10 seconds initial (matches ProtocolConstants)
            timeProvider: timeProvider)
    {
    }

    /// <summary>
    /// Creates an adaptive timeout manager with custom settings.
    /// </summary>
    public AdaptiveTimeout(int minTimeoutMs, int maxTimeoutMs, int initialTimeoutMs, TimeProvider timeProvider)
    {
        _minTimeoutMs = minTimeoutMs;
        _maxTimeoutMs = maxTimeoutMs;
        _initialTimeoutMs = initialTimeoutMs;
        _timeProvider = timeProvider;

        // RFC 6298 recommended values
        _alpha = 0.125; // 1/8
        _beta = 0.25;   // 1/4
        _k = 4.0;

        _smoothedRtt = initialTimeoutMs; // Initial estimate
        _rttVariance = 0;
    }

    /// <summary>
    /// Gets the current recommended timeout in milliseconds.
    /// </summary>
    public int CurrentTimeoutMs
    {
        get
        {
            lock (_lock)
            {
                if (!_initialized || _sampleCount < 3)
                {
                    return _initialTimeoutMs;
                }

                // RTO = SRTT + K * RTTVAR (RFC 6298)
                double timeout = _smoothedRtt + (_k * _rttVariance);
                return ClampTimeout((int)timeout);
            }
        }
    }

    /// <summary>
    /// Gets the current RTT variance estimate in milliseconds.
    /// </summary>
    public double RttVarianceMs
    {
        get
        {
            lock (_lock)
            {
                return _rttVariance;
            }
        }
    }

    /// <summary>
    /// Gets the number of RTT samples collected.
    /// </summary>
    public int SampleCount
    {
        get
        {
            lock (_lock)
            {
                return _sampleCount;
            }
        }
    }

    /// <summary>
    /// Gets the current smoothed RTT estimate in milliseconds.
    /// </summary>
    public double SmoothedRttMs
    {
        get
        {
            lock (_lock)
            {
                return _smoothedRtt;
            }
        }
    }

    /// <summary>
    /// Gets an aggressive timeout (for fast-fail scenarios).
    /// Uses median of recent samples.
    /// </summary>
    public int GetAggressiveTimeoutMs()
    {
        lock (_lock)
        {
            if (_recentSamples.Count < 5)
            {
                return _initialTimeoutMs / 2;
            }

            // Calculate median
            var sortedRtts = _recentSamples.Select(s => s.RttMs).OrderBy(x => x).ToList();
            int medianRtt = sortedRtts[sortedRtts.Count / 2];

            // Add small margin
            return ClampTimeout((int)(medianRtt * 2.0));
        }
    }

    /// <summary>
    /// Gets a conservative timeout (for critical operations).
    /// Uses 95th percentile of recent samples if available.
    /// </summary>
    public int GetConservativeTimeoutMs()
    {
        lock (_lock)
        {
            if (_recentSamples.Count < 10)
            {
                return _maxTimeoutMs;
            }

            // Calculate 95th percentile
            var sortedRtts = _recentSamples.Select(s => s.RttMs).OrderBy(x => x).ToList();
            int p95Index = (int)(sortedRtts.Count * 0.95);
            int p95Rtt = sortedRtts[Math.Min(p95Index, sortedRtts.Count - 1)];

            // Add safety margin
            return ClampTimeout((int)(p95Rtt * 1.5));
        }
    }

    /// <summary>
    /// Gets statistics summary for logging/debugging.
    /// </summary>
    public string GetStatsSummary()
    {
        lock (_lock)
        {
            return $"SRTT={_smoothedRtt:F0}ms, Var={_rttVariance:F0}ms, " +
                   $"Timeout={CurrentTimeoutMs}ms, Samples={_sampleCount}, " +
                   $"Endpoints={_endpointStats.Count}";
        }
    }

    /// <summary>
    /// Gets the recommended timeout for a specific endpoint.
    /// Falls back to global timeout if endpoint has insufficient data.
    /// </summary>
    public int GetTimeoutForEndpoint(IPEndPoint endpoint)
    {
        lock (_lock)
        {
            string key = GetEndpointKey(endpoint);
            if (_endpointStats.TryGetValue(key, out var stats) && stats.SampleCount >= 3)
            {
                double timeout = stats.SmoothedRtt + (_k * stats.RttVariance);
                return ClampTimeout((int)timeout);
            }

            return CurrentTimeoutMs;
        }
    }

    /// <summary>
    /// Checks if we have sufficient history for this endpoint to provide a specific timeout.
    /// </summary>
    public bool HasHistory(IPEndPoint endpoint)
    {
        lock (_lock)
        {
            string key = GetEndpointKey(endpoint);
            return _endpointStats.TryGetValue(key, out var stats) && stats.SampleCount >= 3;
        }
    }

    /// <summary>
    /// Records a successful connection/operation time.
    /// </summary>
    /// <param name="rttMs">The observed round-trip time in milliseconds.</param>
    /// <param name="endpoint">Optional endpoint for per-peer tracking.</param>
    public void RecordSuccess(int rttMs, IPEndPoint? endpoint = null)
    {
        if (rttMs <= 0)
        {
            return;
        }

        // Clamp to reasonable bounds
        rttMs = Math.Clamp(rttMs, ProtocolConstants.MinRttMs, ProtocolConstants.MaxRttMs);

        lock (_lock)
        {
            UpdateGlobalStats(rttMs);

            if (endpoint != null)
            {
                UpdateEndpointStats(endpoint, rttMs, success: true);
            }

            // Add to recent samples
            _recentSamples.Enqueue(new TimedSample(rttMs, _timeProvider.GetUtcNow()));
            while (_recentSamples.Count > MaxRecentSamples)
            {
                _recentSamples.Dequeue();
            }
        }
    }

    /// <summary>
    /// Records a connection/operation timeout (failure).
    /// </summary>
    /// <param name="endpoint">Optional endpoint for per-peer tracking.</param>
    public void RecordTimeout(IPEndPoint? endpoint = null)
    {
        lock (_lock)
        {
            // On timeout, back off the estimates (increase variance)
            // This helps prevent repeated timeouts
            // Use 1.1x instead of 1.5x to avoid rapid explosion of timeout values on dead peers
            _rttVariance = Math.Min(_rttVariance * 1.1, _maxTimeoutMs / 2.0);

            if (endpoint != null)
            {
                UpdateEndpointStats(endpoint, 0, success: false);
            }
        }
    }

    /// <summary>
    /// Resets all statistics.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _smoothedRtt = _initialTimeoutMs;
            _rttVariance = 0;
            _sampleCount = 0;
            _initialized = false;
            _endpointStats.Clear();
            _recentSamples.Clear();
        }
    }

    private static string GetEndpointKey(IPEndPoint endpoint)
    {
        // Use IP only (not port) for grouping - same host likely has similar latency
        return endpoint.Address.ToString();
    }

    private int ClampTimeout(int timeout)
    {
        return Math.Clamp(timeout, _minTimeoutMs, _maxTimeoutMs);
    }

    private void CleanupOldEndpointStats()
    {
        var cutoff = _timeProvider.GetUtcNow().AddMinutes(-EndpointStatsExpiryMinutes);
        var keysToRemove = _endpointStats
            .Where(kvp => kvp.Value.LastSeen < cutoff)
            .Select(kvp => kvp.Key)
            .Take(_endpointStats.Count / 2) // Remove at most half
            .ToList();

        foreach (var key in keysToRemove)
        {
            _endpointStats.Remove(key);
        }
    }

    private void UpdateEndpointStats(IPEndPoint endpoint, int rttMs, bool success)
    {
        string key = GetEndpointKey(endpoint);

        if (!_endpointStats.TryGetValue(key, out var stats))
        {
            // Clean up old entries if needed
            if (_endpointStats.Count >= MaxEndpointEntries)
            {
                CleanupOldEndpointStats();
            }

            stats = new EndpointStats
            {
                SmoothedRtt = _initialTimeoutMs / 2.0,
                RttVariance = _initialTimeoutMs / 4.0
            };
            _endpointStats[key] = stats;
        }

        stats.LastSeen = _timeProvider.GetUtcNow();

        if (success)
        {
            if (stats.SampleCount == 0)
            {
                stats.SmoothedRtt = rttMs;
                stats.RttVariance = rttMs / 2.0;
            }
            else
            {
                double rttDiff = Math.Abs(stats.SmoothedRtt - rttMs);
                stats.RttVariance = ((1 - _beta) * stats.RttVariance) + (_beta * rttDiff);
                stats.SmoothedRtt = ((1 - _alpha) * stats.SmoothedRtt) + (_alpha * rttMs);
            }
            stats.SampleCount++;
            stats.SuccessCount++;
        }
        else
        {
            stats.FailureCount++;
            stats.RttVariance = Math.Min(stats.RttVariance * 1.5, _maxTimeoutMs / 2.0);
        }
    }

    private void UpdateGlobalStats(int rttMs)
    {
        if (!_initialized)
        {
            // First sample: initialize per RFC 6298
            _smoothedRtt = rttMs;
            _rttVariance = rttMs / 2.0;
            _initialized = true;
        }
        else
        {
            // RFC 6298 algorithm
            double rttDiff = Math.Abs(_smoothedRtt - rttMs);
            _rttVariance = ((1 - _beta) * _rttVariance) + (_beta * rttDiff);
            _smoothedRtt = ((1 - _alpha) * _smoothedRtt) + (_alpha * rttMs);
        }

        _sampleCount++;

        // Clamp to prevent extreme values
        _smoothedRtt = Math.Clamp(_smoothedRtt, _minTimeoutMs / 2.0, ProtocolConstants.MaxSmoothedRttMs);
        _rttVariance = Math.Clamp(_rttVariance, 0, _maxTimeoutMs / 2.0);

        System.Diagnostics.Debug.WriteLine($"AdaptiveTimeout: UpdateGlobalStats - RTT: {rttMs}ms, Smoothed: {_smoothedRtt:F0}ms, Variance: {_rttVariance:F0}ms, Samples: {_sampleCount}");
    }

    private readonly record struct TimedSample(int RttMs, DateTimeOffset Timestamp);

    private sealed class EndpointStats
    {
        public int FailureCount { get; set; }
        public DateTimeOffset LastSeen { get; set; }
        public double RttVariance { get; set; }
        public int SampleCount { get; set; }
        public double SmoothedRtt { get; set; }
        public int SuccessCount { get; set; }
    }
}
