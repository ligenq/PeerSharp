using System.Buffers;

namespace PeerSharp.Core;

/// <summary>
/// Represents a block of data from a piece, using pooled memory.
/// Thread-safe for disposal. Immutable after construction.
/// </summary>
internal sealed class Block : IDisposable
{
    /// <summary>
    /// Dedicated pool for block buffers, capped to prevent unbounded memory growth.
    /// 4096 arrays x 16KB = 64MB max pool retention vs unbounded with ArrayPool.Shared.
    /// The higher cap (vs. 256) reduces GC pressure during active multi-peer downloads
    /// where hundreds of 16KB blocks are allocated and freed per second.
    /// </summary>
    private static readonly ArrayPool<byte> BlockPool =
        ArrayPool<byte>.Create(maxArrayLength: 16384, maxArraysPerBucket: 4096);

    private byte[]? _buffer;
    private AtomicDisposal _disposal = new();

    public Block(int length)
    {
        Length = length;
        _buffer = BlockPool.Rent(length);
    }

    public Block(int pieceIndex, int offset, int length) : this(length)
    {
        PieceIndex = pieceIndex;
        Offset = offset;
    }

    public byte[] Buffer
    {
        get
        {
            var buf = _buffer;
            if (buf == null)
            {
                _disposal.ThrowIfDisposed(this);
                throw new ObjectDisposedException(nameof(Block));
            }
            return buf;
        }
    }

    public ReadOnlyMemory<byte> Data
    {
        get
        {
            var buf = _buffer;
            if (buf == null)
            {
                return ReadOnlyMemory<byte>.Empty;
            }
            return new ReadOnlyMemory<byte>(buf, 0, Length);
        }
    }

    public int Length { get; }
    public int Offset { get; }
    public int PieceIndex { get; }

    public void Dispose()
    {
        if (_disposal.MarkDisposed())
        {
            var buf = Interlocked.Exchange(ref _buffer, null);
            if (buf != null)
            {
                BlockPool.Return(buf, clearArray: true);
            }
        }
    }
}

internal sealed class BlockRequest
{
    public int Attempts { get; init; }
    public int Length { get; init; }
    public int Offset { get; init; }
    public int PieceIndex { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.MinValue;
}
