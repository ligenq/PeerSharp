using PeerSharp.PiecePicking;
using Microsoft.Extensions.Time.Testing;

namespace PeerSharp.Tests.Core.Pieces;

public class PiecePickerTests
{
    private class MockContext : IPiecePickerContext
    {
        public int PieceCount { get; set; }
        public HashSet<int> CompletedPieces { get; } = [];
        public List<FileSelection>? Selection { get; set; }
        public DownloadStrategy DownloadStrategy { get; set; } = DownloadStrategy.RarestFirst;
        public List<int>? PriorityPieces { get; set; }
        public HashSet<int> ActivePieces { get; } = [];

        public bool HasPiece(int pieceIndex)
        {
            return CompletedPieces.Contains(pieceIndex);
        }

        public IReadOnlyList<FileSelection>? GetFileSelectionSnapshot()
        {
            return Selection;
        }

        public bool IsPieceNeeded(int pieceIndex, IReadOnlyList<FileSelection>? selection)
        {
            return true;
        }

        public Priority GetPiecePriority(int pieceIndex, IReadOnlyList<FileSelection>? selection)
        {
            return Priority.Normal;
        }

        public bool IsPieceActive(int pieceIndex)
        {
            return ActivePieces.Contains(pieceIndex);
        }

        IReadOnlyList<int>? IPiecePickerContext.StreamingPriorityPieces => PriorityPieces;
    }

    private class MockPeer : IPeerPieceInfo
    {
        public HashSet<int> Pieces { get; } = [];
        public bool IsChoking { get; set; }
        public HashSet<int> AllowedFastPieces { get; } = [];
        public List<int> SuggestedPieces { get; } = [];

        public bool HasPiece(int pieceIndex)
        {
            return Pieces.Contains(pieceIndex);
        }

        public int Count => 100;
        public bool IsAllowedFast(int pieceIndex)
        {
            return AllowedFastPieces.Contains(pieceIndex);
        }

        public IEnumerable<int> GetSuggestedPieces()
        {
            return SuggestedPieces;
        }
    }

    private readonly FakeTimeProvider _timeProvider = new();
    private readonly Random _random = new(42);

    [Fact]
    public void PickNextPiece_RarestFirst_PicksLeastCommon()
    {
        // Arrange
        var ctx = new MockContext { PieceCount = 5 };
        var picker = new PiecePicker(ctx, _timeProvider, _random);

        // Piece 0: avail 3, Piece 1: avail 1, Piece 2: avail 2
        picker.IncrementAvailability(0);
        picker.IncrementAvailability(0);
        picker.IncrementAvailability(0);
        picker.IncrementAvailability(1);
        picker.IncrementAvailability(2);
        picker.IncrementAvailability(2);

        var peer = new MockPeer();
        for (int i = 0; i < 5; i++)
        {
            peer.Pieces.Add(i);
        }

        // Act
        bool success = picker.PickNextPiece(peer, out int picked);

        // Assert
        Assert.True(success);
        Assert.Equal(3, picked); // Rarest available (Piece 3 and 4 have 0 availability)
    }

    [Fact]
    public void PickNextPiece_RespectsPeerAvailability()
    {
        var ctx = new MockContext { PieceCount = 10 };
        var picker = new PiecePicker(ctx, _timeProvider, _random);
        var peer = new MockPeer();
        peer.Pieces.Add(5);

        bool success = picker.PickNextPiece(peer, out int picked);
        Assert.True(success);
        Assert.Equal(5, picked);
    }

    [Fact]
    public void PickNextPiece_Choked_OnlyPicksAllowedFast()
    {
        var ctx = new MockContext { PieceCount = 10 };
        var picker = new PiecePicker(ctx, _timeProvider, _random);
        var peer = new MockPeer { IsChoking = true };
        peer.Pieces.Add(1);
        peer.Pieces.Add(2);
        peer.AllowedFastPieces.Add(2);

        bool success = picker.PickNextPiece(peer, out int picked);
        Assert.True(success);
        Assert.Equal(2, picked);
    }

    [Fact]
    public void PickNextPiece_Streaming_PrioritizesStreamingPieces()
    {
        var ctx = new MockContext
        {
            PieceCount = 10,
            DownloadStrategy = DownloadStrategy.Streaming,
            PriorityPieces = [8, 9]
        };
        var picker = new PiecePicker(ctx, _timeProvider, _random);
        var peer = new MockPeer();
        for (int i = 0; i < 10; i++)
        {
            peer.Pieces.Add(i);
        }

        bool success = picker.PickNextPiece(peer, out int picked);
        Assert.True(success);
        Assert.Equal(8, picked);
    }

    [Fact]
    public void PickNextPiece_Sequential_PicksInOrder()
    {
        var ctx = new MockContext { PieceCount = 10, DownloadStrategy = DownloadStrategy.Sequential };
        var picker = new PiecePicker(ctx, _timeProvider, _random);
        var peer = new MockPeer();
        peer.Pieces.Add(3);
        peer.Pieces.Add(1);

        bool success = picker.PickNextPiece(peer, out int picked);
        Assert.True(success);
        Assert.Equal(1, picked);
    }

    [Fact]
    public void RefreshSelection_AllowsPieceCountGrowth()
    {
        var ctx = new MockContext { PieceCount = 4 };
        var picker = new PiecePicker(ctx, _timeProvider, _random);

        ctx.PieceCount = 10;
        var ex = Record.Exception(() =>
        {
            picker.IncrementAvailability(8);
            picker.RefreshSelection();
        });

        Assert.Null(ex);
        Assert.Equal(1, picker.GetAvailability(8));
    }

    [Fact]
    public void GetAvailability_NegativeIndex_ReturnsZero()
    {
        var ctx = new MockContext { PieceCount = 5 };
        var picker = new PiecePicker(ctx, _timeProvider, _random);

        Assert.Equal(0, picker.GetAvailability(-1));
    }

    [Fact]
    public void DecrementAvailability_NegativeIndex_NoOp()
    {
        var ctx = new MockContext { PieceCount = 5 };
        var picker = new PiecePicker(ctx, _timeProvider, _random);
        picker.IncrementAvailability(0);

        var ex = Record.Exception(() => picker.DecrementAvailability(-1));

        Assert.Null(ex);
        Assert.Equal(1, picker.GetAvailability(0)); // Other piece unaffected
    }

    [Fact]
    public void DecrementAvailability_ReducesCount()
    {
        var ctx = new MockContext { PieceCount = 5 };
        var picker = new PiecePicker(ctx, _timeProvider, _random);
        picker.IncrementAvailability(2);
        picker.IncrementAvailability(2);

        picker.DecrementAvailability(2);

        Assert.Equal(1, picker.GetAvailability(2));
    }

    [Fact]
    public void InvalidateSelection_ForcesRefreshOnNextGetCandidates()
    {
        var ctx = new MockContext { PieceCount = 5 };
        var picker = new PiecePicker(ctx, _timeProvider, _random);
        picker.GetCandidates(); // Initial refresh

        ctx.CompletedPieces.Add(0); // Mark piece 0 as complete
        picker.InvalidateSelection();

        var candidates = picker.GetCandidates();
        Assert.DoesNotContain(0, candidates);
    }

    [Fact]
    public void GetSelectionSnapshot_ReturnsNullBeforeFirstInvalidation()
    {
        var ctx = new MockContext { PieceCount = 5 };
        var picker = new PiecePicker(ctx, _timeProvider, _random);

        // Before any refresh or invalidation, snapshot is null
        var snapshot = picker.GetSelectionSnapshot();
        Assert.Null(snapshot);
    }

    [Fact]
    public void GetCandidates_ReturnsSortedByAvailability()
    {
        var ctx = new MockContext { PieceCount = 3 };
        var picker = new PiecePicker(ctx, _timeProvider, _random);
        picker.IncrementAvailability(2); // piece 2 most common
        picker.IncrementAvailability(2);
        picker.IncrementAvailability(1); // piece 1 medium

        var candidates = picker.GetCandidates().ToList();

        // Rarest first: piece 0 (avail=0) should appear before piece 2 (avail=2)
        Assert.True(candidates.IndexOf(0) < candidates.IndexOf(2));
    }

    [Fact]
    public void PickNextPiece_PieceAlreadyOwned_NotPicked()
    {
        var ctx = new MockContext { PieceCount = 3 };
        ctx.CompletedPieces.Add(0);
        ctx.CompletedPieces.Add(1);
        var picker = new PiecePicker(ctx, _timeProvider, _random);
        var peer = new MockPeer();
        for (int i = 0; i < 3; i++)
        {
            peer.Pieces.Add(i);
        }

        bool success = picker.PickNextPiece(peer, out int picked);

        Assert.True(success);
        Assert.Equal(2, picked); // Only piece 2 available
    }

    [Fact]
    public void PickNextPiece_ActivePiece_NotPicked()
    {
        var ctx = new MockContext { PieceCount = 3 };
        ctx.ActivePieces.Add(0);
        ctx.ActivePieces.Add(1);
        var picker = new PiecePicker(ctx, _timeProvider, _random);
        var peer = new MockPeer();
        for (int i = 0; i < 3; i++)
        {
            peer.Pieces.Add(i);
        }

        bool success = picker.PickNextPiece(peer, out int picked);

        Assert.True(success);
        Assert.Equal(2, picked);
    }

    [Fact]
    public void PickNextPiece_AllPiecesOwned_ReturnsFalse()
    {
        var ctx = new MockContext { PieceCount = 3 };
        for (int i = 0; i < 3; i++)
        {
            ctx.CompletedPieces.Add(i);
        }

        var picker = new PiecePicker(ctx, _timeProvider, _random);
        var peer = new MockPeer();
        for (int i = 0; i < 3; i++)
        {
            peer.Pieces.Add(i);
        }

        bool success = picker.PickNextPiece(peer, out int picked);

        Assert.False(success);
        Assert.Equal(-1, picked);
    }

    [Fact]
    public void PickNextPiece_Streaming_NullPriorityPieces_FallsBackToRarestFirst()
    {
        var ctx = new MockContext
        {
            PieceCount = 5,
            DownloadStrategy = DownloadStrategy.Streaming,
            PriorityPieces = null
        };
        var picker = new PiecePicker(ctx, _timeProvider, _random);
        var peer = new MockPeer();
        for (int i = 0; i < 5; i++)
        {
            peer.Pieces.Add(i);
        }

        bool success = picker.PickNextPiece(peer, out int picked);

        Assert.True(success);
        Assert.InRange(picked, 0, 4);
    }

    [Fact]
    public void PickNextPiece_SuggestedPieces_PreferredOverCandidates()
    {
        var ctx = new MockContext { PieceCount = 10 };
        var picker = new PiecePicker(ctx, _timeProvider, _random);

        // Make piece 7 high availability so it wouldn't normally be picked first
        for (int i = 0; i < 5; i++)
        {
            picker.IncrementAvailability(7);
        }

        var peer = new MockPeer();
        for (int i = 0; i < 10; i++)
        {
            peer.Pieces.Add(i);
        }

        peer.SuggestedPieces.Add(7);

        bool success = picker.PickNextPiece(peer, out int picked);

        Assert.True(success);
        Assert.Equal(7, picked);
    }

    [Fact]
    public void PickNextPiece_Sequential_NoPeerPieces_ReturnsFalse()
    {
        var ctx = new MockContext { PieceCount = 5, DownloadStrategy = DownloadStrategy.Sequential };
        var picker = new PiecePicker(ctx, _timeProvider, _random);
        var peer = new MockPeer(); // No pieces

        bool success = picker.PickNextPiece(peer, out _);

        Assert.False(success);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var ctx = new MockContext { PieceCount = 5 };
        var picker = new PiecePicker(ctx, _timeProvider, _random);

        picker.Dispose();
        var ex = Record.Exception(() => picker.Dispose());

        Assert.Null(ex);
    }

    [Fact]
    public void BucketSort_HighAvailabilityPieces_SortedCorrectly()
    {
        var ctx = new MockContext { PieceCount = 3 };
        var picker = new PiecePicker(ctx, _timeProvider, _random);

        // Saturate buckets: give piece 0 very high availability (>= 128)
        for (int i = 0; i < 200; i++)
        {
            picker.IncrementAvailability(0);
        }

        picker.IncrementAvailability(1); // piece 1 low availability

        var candidates = picker.GetCandidates().ToList();

        // Piece 2 (avail=0) → piece 1 (avail=1) → piece 0 (avail=200)
        Assert.True(candidates.IndexOf(2) < candidates.IndexOf(1));
        Assert.True(candidates.IndexOf(1) < candidates.IndexOf(0));
    }
}






