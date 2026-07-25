namespace PeerSharp.PieceWriter;

/// <summary>
/// Handles mapping of global torrent offsets to specific files and file offsets.
/// Immutable and thread-safe.
/// </summary>
internal class FileMapper
{
    private readonly long[] _cumulativeOffsets;
    private readonly long[] _fileSizes;

    public FileMapper(IReadOnlyList<long> fileSizes)
    {
        _fileSizes = [.. fileSizes];
        _cumulativeOffsets = new long[_fileSizes.Length + 1];

        long offset = 0;
        for (int i = 0; i < _fileSizes.Length; i++)
        {
            _cumulativeOffsets[i] = offset;
            offset += _fileSizes[i];
        }
        _cumulativeOffsets[_fileSizes.Length] = offset;
        TotalSize = offset;
    }

    public int FileCount => _fileSizes.Length;
    public long TotalSize { get; }

    /// <summary>
    /// Resolves a global offset to a file index and offset within that file.
    /// Uses binary search for O(log N) performance.
    /// </summary>
    public (int FileIndex, long FileOffset) MapOffset(long globalOffset)
    {
        // Binary search for O(log N) performance with large file lists
        int idx = Array.BinarySearch(_cumulativeOffsets, globalOffset);

        if (idx >= 0)
        {
            // Exact match (start of a file)
            // If it's the very last offset (total size), clamp to the last file
            if (idx >= _fileSizes.Length)
            {
                return (_fileSizes.Length - 1, _fileSizes[^1]);
            }

            return (idx, 0);
        }

        // Not found: ~idx is the index of the first element LARGER than globalOffset.
        // So the file we want is at (~idx) - 1.
        int fileIndex = (~idx) - 1;

        // Safety clamps
        if (fileIndex < 0)
        {
            fileIndex = 0;
        }

        if (fileIndex >= _fileSizes.Length)
        {
            fileIndex = _fileSizes.Length - 1;
        }

        return (fileIndex, globalOffset - _cumulativeOffsets[fileIndex]);
    }

    /// <summary>
    /// Maps a global range (offset + length) to a sequence of file operations.
    /// </summary>
    /// <remarks>
    /// Returns a struct enumerable rather than an <see cref="IEnumerable{T}"/>, so a foreach over
    /// it allocates nothing. Every Storage read and write enters here once per 16 KiB block, and a
    /// compiler-generated iterator was costing an allocation per call on that path - for laziness
    /// no caller wants, since they all drain the result into a list immediately.
    /// </remarks>
    public RangeEnumerable MapRange(long globalOffset, int length) => new(this, globalOffset, length);

    /// <summary>Allocation-free enumerable over the file operations covering a global range.</summary>
    public readonly struct RangeEnumerable(FileMapper mapper, long globalOffset, int length)
    {
        public RangeEnumerator GetEnumerator() => new(mapper, globalOffset, length);

        /// <summary>
        /// Materialises the operations. For callers that want a list anyway - LINQ is unavailable
        /// here because the type deliberately does not implement <see cref="IEnumerable{T}"/>,
        /// which is what keeps foreach allocation-free.
        /// </summary>
        public List<(int FileIndex, long FileOffset, int Length, int BufferOffset)> ToList()
        {
            var results = new List<(int FileIndex, long FileOffset, int Length, int BufferOffset)>();
            foreach (var operation in this)
            {
                results.Add(operation);
            }
            return results;
        }
    }

    /// <summary>
    /// Walks a global range forward, yielding one operation per file it touches. Indices are
    /// produced strictly ascending and each file appears at most once, which is what lets Storage
    /// take its per-file locks in a deadlock-free order without sorting.
    /// </summary>
    public struct RangeEnumerator(FileMapper mapper, long globalOffset, int length)
    {
        private long _current = globalOffset;
        private int _remaining = length;
        private int _bufferOffset;

        public (int FileIndex, long FileOffset, int Length, int BufferOffset) Current { get; private set; }

        public bool MoveNext()
        {
            if (_remaining <= 0)
            {
                return false;
            }

            var (fileIdx, fileOffset) = mapper.MapOffset(_current);

            // Bounds check
            if (fileIdx >= mapper._fileSizes.Length)
            {
                return false;
            }

            long fileSize = mapper._fileSizes[fileIdx];
            long spaceLeft = fileSize - fileOffset;
            int chunk = (int)Math.Min(_remaining, spaceLeft);

            if (chunk <= 0)
            {
                return false;
            }

            Current = (fileIdx, fileOffset, chunk, _bufferOffset);

            _remaining -= chunk;
            _current += chunk;
            _bufferOffset += chunk;
            return true;
        }
    }
}
