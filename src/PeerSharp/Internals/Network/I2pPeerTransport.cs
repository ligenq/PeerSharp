using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PeerSharp.Interfaces;

namespace PeerSharp.Internals.Network;

/// <summary>Bounded destination queue; one SAM session is shared across all torrents.</summary>
internal sealed class I2pPeerTransport : IPeerTransport
{
    private readonly Torrent _torrent;
    private readonly I2pSamSession _session;
    private readonly Channel<string> _peers = Channel.CreateBounded<string>(128);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _attempts = new(StringComparer.Ordinal);
    private CancellationTokenSource? _stop;
    private Task[] _workers = [];

    public I2pPeerTransport(Torrent torrent, I2pSamSession session) { _torrent = torrent; _session = session; }

    public void AddPeers(IEnumerable<string> peers)
    {
        foreach (string peer in peers)
        {
            I2pSamSession.ValidateDestination(peer);
            lock (_gate)
            {
                var now = _torrent.Services.TimeProvider.GetUtcNow();
                if (_attempts.TryGetValue(peer, out var next) && now < next) continue;
                if (!_peers.Writer.TryWrite(peer)) break;
                if (_attempts.Count >= 1024) _attempts.Clear();
                _attempts[peer] = now.AddMinutes(2);
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_stop != null) return;
        await _session.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        _stop = new CancellationTokenSource();
        _workers = Enumerable.Range(0, 4).Select(_ => WorkerAsync(_stop.Token)).ToArray();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_stop == null) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_workers).ConfigureAwait(false);
        _stop.Dispose(); _stop = null;
        while (_peers.Reader.TryRead(out _)) { /* Discard stopped-torrent work. */ }
        lock (_gate) _attempts.Clear();
    }

    private async Task WorkerAsync(CancellationToken ct)
    {
        try
        {
            await foreach (string destination in _peers.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Stream? stream = null;
                try
                {
                    if (_torrent.Peers.ConnectedCount >= _torrent.Settings.Connection.MaxPeersPerTorrent) continue;
                    stream = await _session.ConnectAsync(destination, ct).ConfigureAwait(false);
                    if (_torrent.InfoFile.Info.IsSsl) stream = await TorrentTls.ConnectAsync(stream, _torrent, ct).ConfigureAwait(false);
                    await _torrent.PeersInternal.AddConnectedPeerAsync(stream, true, cancellationToken: ct).ConfigureAwait(false);
                    stream = null;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { _torrent.Services.LoggerFactory.CreateLogger<I2pPeerTransport>().LogDebug(ex, "I2P peer connection failed"); }
                finally { if (stream != null) await stream.DisposeAsync().ConfigureAwait(false); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* Normal worker shutdown. */ }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);
}
