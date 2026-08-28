using CsCheck;
using PeerSharp.PieceWriter;

namespace PeerSharp.Tests.Core.IO;

/// <summary>
/// The invariants <see cref="FileMapper.MapRange"/> has to hold for every range, rather than for the
/// handful a person thinks to write down.
/// </summary>
/// <remarks>
/// <para>
/// This is the arithmetic that decides which bytes of a multi-file torrent land in which file. It is
/// wrong in ways that example tests are badly placed to find: a range is split at every file
/// boundary it crosses, so the interesting cases are combinations of file sizes and offsets - a
/// block starting one byte before a boundary, a block spanning a whole small file, a block ending
/// exactly on the end of one. Getting it wrong writes real bytes to the wrong place, and piece
/// verification only runs once, when the piece arrives.
/// </para>
/// <para>
/// <see cref="Storage"/> also relies on the ordering documented on the enumerator - ascending file
/// indexes, each file at most once - to take its per-file locks without sorting and without
/// deadlocking, so that ordering is asserted here as a property and not assumed.
/// </para>
/// </remarks>
public class FileMapperPropertyTests
{
    /// <summary>
    /// Torrents with many small files are where boundary splitting actually happens, so the sizes
    /// are kept small and the counts high rather than the reverse.
    /// </summary>
    /// <remarks>
    /// Zero is included deliberately. Torrents may contain empty files, nothing filters them out
    /// before <see cref="FileMapper"/> sees them, and an empty file shares a cumulative offset with
    /// its neighbour - which is precisely the case these properties first failed on. Only the
    /// all-empty torrent is excluded, because it has no byte to address.
    /// </remarks>
    private static readonly Gen<long[]> FileSizes =
        Gen.Long[0, 500].Array[1, 12].Where(sizes => sizes.Sum() > 0);

    [Fact]
    public void EveryRangeIsCoveredExactlyOnce()
    {
        RangesIn(FileSizes).Sample((sizes, offset, length) =>
        {
            var mapper = new FileMapper(sizes);
            var operations = mapper.MapRange(offset, length).ToList();

            // Nothing dropped and nothing written twice.
            Assert.Equal(length, operations.Sum(operation => operation.Length));

            // The buffer is consumed front to back with no gaps, so a caller copying each chunk to
            // its BufferOffset reassembles precisely the range it asked for.
            int expectedBufferOffset = 0;
            long expectedGlobal = offset;
            foreach (var operation in operations)
            {
                Assert.Equal(expectedBufferOffset, operation.BufferOffset);
                Assert.True(operation.Length > 0, "an operation covered no bytes");

                // Each chunk lands where the global range says it should.
                Assert.Equal(expectedGlobal, GlobalOffsetOf(sizes, operation.FileIndex, operation.FileOffset));

                expectedBufferOffset += operation.Length;
                expectedGlobal += operation.Length;
            }

            Assert.Equal(length, expectedBufferOffset);
        }, iter: 10_000);
    }

    [Fact]
    public void NoOperationReachesOutsideItsFile()
    {
        RangesIn(FileSizes).Sample((sizes, offset, length) =>
        {
            var mapper = new FileMapper(sizes);

            foreach (var operation in mapper.MapRange(offset, length).ToList())
            {
                Assert.InRange(operation.FileIndex, 0, sizes.Length - 1);
                Assert.InRange(operation.FileOffset, 0, sizes[operation.FileIndex]);
                Assert.True(
                    operation.FileOffset + operation.Length <= sizes[operation.FileIndex],
                    $"operation ran {operation.FileOffset + operation.Length - sizes[operation.FileIndex]} bytes past the end of file {operation.FileIndex}");
            }
        }, iter: 10_000);
    }

    [Fact]
    public void FilesAreVisitedInAscendingOrderAndOnlyOnce()
    {
        // Storage takes one lock per file in the order they arrive here. Repeats or a step
        // backwards would be a lock-ordering bug rather than merely surprising output.
        RangesIn(FileSizes).Sample((sizes, offset, length) =>
        {
            var mapper = new FileMapper(sizes);
            var indexes = mapper.MapRange(offset, length).ToList().Select(operation => operation.FileIndex).ToArray();

            for (int i = 1; i < indexes.Length; i++)
            {
                Assert.True(indexes[i] > indexes[i - 1], $"file indexes were not ascending: {string.Join(", ", indexes)}");
            }
        }, iter: 10_000);
    }

    [Fact]
    public void MapOffsetAgreesWithTheStartOfTheRange()
    {
        RangesIn(FileSizes).Sample((sizes, offset, length) =>
        {
            var mapper = new FileMapper(sizes);
            var first = mapper.MapRange(offset, length).ToList()[0];
            var (fileIndex, fileOffset) = mapper.MapOffset(offset);

            Assert.Equal(fileIndex, first.FileIndex);
            Assert.Equal(fileOffset, first.FileOffset);
        }, iter: 10_000);
    }

    [Fact]
    public void TotalSizeIsTheSumOfTheFiles()
    {
        FileSizes.Sample(sizes =>
        {
            var mapper = new FileMapper(sizes);

            Assert.Equal(sizes.Sum(), mapper.TotalSize);
            Assert.Equal(sizes.Length, mapper.FileCount);
        }, iter: 1_000);
    }

    [Fact]
    public void ARangeRunningToTheEndOfTheTorrentIsStillComplete()
    {
        // The last file has no successor to spill into, so an off-by-one here truncates the tail of
        // every torrent rather than misplacing bytes in the middle of one.
        FileSizes.SelectMany(sizes =>
        {
            long total = sizes.Sum();
            return Gen.Long[0, total - 1].Select(offset => (sizes, offset, (int)(total - offset)));
        }).Sample((sizes, offset, length) =>
        {
            var mapper = new FileMapper(sizes);
            var operations = mapper.MapRange(offset, length).ToList();

            Assert.Equal(length, operations.Sum(operation => operation.Length));

            // The torrent may end in empty files, which hold no byte and so end no range. The last
            // file with bytes in it is the one that has to be filled to its end.
            int lastFileWithBytes = Array.FindLastIndex(sizes, size => size > 0);
            var last = operations[^1];
            Assert.Equal(lastFileWithBytes, last.FileIndex);
            Assert.Equal(sizes[lastFileWithBytes], last.FileOffset + last.Length);
        }, iter: 5_000);
    }


    /// <summary>
    /// Sizes paired with a range that lies wholly inside them.
    /// </summary>
    private static Gen<(long[] Sizes, long Offset, int Length)> RangesIn(Gen<long[]> sizes)
    {
        return sizes.SelectMany(generated =>
        {
            long total = generated.Sum();
            return Gen.Long[0, total - 1].SelectMany(offset =>
                Gen.Int[1, (int)(total - offset)].Select(length => (generated, offset, length)));
        });
    }

    /// <summary>
    /// Where a file-relative position sits in the torrent, computed independently of the mapper.
    /// </summary>
    private static long GlobalOffsetOf(long[] sizes, int fileIndex, long fileOffset)
    {
        long global = fileOffset;
        for (int i = 0; i < fileIndex; i++)
        {
            global += sizes[i];
        }

        return global;
    }
}
