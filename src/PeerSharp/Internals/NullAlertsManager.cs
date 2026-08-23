using PeerSharp.Core;
using PeerSharp.Interfaces;

namespace PeerSharp.Internals;

/// <summary>
/// Swallows every alert posted to it.
///
/// <para>
/// Given to a torrent the engine added on its own behalf rather than the caller's - a metadata
/// fetch - so that its lifecycle never reaches the engine's alert queue. Nothing downstream can
/// tell a transient torrent from a real one once an alert is queued, so the only place to make
/// the distinction is at the source.
/// </para>
/// </summary>
internal sealed class NullAlertsManager : IAlertsManager
{
    public static readonly NullAlertsManager Instance = new();

    private NullAlertsManager()
    {
    }

    public async IAsyncEnumerable<Alert> GetAlertsAsync(
        TimeSpan? pollingInterval = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Matches the real manager's contract: the stream never ends on its own, and cancelling is
        // the only way out. Nothing is ever queued here, so the wait is on the token alone - no
        // clock is involved, which is why there is no TimeProvider to thread through.
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (cancellationToken.Register(() => cancelled.TrySetCanceled(cancellationToken)).ConfigureAwait(false))
        {
            await cancelled.Task.ConfigureAwait(false);
        }

        yield break;
    }

    public List<Alert> PopAlerts() => [];

    public void RegisterAlerts(uint alertMask)
    {
    }

    public void ConfigAlert(AlertId id, string configType)
    {
    }

    public void MetadataAlert(AlertId id, ITorrent torrent)
    {
    }

    public void MetadataProgressAlert(ITorrent torrent, float progress, int receivedPieces, int totalPieces)
    {
    }

    public void PieceCompletedAlert(ITorrent torrent, int pieceIndex, int completedPieces, int totalPieces)
    {
    }

    public void PieceHashFailedAlert(ITorrent torrent, int pieceIndex, int failures, System.Net.IPEndPoint? suspectedPeer)
    {
    }

    public void PeerBlockedAlert(ITorrent torrent, System.Net.IPEndPoint endpoint, PeerBlockReason reason)
    {
    }

    public void ListenPortChangedAlert(int requestedPort, int actualPort, ListenTransport transport)
    {
    }

    public void PostAlert(Alert alert)
    {
    }

    public void ProgressChangedAlert(ITorrent torrent, float progress, float selectionProgress, ulong finishedBytes, ulong totalBytes, int completedPieces, int totalPieces)
    {
    }

    public void StateChangedAlert(ITorrent torrent, TorrentState previousState, TorrentState newState)
    {
    }

    public void TorrentAlert(AlertId id, ITorrent torrent)
    {
    }

    public void TorrentErrorAlert(ITorrent torrent, Exception exception)
    {
    }

    public void TransferStatsAlert(ITorrent torrent, long downloaded, long uploaded, long downloadSpeed, long uploadSpeed, int connectedPeers)
    {
    }
}
