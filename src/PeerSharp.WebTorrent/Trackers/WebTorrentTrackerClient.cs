using Microsoft.Extensions.Logging;
using PeerSharp.Interfaces;
using PeerSharp.WebTorrent.Network;
using PeerSharp.WebTorrent.Signaling;
using System.Text.Json.Nodes;

namespace PeerSharp.WebTorrent.Trackers;

internal sealed class WebTorrentTrackerClient : IAsyncDisposable
{
    private readonly string _url;
    private readonly IPeerTransportHost _host;
    private readonly IWebSocketConnectionFactory _socketFactory;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _minReannounceInterval;
    private readonly long _uploadedBaseline;
    private readonly long _downloadedBaseline;
    private readonly Action<WebTorrentSignalMessage> _onSignalReceived;
    private readonly Action<Exception> _onConnectionFailed;

    private readonly TrackerRuntime _runtime;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private Task? _disposeTask;

    public WebTorrentTrackerClient(
        string url,
        IPeerTransportHost host,
        IWebSocketConnectionFactory socketFactory,
        ILogger logger,
        TimeProvider timeProvider,
        TimeSpan connectTimeout,
        TimeSpan minReannounceInterval,
        long uploadedBaseline,
        long downloadedBaseline,
        Action<WebTorrentSignalMessage> onSignalReceived,
        Action<Exception> onConnectionFailed)
    {
        _url = url;
        _host = host;
        _socketFactory = socketFactory;
        _logger = logger;
        _timeProvider = timeProvider;
        _connectTimeout = connectTimeout;
        _minReannounceInterval = minReannounceInterval;
        _uploadedBaseline = uploadedBaseline;
        _downloadedBaseline = downloadedBaseline;
        _onSignalReceived = onSignalReceived;
        _onConnectionFailed = onConnectionFailed;
        _runtime = new TrackerRuntime(url);
    }

    public TrackerRuntime Runtime => _runtime;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await ConnectAsync(isInitial: true, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Connected to tracker {Url}", _url);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _onConnectionFailed(ex);
        }
    }

    public async Task ConnectAsync(bool isInitial, CancellationToken cancellationToken)
    {
        lock (_runtime.SyncRoot) { ObjectDisposedException.ThrowIf(_disposeTask != null, this); }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        await _connectGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try { await ConnectCoreAsync(linked.Token).ConfigureAwait(false); }
        finally { _connectGate.Release(); }
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        var socket = _socketFactory.Create();
        Task? connectTask = null;
        try
        {
            connectTask = socket.ConnectAsync(new Uri(_url), cancellationToken);
            await connectTask.WaitAsync(_connectTimeout, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Observe connectTask on every failure path, not just timeouts: we stop awaiting it
            // here, and the DisposeAsync below will typically fault it. An unobserved fault would
            // surface later as TaskScheduler.UnobservedTaskException.
            _ = connectTask?.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            try
            {
                await socket.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                _logger.LogDebug(cleanupException, "Ignored socket cleanup error after tracker connect failed for {Url}", _url);
            }

            throw;
        }

        int generation;
        IWebSocketConnection? previous;
        lock (_runtime.SyncRoot)
        {
            if (cancellationToken.IsCancellationRequested || _disposeTask != null)
            {
                _ = socket.DisposeAsync().AsTask().ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
                cancellationToken.ThrowIfCancellationRequested();
                throw new ObjectDisposedException(nameof(WebTorrentTrackerClient));
            }
            previous = _runtime.Socket;
            _runtime.Socket = socket;
            _runtime.IsConnected = true;
            _runtime.ConsecutiveFailures = 0;
            _runtime.LastError = null;
            _runtime.NextReconnectAt = DateTimeOffset.MinValue;
            _runtime.ReconnectInProgress = false;
            _runtime.CompletedSent = false;
            generation = ++_runtime.Generation;
            _runtime.ReceiveTask = RunReceiveLoopAsync(generation, socket, _cts.Token);
        }
        if (previous != null && !ReferenceEquals(previous, socket))
        {
            try { await previous.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Ignored old tracker socket cleanup error for {Url}", _url); }
        }
    }

    public async Task SendAnnounceAsync(string? @event, JsonArray? offers, CancellationToken cancellationToken)
    {
        var payload = CreateAnnounceBasePayload();
        if (@event != null)
        {
            payload["event"] = @event;
        }
        if (offers != null)
        {
            payload["numwant"] = offers.Count;
            payload["offers"] = offers;
        }

        await SendPayloadAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendSignalAsync(string toPeerId, string offerId, JsonObject signalData, CancellationToken cancellationToken)
    {
        var payload = CreateAnnounceBasePayload();
        payload["to_peer_id"] = toPeerId;
        payload["offer_id"] = offerId;

        // signalData should be either "answer" or "candidate"
        foreach (var property in signalData)
        {
            payload[property.Key] = property.Value?.DeepClone();
        }

        await SendPayloadAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendPayloadAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        lock (_runtime.SyncRoot) { ObjectDisposedException.ThrowIf(_disposeTask != null, this); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10), _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token, timeout.Token);
        await _sendGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (!TryGetSocket(out var socket)) throw new IOException("The WebTorrent tracker is disconnected.");
            await socket.SendTextAsync(payload.ToJsonString(), linked.Token).ConfigureAwait(false);
        }
        finally { _sendGate.Release(); }
    }

    private JsonObject CreateAnnounceBasePayload()
    {
        long uploaded = Math.Max(0, _host.DataUploaded - _uploadedBaseline);
        long downloaded = Math.Max(0, _host.DataDownloaded - _downloadedBaseline);
        return new JsonObject
        {
            ["action"] = "announce",
            ["info_hash"] = BinaryStringEncoding.Encode(_host.Hash.Memory),
            ["peer_id"] = BinaryStringEncoding.Encode(_host.PeerId),
            ["uploaded"] = uploaded,
            ["downloaded"] = downloaded,
            ["left"] = _host.DataLeft
        };
    }

    private bool TryGetSocket(out IWebSocketConnection socket)
    {
        lock (_runtime.SyncRoot)
        {
            socket = _runtime.Socket!;
            return _runtime.IsConnected && socket != null;
        }
    }

    private async Task RunReceiveLoopAsync(int generation, IWebSocketConnection connectedSocket, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IWebSocketConnection? socket;
                lock (_runtime.SyncRoot)
                {
                    if (_runtime.Generation != generation)
                    {
                        return;
                    }

                    socket = _runtime.Socket;
                }
                if (socket == null)
                {
                    break;
                }

                string message = await socket.ReceiveTextAsync(cancellationToken).ConfigureAwait(false);

                lock (_runtime.SyncRoot)
                {
                    if (_runtime.Generation != generation || !ReferenceEquals(_runtime.Socket, socket))
                    {
                        return;
                    }
                }

                if (string.IsNullOrWhiteSpace(message))
                {
                    continue;
                }

                var signal = WebTorrentProtocolCodec.Parse(message);
                string localHash = BinaryStringEncoding.Encode(_host.Hash.ToArray());
                if (!string.Equals(signal.InfoHash, localHash, StringComparison.Ordinal)) continue;
                if (signal.Interval.HasValue)
                {
                    var interval = TimeSpan.FromSeconds(Math.Max((int)_minReannounceInterval.TotalSeconds, signal.Interval.Value));
                    lock (_runtime.SyncRoot)
                    {
                        _runtime.ReannounceInterval = interval;
                        _runtime.NextAnnounce = _timeProvider.GetUtcNow() + interval;
                    }
                }

                if (string.Equals(signal.PeerId, BinaryStringEncoding.Encode(_host.PeerId), StringComparison.Ordinal))
                {
                    continue;
                }

                _onSignalReceived(signal);
            }
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Receive loop canceled for tracker {Url}", _url);
        }
        catch (Exception ex)
        {
            lock (_runtime.SyncRoot)
            {
                if (_runtime.Generation != generation || !ReferenceEquals(_runtime.Socket, connectedSocket) || _disposeTask != null) return;
                _logger.LogError(ex, "Receive loop failed for tracker {Url}", _url);
                _onConnectionFailed(ex);
            }
        }
        finally
        {
            try { await connectedSocket.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Ignored receive socket cleanup error for {Url}", _url); }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_runtime.SyncRoot)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        IWebSocketConnection? socket;
        Task? receiveTask;
        lock (_runtime.SyncRoot)
        {
            socket = _runtime.Socket;
            receiveTask = _runtime.ReceiveTask;
            _runtime.Socket = null;
            _runtime.IsConnected = false;
            _runtime.Generation++;
        }

        if (socket != null)
        {
            try
            {
                await socket.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ignored error while disposing tracker socket {Url}", _url);
            }
        }

        if (receiveTask != null)
        {
            try
            {
                await receiveTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ignored error while awaiting tracker receive loop {Url}", _url);
            }
        }

        await _connectGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await _sendGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { _cts.Dispose(); }
            finally { _sendGate.Release(); }
        }
        finally { _connectGate.Release(); }
    }
}
