using PeerSharp.PiecePicking;
using Microsoft.Extensions.Time.Testing;

namespace PeerSharp.Tests.Core.Pieces;

public class PiecePickerTests
{
    private class MockContext : IPiecePickerContext
    {
        public int PieceCount { get; set; }
        public HashSet<int> CompletedPieces { get; } = new();
        public List<FileSelection>? Selection { get; set; }
        public DownloadStrategy DownloadStrategy { get; set; } = DownloadStrategy.RarestFirst;
        public List<int>? PriorityPieces { get; set; }
        public HashSet<int> ActivePieces { get; } = new();

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
        public HashSet<int> Pieces { get; } = new();
        public bool IsChoking { get; set; }
        public HashSet<int> AllowedFastPieces { get; } = new();
        public List<int> SuggestedPieces { get; } = new();

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
            PriorityPieces = new List<int> { 8, 9 }
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
        // Arrange
        var ctx = new MockContext { PieceCount = 4 };
        var picker = new PiecePicker(ctx, _timeProvider, _random);

        // Act - grow piece count and update availability beyond initial size
        ctx.PieceCount = 10;
        var ex = Record.Exception(() =>
        {
            picker.IncrementAvailability(8);
            picker.RefreshSelection();
        });

        // Assert
        Assert.Null(ex);
        Assert.Equal(1, picker.GetAvailability(8));
    }
}






