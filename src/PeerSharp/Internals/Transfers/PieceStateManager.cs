using Microsoft.Extensions.Logging;
using PeerSharp.PiecePicking;
using System.Collections.Concurrent;

namespace PeerSharp.Internals.Transfers;

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

    /// <summary>
    /// Adds a piece, replacing and disposing any piece already held for that index.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written as an explicit add-or-update loop rather than with
    /// <see cref="ConcurrentDictionary{TKey, TValue}.AddOrUpdate(TKey, Func{TKey, TValue}, Func{TKey, TValue, TValue})"/>,
    /// because that method's factories carry no promise of running once, or of the branch that ran
    /// being the branch that won. An earlier version set a flag inside the add factory and
    /// incremented the count from it, which counted additions that had in fact become updates: the
    /// count drifted upwards under concurrent starts, and only upwards. It gates how many pieces may
    /// be in flight, so drifting up means the engine gradually stops starting pieces it has room
    /// for. <c>TryAdd</c> and <c>TryUpdate</c> each report what they actually did.
    /// </para>
    /// <para>
    /// The replaced piece is disposed here. It leaves the dictionary unreachable, and it owns pooled
    /// block buffers that are never returned otherwise.
    /// </para>
    /// </remarks>
    public void AddOrReplacePiece(PieceState state)
    {
        while (true)
        {
            if (_activePieces.TryAdd(state.Index, state))
            {
                Interlocked.Increment(ref _activePiecesCount);
                return;
            }

            if (!_activePieces.TryGetValue(state.Index, out var existing))
            {
                // Removed between the two calls; go round and add it.
                continue;
            }

            if (ReferenceEquals(existing, state))
            {
                return;
            }

            if (_activePieces.TryUpdate(state.Index, state, existing))
            {
                // One piece replaced one piece, so the count is unchanged.
                existing.Dispose();
                return;
            }
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
