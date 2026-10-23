using System.Text.Json;
using PeerSharp.PieceWriter;

namespace PeerSharp.Internals;

// Stopped torrents retain block flags and offsets, with their partial data in a private temporary
// file. The file is independent of session snapshot directories, which a later save may replace.
internal sealed class DeferredPieceStore : IDisposable
{
    private readonly FileStream _stream;
    private readonly Entry[] _entries;
    private readonly Lock _lifetimeLock = new();
    private int _references = 1;
    private AtomicDisposal _disposal = new();

    private DeferredPieceStore(FileStream stream, Entry[] entries)
    {
        _stream = stream;
        _entries = entries;
    }

    // "directory" places the private file somewhere other than the system temporary directory.
    // Only the owning transfer passes it, so a test can aim one store at an unusable directory to
    // exercise the failure path without redirecting the temporary directory for the whole process,
    // which every concurrently running test that creates a temporary file would read.
    internal static DeferredPieceStore Create(IEnumerable<TorrentStateData.UnfinishedPieceData> pieces,
        string? directory = null)
    {
        string path = Path.Combine(directory ?? Path.GetTempPath(),
            "PeerSharpResume-" + Guid.NewGuid().ToString("N") + ".tmp");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.Read | FileShare.Delete,
            BufferSize = 1,
            Options = FileOptions.RandomAccess
        };

        if (OperatingSystem.IsWindows())
        {
            options.Options |= FileOptions.DeleteOnClose;
        }
        else
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var stream = new FileStream(path, options);
        bool namedFile = !OperatingSystem.IsWindows();
        try
        {
            // Unix DeleteOnClose is implemented by managed disposal. Unlink now so an abrupt
            // process exit cannot leave partial download data behind; the open handle stays usable.
            if (namedFile)
            {
                File.Delete(path);
                namedFile = false;
            }
            var entries = new Dictionary<int, Entry>();
            foreach (var piece in pieces)
            {
                long offset = stream.Position;
                stream.Write(piece.Data);
                bool[] blocks = (bool[])piece.Blocks.Clone();
                entries[piece.Index] = new Entry(piece.Index, blocks, offset, piece.Data.Length, blocks.Count(static block => block));
            }
            // Flags never change. Sort once rather than recounting and sorting on every autosave.
            return new DeferredPieceStore(stream, entries.Values.OrderByDescending(static e => e.ReceivedBlocks).ToArray());
        }
        catch
        {
            stream.Dispose();
            if (namedFile)
            {
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Preserve the creation failure. */ }
            }
            throw;
        }
    }

    internal (List<TorrentStateData.UnfinishedPieceData> Pieces, IDisposable? Lease) Capture(bool streamed,
        int maxPieces = int.MaxValue, long maxBytes = long.MaxValue, Func<int, bool>? includePiece = null)
    {
        lock (_lifetimeLock)
        {
            _disposal.ThrowIfDisposed(this);
            var pieces = new List<TorrentStateData.UnfinishedPieceData>(_entries.Length);
            long budget = maxBytes;
            foreach (var entry in _entries)
            {
                if (includePiece != null && !includePiece(entry.Index))
                {
                    continue;
                }

                if (pieces.Count >= maxPieces || budget <= 0 || (entry.Length > budget && pieces.Count > 0))
                {
                    break;
                }

                budget -= entry.Length;
                var slice = new Slice(this, entry.Offset, entry.Length);
                pieces.Add(new TorrentStateData.UnfinishedPieceData
                {
                    Index = entry.Index,
                    Blocks = (bool[])entry.Blocks.Clone(),
                    Data = streamed ? [] : slice.Read(),
                    DeferredData = streamed ? slice : null
                });
            }
            if (!streamed)
            {
                return (pieces, null);
            }

            Interlocked.Increment(ref _references);
            return (pieces, new Lease(this));
        }
    }

    internal long GetReceivedBytes(long start, long length, uint pieceSize, Func<int, bool>? includePiece = null)
    {
        long end = checked(start + length);
        long bytes = 0;
        foreach (var entry in _entries)
        {
            if (includePiece != null && !includePiece(entry.Index))
            {
                continue;
            }

            long pieceStart = (long)entry.Index * pieceSize;
            for (int i = 0; i < entry.Blocks.Length; i++)
            {
                if (!entry.Blocks[i])
                {
                    continue;
                }

                long blockStart = pieceStart + (long)i * ProtocolConstants.BlockSize;
                long blockEnd = Math.Min(blockStart + ProtocolConstants.BlockSize, pieceStart + entry.Length);
                bytes += Math.Max(0, Math.Min(end, blockEnd) - Math.Max(start, blockStart));
            }
        }
        return bytes;
    }

    public void Dispose()
    {
        lock (_lifetimeLock)
        {
            if (_disposal.MarkDisposed())
            {
                Release();
            }
        }
    }

    private void Release()
    {
        if (Interlocked.Decrement(ref _references) == 0)
        {
            _stream.Dispose();
        }
    }

    private void ReadExactly(Span<byte> destination, long offset)
    {
        while (!destination.IsEmpty)
        {
            int read = RandomAccess.Read(_stream.SafeFileHandle, destination, offset);
            if (read == 0)
            {
                throw new EndOfStreamException("Deferred resume data was truncated.");
            }

            offset += read;
            destination = destination[read..];
        }
    }

    internal readonly record struct Slice(DeferredPieceStore Store, long Offset, int Length)
    {
        internal byte[] Read()
        {
            var data = GC.AllocateUninitializedArray<byte>(Length);
            Store.ReadExactly(data, Offset);
            return data;
        }

        internal void WriteBase64(Utf8JsonWriter writer, CancellationToken cancellationToken = default)
        {
            Span<byte> buffer = stackalloc byte[ResumePieceJsonConverter.ChunkSize];
            int remaining = Length;
            long offset = Offset;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(remaining, buffer.Length);
                Store.ReadExactly(buffer[..count], offset);
                offset += count;
                remaining -= count;
                writer.WriteBase64StringSegment(buffer[..count], isFinalSegment: remaining == 0);
                writer.Flush();
            } while (remaining > 0);
        }
    }

    private sealed record Entry(int Index, bool[] Blocks, long Offset, int Length, int ReceivedBlocks);

    private sealed class Lease(DeferredPieceStore store) : IDisposable
    {
        private readonly DeferredPieceStore _store = store;
        private AtomicDisposal _disposal = new();
        public void Dispose()
        {
            if (_disposal.MarkDisposed())
            {
                _store.Release();
            }
        }
    }
}
