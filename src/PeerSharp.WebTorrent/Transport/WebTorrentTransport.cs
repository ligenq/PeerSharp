using Microsoft.Extensions.Logging;
using PeerSharp.Interfaces;
using PeerSharp.WebTorrent.Configuration;

namespace PeerSharp.WebTorrent.Transport;

/// <summary>
/// Adapts <see cref="WebTorrentSession"/> to the <see cref="IPeerTransport"/> lifecycle.
/// A new session is created on every <see cref="StartAsync"/> call so the transport tolerates
/// the torrent being stopped and started again — <see cref="WebTorrentSession"/> itself is
/// one-shot and cannot be reused after disposal.
/// </summary>
internal sealed class WebTorrentTransport : IPeerTransport
{
    private readonly ITorrent _torrent;
    private readonly WebTorrentSessionOptions? _options;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebTorrentSession? _session;
    private bool _disposed;

    public WebTorrentTransport(ITorrent torrent, WebTorrentSessionOptions? options, ILoggerFactory? loggerFactory)
    {
        _torrent = torrent;
        _options = options;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Exposed for tests so they can observe the live session created on the most recent <see cref="StartAsync"/>.
    /// </summary>
    internal WebTorrentSession? CurrentSession => _session;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_session != null)
            {
                return;
            }

            var session = new WebTorrentSession(_torrent, _options, _loggerFactory);
            try
            {
                await session.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _session = session;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        WebTorrentSession? toDispose;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            toDispose = _session;
            _session = null;
        }
        finally
        {
            _gate.Release();
        }

        if (toDispose != null)
        {
            await toDispose.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        WebTorrentSession? toDispose;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            toDispose = _session;
            _session = null;
        }
        finally
        {
            _gate.Release();
        }

        if (toDispose != null)
        {
            await toDispose.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }
}
