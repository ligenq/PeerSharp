using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Network;
using System.Buffers;
using System.Net;
using System.Net.Http.Headers;

namespace PeerSharp.Internals.Seeding;

/// <summary>
/// BEP 19: Manages HTTP/FTP web seed downloads for a torrent.
/// Web seeds allow downloading torrent data from regular HTTP/FTP servers,
/// providing an alternative to peer-to-peer downloads.
/// </summary>
internal sealed class WebSeedManager : IAsyncDisposable
{
    private const int MaxRetries = 3;
    private const int RetryDelayMs = 5000;
    private const int StreamBufferSize = 8192;

    // Configuration
    private const int WorkerIntervalMs = 1000;


    private readonly Lock _lock = new();
    private readonly ILogger<WebSeedManager> _logger;
    private readonly List<WebSeedSource> _sources = [];
    private readonly TimeProvider _timeProvider;
    private readonly Torrent _torrent;
    private CancellationTokenSource _cts = new();
    private AtomicDisposal _disposal = new();
    private IHttpClient? _testClient;
    private Task? _workerTask;

    public WebSeedManager(Torrent torrent, IEnumerable<string> urls, TimeProvider timeProvider)
        : this(torrent, urls, timeProvider, NullLogger<WebSeedManager>.Instance)
    {
    }

    internal WebSeedManager(Torrent torrent, IEnumerable<string> urls, TimeProvider timeProvider, ILogger<WebSeedManager> logger)
    {
        _torrent = torrent;
        _logger = logger;
        _timeProvider = timeProvider;
        bool isMultiFile = torrent.InfoFile.Info.Files.Count > 1;

        foreach (var url in urls)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                (uri.Scheme == "http" || uri.Scheme == "https" || uri.Scheme == "ftp"))
            {
                _sources.Add(new WebSeedSource(url, isMultiFile));
                _logger.LogInformation("Added web seed: {Url}", url);
            }
            else
            {
                _logger.LogWarning("Skipping invalid web seed URL: {Url}", url);
            }
        }
    }

    /// <summary>
    /// Adds a source, or returns false if the URL is unusable or already present.
    /// </summary>
    public bool AddSource(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https" && uri.Scheme != "ftp"))
        {
            return false;
        }

        bool isMultiFile = _torrent.InfoFile.Info.Files.Count > 1;

        lock (_lock)
        {
            if (_sources.Any(source => Matches(source, url)))
            {
                return false;
            }

            _sources.Add(new WebSeedSource(url, isMultiFile));
        }

        _logger.LogInformation("Added web seed: {Url}", url);
        return true;
    }

    /// <summary>
    /// Removes a source. Downloads already in flight against it are left to finish.
    /// </summary>
    public bool RemoveSource(string url)
    {
        lock (_lock)
        {
            int index = _sources.FindIndex(source => Matches(source, url));
            if (index < 0)
            {
                return false;
            }

            _sources.RemoveAt(index);
        }

        _logger.LogInformation("Removed web seed: {Url}", url);
        return true;
    }

    private static bool Matches(WebSeedSource source, string url)
        => source.IsDirectory == url.EndsWith('/')
            && source.Url.Equals(url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The URLs currently in use.</summary>
    public IReadOnlyList<string> GetSourceUrls()
    {
        lock (_lock)
        {
            return [.. _sources.Select(source => source.IsDirectory ? source.Url + "/" : source.Url)];
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposal.MarkDisposed())
        {
            await StopAsync().ConfigureAwait(false);
            _cts.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Gets statistics about web seed downloads.
    /// </summary>
    public (int TotalSources, int AvailableSources, int ActiveDownloads) GetStats()
    {
        lock (_lock)
        {
            int perSourceLimit = Math.Clamp(_torrent.Settings.Transfer.WebSeedMaxConnectionsPerSource, 1,
                Math.Clamp(_torrent.Settings.Transfer.WebSeedMaxConnections, 1, 64));
            int available = _sources.Count(s => s.IsAvailable(_timeProvider, perSourceLimit));
            int active = _sources.Sum(s => s.ActiveDownloads);
            return (_sources.Count, available, active);
        }
    }

    public void Start()
    {
        if (_sources.Count == 0)
        {
            _logger.LogInformation("No valid web seeds configured, WebSeedManager not starting");
            return;
        }

        if (_cts.IsCancellationRequested)
        {
            _cts.Dispose();
            _cts = new CancellationTokenSource();
        }

        _workerTask = WorkerLoopAsync(_cts.Token);
        _logger.LogInformation("WebSeedManager started with {Count} sources", _sources.Count);
    }

    public async Task StopAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_workerTask != null)
        {
            try
            {
                await _workerTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Expected during cancellation
            }
        }
    }

    internal async Task<byte[]> DownloadMultiFilePieceAsync(WebSeedSource source, int pieceIndex, long pieceOffset, int pieceLength, CancellationToken ct)
    {
        // For multi-file torrents, we need to map piece bytes to files
        // and make separate requests for each file portion
        var result = new byte[pieceLength];
        int resultOffset = 0;

        var files = _torrent.InfoFile.Info.Files;
        long currentOffset = pieceOffset;
        int remaining = pieceLength;

        foreach (var file in files)
        {
            if (remaining <= 0)
            {
                break;
            }

            long fileEnd = file.Offset + file.Size;

            // Skip files that end before our piece starts
            if (fileEnd <= currentOffset)
            {
                continue;
            }

            // Skip files that start after our piece ends
            if (file.Offset >= pieceOffset + pieceLength)
            {
                break;
            }

            // Calculate the portion of this file we need
            long fileReadStart = Math.Max(0, currentOffset - file.Offset);
            long fileReadEnd = Math.Min(file.Size, currentOffset + remaining - file.Offset);
            int bytesToRead = (int)(fileReadEnd - fileReadStart);

            if (bytesToRead <= 0)
            {
                continue;
            }

            if (file.IsPadding)
            {
                Array.Clear(result, resultOffset, bytesToRead);
                resultOffset += bytesToRead;
                currentOffset += bytesToRead;
                remaining -= bytesToRead;
                continue;
            }

            string fileUrl = source.IsDirectory
                ? BuildFileUrl(source.Url, _torrent.InfoFile.Info.Name, file.Path)
                : BuildFileUrl(source.Url, file.Path);

            // Already known absent from this source, so the piece cannot come from here and the
            // request would only earn another 404.
            if (source.MissingFiles.Contains(file.Path))
            {
                return null!;
            }

            var (data, status) = await DownloadFileRangeAsync(fileUrl, fileReadStart, bytesToRead, ct).ConfigureAwait(false);
            if (data == null || data.Length != bytesToRead)
            {
                if (data == null && MeansTheResourceIsNotThere(status))
                {
                    RecordFileIsNotThere(source, file.Path, status);
                }
                else
                {
                    _logger.LogWarning("Failed to download file portion: {FilePath}", file.Path);
                }

                return null!;
            }

            data.CopyTo(result.AsSpan(resultOffset));
            resultOffset += bytesToRead;
            currentOffset += bytesToRead;
            remaining -= bytesToRead;
        }

        return resultOffset == pieceLength ? result : null!;
    }

    internal async Task<byte[]?> DownloadSingleFilePieceAsync(WebSeedSource source, long offset, int length, CancellationToken ct)
    {
        // For single-file torrents, the URL points directly to the file
        var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
        request.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);

        var client = GetClient();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            return await ReadExactContentAsync(response.Content, length, ct).ConfigureAwait(false);
        }
        else if (response.StatusCode == HttpStatusCode.OK)
        {
            // Server ignored the Range header and sent the whole file
            _logger.LogDebug("Web seed {Url} doesn't support range requests; slicing full response", source.Url);
            return await ReadRangeFromFullContentAsync(response.Content, offset, length, ct).ConfigureAwait(false);
        }
        else if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            _logger.LogWarning("Range not satisfiable for {Url}", source.Url);
            return null!;
        }

        if (MeansTheResourceIsNotThere(response.StatusCode))
        {
            RecordFileIsNotThere(source, filePath: null, response.StatusCode);
            return null!;
        }

        _logger.LogWarning("Unexpected status {StatusCode} from {Url}", response.StatusCode, source.Url);
        return null!;
    }

    internal List<int> GetNeededPieces(int maxPieces = 10, ICollection<int>? inFlightPieces = null)
    {
        var result = new List<int>();
        var selection = _torrent.GetFileSelectionSnapshot();
        var fileTransfer = _torrent.FileTransferInternal;

        for (int i = 0; i < _torrent.Pieces.Count; i++)
        {
            if (inFlightPieces?.Contains(i) == true) continue;
            // Skip if we already have this piece
            if (_torrent.Pieces.HasPiece(i))
            {
                continue;
            }

            // Skip if piece is not needed based on file selection
            if (!_torrent.InfoFile.Info.IsPieceNeeded(i, selection))
            {
                continue;
            }

            // Skip if piece is already being downloaded by peers
            if (fileTransfer?.IsPieceActive(i) == true)
            {
                continue;
            }

            result.Add(i);

            // Limit the number of pieces to process per iteration
            if (result.Count >= maxPieces)
            {
                break;
            }
        }

        return result;
    }

    internal void SetTestClient(IHttpClient client)
    {
        _testClient = client;
    }



    private async Task<(byte[]? Data, HttpStatusCode Status)> DownloadFileRangeAsync(
        string url, long offset, int length, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);

        var client = GetClient();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            return (await ReadExactContentAsync(response.Content, length, ct).ConfigureAwait(false), response.StatusCode);
        }
        else if (response.StatusCode == HttpStatusCode.OK)
        {
            return (await ReadRangeFromFullContentAsync(response.Content, offset, length, ct).ConfigureAwait(false), response.StatusCode);
        }

        // The status is carried back rather than folded into a null so the caller can tell a file
        // this source does not have from one it could not serve this time.
        return (null, response.StatusCode);
    }

    private static async Task<byte[]?> ReadExactContentAsync(HttpContent content, int length, CancellationToken ct)
    {
        if (length < 0)
        {
            return null;
        }

        if (content.Headers.ContentLength is long contentLength)
        {
            if (contentLength > length)
            {
                return null;
            }

            if (contentLength == length)
            {
                return await content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }
        }

        var result = new byte[length];
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        int total = 0;
        while (total < length)
        {
            int read = await stream.ReadAsync(result.AsMemory(total, length - total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }
            total += read;
        }

        byte[] extra = ArrayPool<byte>.Shared.Rent(1);
        try
        {
            int read = await stream.ReadAsync(extra.AsMemory(0, 1), ct).ConfigureAwait(false);
            return read == 0 ? result : null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(extra);
        }
    }

    /// <summary>
    /// A server that ignores the Range header replies 200 with the whole file.
    /// Like libtorrent, accept that and stream-slice the requested range without
    /// buffering the full response.
    /// </summary>
    private static async Task<byte[]?> ReadRangeFromFullContentAsync(HttpContent content, long offset, int length, CancellationToken ct)
    {
        if (offset < 0 || length < 0)
        {
            return null;
        }

        if (content.Headers.ContentLength is long contentLength && offset > contentLength - length)
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        byte[] scratch = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
        byte[] result = new byte[length];
        try
        {
            long skipped = 0;
            while (skipped < offset)
            {
                int toRead = (int)Math.Min(scratch.Length, offset - skipped);
                int read = await stream.ReadAsync(scratch.AsMemory(0, toRead), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    return null;
                }
                skipped += read;
            }

            int total = 0;
            while (total < length)
            {
                int read = await stream.ReadAsync(result.AsMemory(total, length - total), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    return null;
                }
                total += read;
            }

            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    private static string BuildFileUrl(string baseUrl, params string[] paths)
    {
        var segments = paths
            .SelectMany(path => path
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries));

        return $"{baseUrl}/{string.Join("/", segments.Select(Uri.EscapeDataString))}";
    }

    private async Task DownloadPieceAsync(WebSeedSource source, int pieceIndex, CancellationToken ct)
    {
        try
        {
            long pieceSize = _torrent.InfoFile.Info.PieceSize;
            long pieceStart = pieceIndex * pieceSize;
            long pieceEnd = pieceStart + pieceSize;

            // Handle last piece being smaller
            if (pieceEnd > _torrent.InfoFile.Info.FullSize)
            {
                pieceEnd = _torrent.InfoFile.Info.FullSize;
            }

            long actualPieceSize = pieceEnd - pieceStart;

            _logger.LogDebug("Downloading piece {PieceIndex} ({Size} bytes) from {Url}", pieceIndex, actualPieceSize, source.Url);

            byte[]? data;
            if (source.IsMultiFile)
            {
                data = await DownloadMultiFilePieceAsync(source, pieceIndex, pieceStart, (int)actualPieceSize, ct).ConfigureAwait(false);
            }
            else
            {
                data = await DownloadSingleFilePieceAsync(source, pieceStart, (int)actualPieceSize, ct).ConfigureAwait(false);
            }

            if (data == null || data.Length != actualPieceSize)
            {
                // A source that has just been retired said what was wrong and said it at a level
                // that suits it. Repeating it here as a size problem describes the wrong fault, and
                // for a seed that has none of the torrent it would be the second warning per piece
                // for as long as pieces were still being tried.
                if (!source.IsRetired)
                {
                    _logger.LogWarning("Failed to download piece {PieceIndex}: invalid response size", pieceIndex);
                }

                RecordFailure(source);
                return;
            }

            // Feed blocks to FileTransfer
            const int blockSize = ProtocolConstants.BlockSize;
            int offset = 0;

            while (offset < data.Length)
            {
                int blockLen = Math.Min(blockSize, data.Length - offset);
                var block = new Block(pieceIndex, offset, blockLen);
                data.AsSpan(offset, blockLen).CopyTo(block.Buffer);

                // Use FileTransfer to process the block
                await _torrent.FileTransferInternal.WebSeedBlockReceivedAsync(block).ConfigureAwait(false);

                offset += blockLen;
            }

            RecordSuccess(source);
            _logger.LogDebug("Successfully downloaded piece {PieceIndex} from {Url}", pieceIndex, source.Url);
        }
        catch (OperationCanceledException)
        {
            // Shutdown - don't count as failure
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "HTTP error downloading piece {PieceIndex} from {Url}", pieceIndex, source.Url);
            RecordFailure(source);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading piece {PieceIndex} from {Url}", pieceIndex, source.Url);
            RecordFailure(source);
        }
    }

    private WebSeedSource? GetAvailableSource(int perSourceLimit)
    {
        lock (_lock)
        {
            var source = _sources.Where(source => source.IsAvailable(_timeProvider, perSourceLimit))
                .MinBy(source => source.ActiveDownloads);
            if (source != null)
            {
                // Reserved here, under the same lock that chose it, so two fills cannot both take the
                // last slot on a source. Released in DownloadAndReleaseAsync's finally.
                source.ActiveDownloads++;
            }

            return source;
        }
    }

    private IHttpClient GetClient()
    {
        if (_testClient != null)
        {
            return _testClient;
        }

        var settings = _torrent.Settings.Proxy;
        if (!settings.ProxyPeers)
        {
            // Direct connection if proxying peers is disabled
            var directSettings = new ProxySettings { Type = ProxyType.None };
            return new DefaultHttpClient(_torrent.Services.HttpClientFactory.CreateClient(
                directSettings,
                false,
                _torrent.Settings.Connection.BindAddress,
                maxConnectionsPerServer: PerServerConnectionLimit()));
        }

        return new DefaultHttpClient(_torrent.Services.HttpClientFactory.CreateClient(
            settings,
            false,
            _torrent.Settings.Connection.BindAddress,
            maxConnectionsPerServer: PerServerConnectionLimit()));
    }

    /// <summary>
    /// How many HTTP connections one origin may hold open. Derived from the configured per-source
    /// concurrency, because the handler's own cap silently queues anything above it: raising
    /// WebSeedMaxConnectionsPerSource past a fixed limit would buy no extra parallelism at all.
    /// </summary>
    private int PerServerConnectionLimit()
    {
        int limit = Math.Clamp(_torrent.Settings.Transfer.WebSeedMaxConnections, 1, 64);
        int perSource = Math.Clamp(_torrent.Settings.Transfer.WebSeedMaxConnectionsPerSource, 1, limit);
        return Math.Max(IHttpClientFactory.DefaultMaxConnectionsPerServer, perSource);
    }

    /// <summary>
    /// Whether a status says the resource is not at this URL, as opposed to the server being
    /// briefly unable to serve it.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow. A 404 or a 410 is HTTP saying there is nothing here, and no number of
    /// retries changes that; everything else - a 500, a 503, a timeout - is the server having a bad
    /// minute and keeps the ordinary backoff. 403 and 401 are left out on purpose: a web seed
    /// behind an authenticating proxy that is misconfigured for a moment would be written off for
    /// the whole torrent, and the cost of retrying those is only the backoff.
    /// </remarks>
    private static bool MeansTheResourceIsNotThere(HttpStatusCode status) =>
        status is HttpStatusCode.NotFound or HttpStatusCode.Gone;

    /// <summary>
    /// Records that a source does not hold one of the torrent's files, and retires the source once
    /// it holds none of them.
    /// </summary>
    /// <remarks>
    /// libtorrent does the same thing per file rather than per server: a non-OK status clears that
    /// file's bit in the web seed's <c>have_files</c>, and only a seed left holding nothing the
    /// torrent wants stops being asked. A multi-file torrent whose seed is missing one file can
    /// still be served the rest by it, which dropping the whole source would throw away.
    /// </remarks>
    private void RecordFileIsNotThere(WebSeedSource source, string? filePath, HttpStatusCode status)
    {
        bool retiredNow = false;

        lock (_lock)
        {
            if (source.IsRetired)
            {
                return;
            }

            if (filePath is null)
            {
                source.IsRetired = true;
            }
            else
            {
                source.MissingFiles.Add(filePath);
                source.IsRetired = _torrent.InfoFile.Info.Files
                    .Where(file => !file.IsPadding)
                    .All(file => source.MissingFiles.Contains(file.Path));
            }

            retiredNow = source.IsRetired;
        }

        if (retiredNow)
        {
            _logger.LogInformation(
                "Web seed {Url} does not have this torrent ({StatusCode}); it will not be asked again",
                source.Url,
                status);
        }
        else
        {
            _logger.LogDebug(
                "Web seed {Url} does not have {FilePath} ({StatusCode}); the rest of the torrent is still offered",
                source.Url,
                filePath,
                status);
        }
    }

    private void RecordFailure(WebSeedSource source)
    {
        lock (_lock)
        {
            source.FailureCount++;
            source.LastFailure = _timeProvider.GetUtcNow();
        }
    }

    private void RecordSuccess(WebSeedSource source)
    {
        lock (_lock)
        {
            source.FailureCount = 0;
            source.LastSuccess = _timeProvider.GetUtcNow();
        }
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        var active = new Dictionary<int, Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (int piece in active.Where(pair => pair.Value.IsCompleted).Select(pair => pair.Key).ToArray())
                {
                    await active[piece].ConfigureAwait(false);
                    active.Remove(piece);
                }

                int limit = Math.Clamp(_torrent.Settings.Transfer.WebSeedMaxConnections, 1, 64);
                int sourceLimit = Math.Clamp(_torrent.Settings.Transfer.WebSeedMaxConnectionsPerSource, 1, limit);
                if (CanStartMoreDownloads() && active.Count < limit)
                {
                    foreach (int piece in GetNeededPieces(limit - active.Count, active.Keys))
                    {
                        ct.ThrowIfCancellationRequested();
                        var source = GetAvailableSource(sourceLimit);
                        if (source == null) break;
                        active.Add(piece, DownloadAndReleaseAsync(source, piece, ct));
                    }
                }

                // Completion replenishes a slot immediately. The timer is only for idle work,
                // retries and live settings/source changes while all HTTP requests are blocked.
                using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Task poll = Task.Delay(TimeSpan.FromMilliseconds(WorkerIntervalMs), _timeProvider, pollCts.Token);
                await Task.WhenAny(active.Values.Append(poll)).ConfigureAwait(false);
                await pollCts.CancelAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown still drains the requests below so source reservations are released.
        }
        finally
        {
            await Task.WhenAll(active.Values).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether there is any point starting another piece. The hash check is in the list because
    /// <see cref="GetNeededPieces"/> reads <c>HasPiece</c>, which reports false for every piece until
    /// the check has written its results - so dialling during a check re-fetches over HTTP whatever
    /// a resumed torrent already has on disk.
    /// </summary>
    private bool CanStartMoreDownloads()
    {
        return !_torrent.Finished
            && !_torrent.SelectionFinished
            && _torrent.FilesInternal?.Checking != true;
    }

    private async Task DownloadAndReleaseAsync(WebSeedSource source, int piece, CancellationToken ct)
    {
        try { await DownloadPieceAsync(source, piece, ct).ConfigureAwait(false); }
        finally
        {
            lock (_lock)
            {
                source.ActiveDownloads--;
            }
        }
    }

    /// <summary>
    /// Tracks state for each web seed URL.
    /// </summary>
    internal sealed class WebSeedSource
    {
        public WebSeedSource(string url, bool isMultiFile)
        {
            IsDirectory = url.EndsWith('/');
            Url = url.TrimEnd('/');
            IsMultiFile = isMultiFile;
        }

        public int ActiveDownloads { get; set; }
        public int FailureCount { get; set; }

        /// <summary>
        /// The files this source has answered for with a status that says the resource is not
        /// there. Keyed by the torrent's own path for the file, and empty for a single-file
        /// torrent, where <see cref="IsRetired"/> carries the same news.
        /// </summary>
        public HashSet<string> MissingFiles { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Set once this source has nothing left that the torrent wants. Not asked again.
        /// </summary>
        public bool IsRetired { get; set; }
        public bool IsDirectory { get; }
        public bool IsMultiFile { get; }
        public DateTimeOffset LastFailure { get; set; }
        public DateTimeOffset LastSuccess { get; set; }
        public string Url { get; }

        public bool IsAvailable(TimeProvider timeProvider, int maxDownloads = 1)
        {
            // A source that has told us it does not hold what we want is not waiting on a backoff,
            // it is answered. Retrying it costs a request and a warning per attempt for the length
            // of the download and cannot ever succeed.
            if (IsRetired)
            {
                return false;
            }

            if (ActiveDownloads >= maxDownloads)
            {
                return false;
            }

            if (FailureCount == 0)
            {
                return true;
            }

            // Below MaxRetries the wait is one poll interval, not none. The batch-and-sleep loop this
            // replaced slept between batches, so the early retries were spaced whether the policy said
            // so or not; without that sleep, a source failing instantly would be redialled instantly.
            //
            // Past MaxRetries the wait grows with the failure count. Milliseconds, as the name says:
            // this read them as seconds, which turned the intended fifteen second wait before retrying
            // a seed that has failed its three times into four hours and ten minutes - long enough
            // that, for any torrent that finishes in an afternoon, a web seed which failed once was
            // never tried again.
            double waitMs = FailureCount < MaxRetries
                ? WorkerIntervalMs
                : (double)RetryDelayMs * FailureCount;
            return timeProvider.GetUtcNow() - LastFailure > TimeSpan.FromMilliseconds(waitMs);
        }
    }
}
