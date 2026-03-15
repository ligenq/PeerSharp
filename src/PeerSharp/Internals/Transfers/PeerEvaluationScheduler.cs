using Microsoft.Extensions.Logging;
using PeerSharp.Internals.Peers;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace PeerSharp.Internals;

internal sealed class PeerEvaluationScheduler
{
    private readonly Channel<PeerCommunication> _queue;
    private readonly ConcurrentDictionary<PeerCommunication, byte> _queuedPeers = new();
    private readonly Func<PeerCommunication, Task> _evaluateAsync;
    private readonly ILogger<PeerEvaluationScheduler> _logger;

    public PeerEvaluationScheduler(
        Channel<PeerCommunication> queue,
        Func<PeerCommunication, Task> evaluateAsync,
        ILogger<PeerEvaluationScheduler> logger)
    {
        _queue = queue;
        _evaluateAsync = evaluateAsync;
        _logger = logger;
    }

    public void Enqueue(PeerCommunication peer)
    {
        if (_queuedPeers.TryAdd(peer, 0) && !_queue.Writer.TryWrite(peer))
        {
            _queuedPeers.TryRemove(peer, out _);
            _logger.LogWarning("Failed to queue peer {RemoteEndPoint} for evaluation", peer.RemoteEndPoint);
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (await _queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (_queue.Reader.TryRead(out var peer))
            {
                _queuedPeers.TryRemove(peer, out _);

                try
                {
                    await _evaluateAsync(peer).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error evaluating peer {RemoteEndPoint}", peer.RemoteEndPoint);
                }
            }
        }
    }
}
