using PeerSharp.Internals;
using PeerSharp.Internals.Framework;

namespace PeerSharp.Tests.Core.Framework;

public class FileSelectionManagerTests
{
    private readonly TorrentFileMetadata _metadata;
    private readonly FileSelectionManager _manager;

    public FileSelectionManagerTests()
    {
        _metadata = new TorrentFileMetadata();
        _metadata.Info.PieceSize = 100;
        _metadata.Info.FullSize = 300;
        _metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "f1", Size = 150, Offset = 0 });
        _metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "f2", Size = 150, Offset = 150 });
        _metadata.Info.Pieces.Add(new byte[20]); // Piece 0
        _metadata.Info.Pieces.Add(new byte[20]); // Piece 1
        _metadata.Info.Pieces.Add(new byte[20]); // Piece 2

        _manager = new FileSelectionManager(_metadata);
    }

    [Fact]
    public void Initialize_Default_AllSelected()
    {
        var pieces = new PiecesProgress(3);
        _manager.Initialize(null, pieces);

        var selections = _manager.GetAllFileSelections();
        Assert.Equal(2, selections.Count);
        Assert.All(selections, s => Assert.True(s.Selected));
        Assert.Equal(3, _manager.TotalSelectedPieces);
    }

    [Fact(Timeout = 30000)]
    public async Task SetFilePriority_UpdatesStatsAndObserver()
    {
        var pieces = new PiecesProgress(3);
        _manager.Initialize(null, pieces);

        var observer = new MockObserver();
        _manager.SetObserver(observer);

        // Deselect second file (covers pieces 1 and 2)
        // Piece 0: Offset 0-99 (File 1)
        // Piece 1: Offset 100-199 (File 1 and 2)
        // Piece 2: Offset 200-299 (File 2)
        await _manager.SetFilePriorityAsync(1, Priority.DoNotDownload);

        Assert.Equal(Priority.DoNotDownload, _manager.GetFileSelection(1).Priority);
        Assert.False(_manager.GetFileSelection(1).Selected);

        // Piece 1 is still needed by File 1
        Assert.Equal(2, _manager.TotalSelectedPieces);
        Assert.True(observer.Changed);
    }

    [Fact]
    public void OnPieceVerified_IncrementsReceivedCount()
    {
        var pieces = new PiecesProgress(3);
        _manager.Initialize(null, pieces);

        _manager.OnPieceVerified(0);
        Assert.Equal(1, _manager.ReceivedSelectedPieces);
    }

    [Fact]
    public void CalculateSelectionProgress_Works()
    {
        var pieces = new PiecesProgress(3);
        _manager.Initialize(null, pieces);

        _manager.OnPieceVerified(0);
        Assert.Equal(1.0f / 3.0f, _manager.CalculateSelectionProgress());
    }

    private class MockObserver : IFileSelectionObserver
    {
        public bool Changed { get; private set; }
        public IReadOnlyList<FileSelection>? LastSelection { get; private set; }
        public int CallCount { get; private set; }

        public Task OnSelectionChangedAsync(IReadOnlyList<FileSelection> selection, CancellationToken ct = default)
        {
            Changed = true;
            LastSelection = selection;
            CallCount++;
            return Task.CompletedTask;
        }
    }
}






