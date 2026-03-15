using Microsoft.Extensions.Logging;
using PeerSharp.PiecePicking;
using System.Collections.Concurrent;

namespace PeerSharp.Internals;

internal sealed class PieceStateManager : IDisposable
{
    private readonly ConcurrentDictionary<int, PieceState> _activePieces = new();
    private readonly PiecePicker _piecePicker;
    private readonly ILogger<PieceStateManager> _logger;
    private readonly int _maxActivePieces;
    private int _activePiecesCount;
    private AtomicDisposal _disposal = new();

    public PieceStateManager(PiecePicker piecePicker, ILogger<PieceStateManager> logger, int maxActivePieces)
    {
        _piecePicker = piecePicker;
        _logger = logger;
        _maxActivePieces = maxActivePieces;
    }

    public void Dispose()
    {
        if (_disposal.MarkDisposed())
        {
            foreach (var piece in _activePieces.Values)
            {
                piece.Dispose();
            }
            _activePieces.Clear();
            _activePiecesCount = 0;
        }
    }

    public ConcurrentDictionary<int, PieceState> ActivePieces => _activePieces;

    public int Count => Interlocked.CompareExchange(ref _activePiecesCount, 0, 0);

    public int MaxActivePieces => _maxActivePieces;

    public bool TryAddPiece(PieceState state)
    {
        if (_activePieces.TryAdd(state.Index, state))
        {
            Interlocked.Increment(ref _activePiecesCount);
            return true;
        }
        return false;
    }

    public void AddOrReplacePiece(PieceState state)
    {
        bool added = false;
        _activePieces.AddOrUpdate(
            state.Index,
            _ =>
            {
                added = true;
                return state;
            },
            (_, __) => state);

        if (added)
        {
            Interlocked.Increment(ref _activePiecesCount);
        }
    }

    public bool TryRemovePiece(int index, out PieceState state)
    {
        if (_activePieces.TryRemove(index, out state!))
        {
            Interlocked.Decrement(ref _activePiecesCount);
            return true;
        }
        return false;
    }

    public bool TryGetPiece(int index, out PieceState state)
    {
        return _activePieces.TryGetValue(index, out state!);
    }

    public bool ContainsPiece(int index)
    {
        return _activePieces.ContainsKey(index);
    }

    public void PruneStalePieces()
    {
        if (Count < _maxActivePieces)
        {
            return;
        }

        var toRemove = new List<PieceState>();
        foreach (var kv in _activePieces)
        {
            var pieceIdx = kv.Key;
            if (_piecePicker.GetAvailability(pieceIdx) <= 0)
            {
                toRemove.Add(kv.Value);
            }
        }

        foreach (var state in toRemove)
        {
            if (TryRemovePiece(state.Index, out _))
            {
                state.Dispose();
                _logger.LogDebug("Pruned stale piece {PieceIndex} (no active peers)", state.Index);
            }
        }
    }
}
