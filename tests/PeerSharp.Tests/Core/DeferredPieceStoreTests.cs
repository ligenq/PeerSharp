using System.Text.Json;
using PeerSharp.Internals;
using PeerSharp.PieceWriter;

namespace PeerSharp.Tests.Core;

public sealed class DeferredPieceStoreTests
{
    [Fact]
    public void CapturingWhileDisposingEitherFailsOrReturnsAnIntactLeasedSnapshot()
    {
        for (int i = 0; i < 50; i++)
        {
            byte[] bytes = [1, 2, 3];
            var store = DeferredPieceStore.Create([new() { Index = 0, Blocks = [true], Data = bytes }]);
            Parallel.Invoke(() =>
            {
                (List<TorrentStateData.UnfinishedPieceData> Pieces, IDisposable? Lease) captured;
                try
                {
                    captured = store.Capture(streamed: true);
                }
                catch (ObjectDisposedException) { return; /* Disposal won before a lease could be acquired. */ }
                using var lease = captured.Lease;
                Assert.Equal(bytes, Assert.Single(captured.Pieces).DeferredData!.Value.Read());
            }, store.Dispose);
        }
    }

    [Fact]
    public void SnapshotLeaseKeepsDataReadableAfterTheStoppedTransferIsDisposed()
    {
        byte[] data = [1, 2, 3, 4, 5];
        var store = DeferredPieceStore.Create([new() { Index = 2, Blocks = [true], Data = data }]);
        var capture = store.Capture(streamed: true);
        store.Dispose();
        store.Dispose();
        try
        {
            var piece = Assert.Single(capture.Pieces);
            Assert.Empty(piece.Data);
            Assert.Equal(data, piece.DeferredData!.Value.Read());
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output)) piece.DeferredData.Value.WriteBase64(writer);
            using var json = JsonDocument.Parse(output.ToArray());
            Assert.Equal(data, json.RootElement.GetBytesFromBase64());
        }
        finally { capture.Lease!.Dispose(); }
        capture.Lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => capture.Pieces[0].DeferredData!.Value.Read());
    }

    [Fact]
    public void ProgressCountsReceivedRangesAndClipsTheLastBlockAndFileBoundaries()
    {
        using var store = DeferredPieceStore.Create([
            new() { Index = 0, Blocks = [true, false], Data = new byte[32768] },
            new() { Index = 1, Blocks = [false, true], Data = new byte[17000] }
        ]);

        Assert.Equal(16384 + 616, store.GetReceivedBytes(0, 49768, 32768));
        Assert.Equal(100, store.GetReceivedBytes(49668, 100, 32768));
        Assert.Equal(16384, store.GetReceivedBytes(0, 49768, 32768, index => index == 0));
        Assert.Equal(0, store.GetReceivedBytes(20000, 1000, 32768));
    }

    [Fact]
    public void ExportLimitsKeepTheMostCompletePiecesWithoutLimitingRestoration()
    {
        using var store = DeferredPieceStore.Create([
            new() { Index = 0, Blocks = [true, false], Data = new byte[32768] },
            new() { Index = 1, Blocks = [true, true], Data = new byte[32768] }
        ]);
        var limited = store.Capture(streamed: false, maxPieces: 1, maxBytes: 32768);
        Assert.Equal(1, Assert.Single(limited.Pieces).Index);
        Assert.Equal(2, store.Capture(streamed: false).Pieces.Count);
    }
}
