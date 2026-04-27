using Microsoft.Extensions.Logging;
using PeerSharp.Interfaces;
using PeerSharp.WebTorrent.Configuration;
using PeerSharp.WebTorrent.Network;
using PeerSharp.WebTorrent.Signaling;
using System.Text.Json.Nodes;

namespace PeerSharp.WebTorrent.Trackers;

internal sealed class WebTorrentTrackerManager : IAsyncDisposable
{
    private readonly List<WebTorrentTrackerClient> _clients = new();
    private readonly ITorrent _torrent;
    private readonly IPeerTransportHost _host;
    private readonly WebTorrentSessionOptions _options;
    private readonly IWebSocketConnectionFactory _socketFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly Action<WebTorrentSignalMessage, TrackerRuntime> _onSignalReceived;
    private readonly long _uploadedBaseline;
    private readonly long _downloadedBaseline;

    public WebTorrentTrackerManager(
        ITorrent torrent,
        IPeerTransportHost host,
        WebTorrentSessionOptions options,
        IWebSocketConnectionFactory socketFactory,
        ILoggerFactory loggerFactory,
        long uploadedBaseline,
        long downloadedBaseline,
        Action<WebTorrentSignalMessage, TrackerRuntime> onSignalReceived)
    {
        _torrent = torrent;
        _host = host;
        _options = options;
        _socketFactory = socketFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<WebTorrentTrackerManager>();
        _uploadedBaseline = uploadedBaseline;
        _downloadedBaseline = downloadedBaseline;
        _onSignalReceived = onSignalReceived;
    }

    public IReadOnlyList<TrackerRuntime> GetRuntimes()
    {
        lock (_clients)
        {
            return _clients.Select(c => c.Runtime).ToList();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var urls = WebTorrentTrackerUrls.Collect(_torrent, _options);
        foreach (var url in urls)
        {
            var client = new WebTorrentTrackerClient(
                url,
                _host,
                _socketFactory,
                _loggerFactory.CreateLogger<WebTorrentTrackerClient>(),
                _options.TimeProvider,
                _options.TrackerConnectTimeout,
                _options.MinimumReannounceInterval,
                _uploadedBaseline,
                _downloadedBaseline,
                signal => _onSignalReceived(signal, GetRuntimeForUrl(url)),
                ex => HandleTrackerFailure(url, ex)
            );

            lock (_clients)
            {
                _clients.Add(client);
            }

            await client.StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private TrackerRuntime GetRuntimeForUrl(string url)
    {
        lock (_clients)
        {
            return _clients.First(c => c.Runtime.Url == url).Runtime;
        }
    }

    private void HandleTrackerFailure(string url, Exception ex)
    {
        var runtime = GetRuntimeForUrl(url);
        if (TrackerConnectFailureClassifier.IsTerminal(ex))
        {
            _logger.LogInformation("Terminal failure for tracker {Url}: {Reason}", url, TrackerConnectFailureClassifier.Describe(ex));
            MarkTrackerTerminalFailure(runtime, ex);
        }
        else
        {
            _logger.LogWarning("Failure for tracker {Url}: {Reason}", url, TrackerConnectFailureClassifier.Describe(ex));
            ScheduleTrackerReconnect(runtime, ex);
        }
    }

    private static void MarkTrackerTerminalFailure(TrackerRuntime runtime, Exception ex)
    {
        lock (runtime.SyncRoot)
        {
            runtime.IsConnected = false;
            runtime.Socket = null;
            runtime.LastError = TrackerConnectFailureClassifier.Describe(ex);
            runtime.NextReconnectAt = DateTimeOffset.MinValue;
            runtime.ReconnectInProgress = false;
        }
    }

    private void ScheduleTrackerReconnect(TrackerRuntime runtime, Exception ex)
    {
        lock (runtime.SyncRoot)
        {
            runtime.Generation++;
            runtime.IsConnected = false;
            runtime.ConsecutiveFailures++;
            runtime.LastError = TrackerConnectFailureClassifier.Describe(ex);
            int seconds = Math.Min(300, (int)Math.Pow(2, Math.Min(runtime.ConsecutiveFailures, 8)));
            runtime.NextReconnectAt = _options.TimeProvider.GetUtcNow() + TimeSpan.FromSeconds(seconds);
        }
    }

    public async Task ReannounceAsync(TrackerRuntime runtime, string? @event, JsonArray? offers, CancellationToken cancellationToken)
    {
        WebTorrentTrackerClient? client;
        lock (_clients)
        {
            client = _clients.FirstOrDefault(c => c.Runtime == runtime);
        }

        if (client != null)
        {
            try
            {
                await client.SendAnnounceAsync(@event, offers, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                HandleTrackerFailure(runtime.Url, ex);
                throw;
            }
        }
    }

    public async Task SendSignalAsync(TrackerRuntime runtime, string toPeerId, string offerId, JsonObject signalData, CancellationToken cancellationToken)
    {
        WebTorrentTrackerClient? client;
        lock (_clients)
        {
            client = _clients.FirstOrDefault(c => c.Runtime == runtime);
        }

        if (client != null)
        {
            try
            {
                await client.SendSignalAsync(toPeerId, offerId, signalData, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                HandleTrackerFailure(runtime.Url, ex);
                throw;
            }
        }
    }

    public async Task<bool> TryReconnectAsync(TrackerRuntime runtime, CancellationToken cancellationToken)
    {
        WebTorrentTrackerClient? client;
        lock (_clients)
        {
            client = _clients.FirstOrDefault(c => c.Runtime == runtime);
        }

        if (client == null) return false;

        lock (runtime.SyncRoot)
        {
            if (runtime.ReconnectInProgress) return false;
            runtime.ReconnectInProgress = true;
        }

        try
        {
            await client.ConnectAsync(isInitial: false, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            HandleTrackerFailure(runtime.Url, ex);
            return false;
        }
        finally
        {
            lock (runtime.SyncRoot)
            {
                runtime.ReconnectInProgress = false;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<WebTorrentTrackerClient> snapshot;
        lock (_clients)
        {
            snapshot = new List<WebTorrentTrackerClient>(_clients);
            _clients.Clear();
        }

        foreach (var client in snapshot)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
