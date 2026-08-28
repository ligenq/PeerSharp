using CsCheck;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Core;
using PeerSharp.Internals;
using PeerSharp.Internals.Transfers;
using PeerSharp.PiecePicking;

namespace PeerSharp.Tests.Core;

/// <summary>
/// The bookkeeping around the set of pieces currently being downloaded.
/// </summary>
/// <remarks>
/// <para>
/// The manager keeps a running count beside the dictionary it counts, because reading
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}.Count"/> takes every
/// lock in the table and this is read often. Two representations of one fact have to be kept in
/// step, and the count is what decides whether the engine may start another piece.
/// </para>
/// <para>
/// Pieces also own pooled block buffers, so anything that removes a piece from the manager without
/// disposing it loses those buffers for the lifetime of the process.
/// </para>
/// </remarks>
public class PieceStateManagerPropertyTests
{
    private const int PieceCount = 8;

    [Fact]
    public void TheCountAlwaysMatchesTheDictionaryItCounts()
    {
        Operations().Sample(script =>
        {
            using var manager = Create();

            foreach (var operation in script)
            {
                switch (operation.Kind)
                {
                    case 0:
                        var added = new PieceState(operation.Index, 1);
                        if (!manager.TryAddPiece(added))
                        {
                            added.Dispose();
                        }

                        break;

                    case 1:
                        manager.AddOrReplacePiece(new PieceState(operation.Index, 1));
                        break;

                    default:
                        if (manager.TryRemovePiece(operation.Index, out var removed))
                        {
                            removed.Dispose();
                        }

                        break;
                }

                Assert.Equal(manager.ActivePieces.Count, manager.Count);
            }
        }, iter: 5_000);
    }

    [Fact]
    public void TheCountSurvivesConcurrentChurn()
    {
        // Blocks arrive on whichever peer's thread received them, so pieces are started and finished
        // concurrently. A count maintained beside the dictionary has to be right under that, not only
        // when operations happen one at a time.
        Gen.Int[0, PieceCount - 1].Array[8, 40].Sample(indexes =>
        {
            using var manager = Create();

            Parallel.ForEach(indexes, index =>
            {
                manager.AddOrReplacePiece(new PieceState(index, 1));
                if (manager.TryRemovePiece(index, out var removed))
                {
                    removed.Dispose();
                }

                manager.AddOrReplacePiece(new PieceState(index, 1));
            });

            Assert.Equal(manager.ActivePieces.Count, manager.Count);
        }, iter: 500, threads: 1);
    }

    [Fact]
    public void ReplacingAPieceDisposesTheOneItReplaced()
    {
        // The replaced piece is unreachable the moment it leaves the dictionary, so if it is not
        // disposed here its pooled block buffers are never returned.
        Gen.Int[0, PieceCount - 1].Sample(index =>
        {
            using var manager = Create();

            var first = new PieceState(index, 1);
            first.TryAddBlockFromWebSeed(0, new Block(length: 1));
            manager.AddOrReplacePiece(first);

            manager.AddOrReplacePiece(new PieceState(index, 1));

            Assert.All(first.BlockData, block => Assert.Null(block));
        }, iter: 200);
    }

    private static Gen<Operation[]> Operations()
    {
        return Gen.Select(Gen.Int[0, 2], Gen.Int[0, PieceCount - 1])
            .Select(t => new Operation(t.Item1, t.Item2))
            .Array[1, 30];
    }

    private readonly record struct Operation(int Kind, int Index);

    private static PieceStateManager Create()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = 16384;
        metadata.Info.FullSize = 16384L * PieceCount;
        var torrent = TorrentTestUtility.CreateMinimal(metadata);
        var picker = new PiecePicker(new TorrentPiecePickerContext(torrent), TimeProvider.System, Random.Shared);

        return new PieceStateManager(picker, NullLogger<PieceStateManager>.Instance, maxActivePieces: PieceCount);
    }
}
