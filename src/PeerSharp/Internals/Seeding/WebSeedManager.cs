using Microsoft.Extensions.Logging;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Network;
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
    private const int MaxConcurrentDownloads = 2;
    private const int MaxRetries = 3;
    private const int RetryDelayMs = 5000;

    // Configuration
    private const int WorkerIntervalMs = 1000;

    private readonly IHttpClientFactory _httpClientFactory = new HttpClientFactory();
    private readonly Lock _lock = new();
    private readonly ILogger<WebSeedManager> _logger = TorrentLoggerFactory.CreateLogger<WebSeedManager>();
    private readonly List<WebSeedSource> _sources = [];
    private readonly TimeProvider _timeProvider;
    private readonly Torrent _torrent;
    private CancellationTokenSource _cts = new();
    private AtomicDisposal _disposal = new();
    private IHttpClient? _testClient;
    private Task? _workerTask;

    public WebSeedManager(Torrent torrent, IEnumerable<string> urls, TimeProvider timeProvider)
    {
        _torrent = torrent;
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
            int available = _sources.Count(s => s.IsAvailable(_timeProvider));
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
                await _workerTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
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

            var data = await DownloadFileRangeAsync(fileUrl, fileReadStart, bytesToRead, ct).ConfigureAwait(false);
            if (data == null || data.Length != bytesToRead)
            {
                _logger.LogWarning("Failed to download file portion: {FilePath}", file.Path);
                return null!;
            }

            data.CopyTo(result.AsSpan(resultOffset));
            resultOffset += bytesToRead;
            currentOffset += bytesToRead;
            remaining -= bytesToRead;
        }

        return resultOffset == pieceLength ? result : null!;
    }

    internal async Task<byte[]> DownloadSingleFilePieceAsync(WebSeedSource source, long offset, int length, CancellationToken ct)
    {
        // For single-file torrents, the URL points directly to the file
        var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
        request.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);

        var client = GetClient();
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        else if (response.StatusCode == HttpStatusCode.OK)
        {
            // Server ignored the Range header and sent the whole file
            _logger.LogDebug("Web seed {Url} doesn't support range requests; slicing full response", source.Url);
            var content = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return SliceFullContentResponse(content, offset, length)!;
        }
        else if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            _logger.LogWarning("Range not satisfiable for {Url}", source.Url);
            return null!;
        }

        _logger.LogWarning("Unexpected status {StatusCode} from {Url}", response.StatusCode, source.Url);
        return null!;
    }

    internal List<int> GetNeededPieces()
    {
        var result = new List<int>();
        var selection = _torrent.GetFileSelectionSnapshot();
        var fileTransfer = _torrent.FileTransferInternal;

        for (int i = 0; i < _torrent.Pieces.Count; i++)
        {
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
            if (result.Count >= 10)
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



    private async Task<byte[]?> DownloadFileRangeAsync(string url, long offset, int length, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);

        var client = GetClient();
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        else if (response.StatusCode == HttpStatusCode.OK)
        {
            return SliceFullContentResponse(
                await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false),
                offset,
                length);
        }

        return null;
    }

    /// <summary>
    /// A server that ignores the Range header replies 200 with the whole file.
    /// Like libtorrent, accept that and slice the requested range out of the body.
    /// </summary>
    private static byte[]? SliceFullContentResponse(byte[] content, long offset, int length)
    {
        if (offset < 0 || offset > content.LongLength - length)
        {
            return null;
        }

        return content.AsSpan((int)offset, length).ToArray();
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
                _logger.LogWarning("Failed to download piece {PieceIndex}: invalid response size", pieceIndex);
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

    private WebSeedSource? GetAvailableSource()
    {
        lock (_lock)
        {
            foreach (var source in _sources)
            {
                if (source.IsAvailable(_timeProvider))
                {
                    return source;
                }
            }
        }
        return null;
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
            return new DefaultHttpClient(_httpClientFactory.CreateClient(directSettings, false));
        }

        return new DefaultHttpClient(_httpClientFactory.CreateClient(settings, false));
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
        // Wait for initial file check to complete before starting downloads
        await Task.Delay(TimeSpan.FromMilliseconds(3000), _timeProvider, ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Check if download is complete or we have no pieces to download
                if (_torrent.Finished || _torrent.SelectionFinished)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(WorkerIntervalMs * 5), _timeProvider, ct).ConfigureAwait(false);
                    continue;
                }

                // Find pieces that need downloading
                var neededPieces = GetNeededPieces();
                if (neededPieces.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(WorkerIntervalMs), _timeProvider, ct).ConfigureAwait(false);
                    continue;
                }

                // Try to download from available sources
                var tasks = new List<Task>();
                foreach (var piece in neededPieces)
                {
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    var source = GetAvailableSource();
                    if (source == null)
                    {
                        break;
                    }

                    lock (_lock)
                    {
                        source.IsActive = true;
                        source.ActiveDownloads++;
                    }

                    var task = DownloadPieceAsync(source, piece, ct)
                        .ContinueWith(t =>
                        {
                            lock (_lock)
                            {
                                source.ActiveDownloads--;
                                if (source.ActiveDownloads <= 0)
                                {
                                    source.IsActive = false;
                                }
                            }
                        }, TaskScheduler.Default);

                    tasks.Add(task);

                    if (tasks.Count >= MaxConcurrentDownloads)
                    {
                        break;
                    }
                }

                if (tasks.Count > 0)
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(WorkerIntervalMs), _timeProvider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WebSeed worker error");
                await Task.Delay(TimeSpan.FromMilliseconds(WorkerIntervalMs * 2), _timeProvider, ct).ConfigureAwait(false);
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
            IsDirectory = url.EndsWith("/", StringComparison.Ordinal);
            Url = url.TrimEnd('/');
            IsMultiFile = isMultiFile;
        }

        public int ActiveDownloads { get; set; }
        public int FailureCount { get; set; }
        public bool IsActive { get; set; }
        public bool IsDirectory { get; }
        public bool IsMultiFile { get; }
        public DateTimeOffset LastFailure { get; set; }
        public DateTimeOffset LastSuccess { get; set; }
        public string Url { get; }

        public bool IsAvailable(TimeProvider timeProvider)
        {
            return !IsActive &&
            (FailureCount < MaxRetries ||
             timeProvider.GetUtcNow() - LastFailure > TimeSpan.FromSeconds(RetryDelayMs * FailureCount));
        }
    }
}
