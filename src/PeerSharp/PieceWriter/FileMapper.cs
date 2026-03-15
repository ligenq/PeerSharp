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
        _fileSizes = fileSizes.ToArray();
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
    public IEnumerable<(int FileIndex, long FileOffset, int Length, int BufferOffset)> MapRange(long globalOffset, int length)
    {
        long current = globalOffset;
        int remaining = length;
        int bufferOffset = 0;

        while (remaining > 0)
        {
            var (fileIdx, fileOffset) = MapOffset(current);

            // Bounds check
            if (fileIdx >= _fileSizes.Length)
            {
                yield break;
            }

            long fileSize = _fileSizes[fileIdx];
            long spaceLeft = fileSize - fileOffset;
            int chunk = (int)Math.Min(remaining, spaceLeft);

            if (chunk <= 0)
            {
                yield break;
            }

            yield return (fileIdx, fileOffset, chunk, bufferOffset);

            remaining -= chunk;
            current += chunk;
            bufferOffset += chunk;
        }
    }
}
