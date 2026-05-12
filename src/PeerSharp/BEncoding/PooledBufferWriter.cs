using System.Buffers;

namespace PeerSharp.BEncoding;

/// <summary>
/// A buffer writer that uses ArrayPool&lt;byte&gt;.Shared.
/// Bencode output is bounded in size (torrent metadata, DHT messages), so thread-local
/// cache retention from Shared is not a concern here.
/// </summary>
internal sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
{
    private byte[]? _buffer;
    private int _index;

    public PooledBufferWriter(int initialCapacity = 256)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
    }

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _index);
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _index);

    public void Advance(int count)
    {
        _index += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_index);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_index);
    }

    private void EnsureCapacity(int sizeHint)
    {
        int needed = _index + Math.Max(sizeHint, 1);
        if (needed > _buffer!.Length)
        {
            int newSize = Math.Max(_buffer.Length * 2, needed);
            byte[] newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
            _buffer.AsSpan(0, _index).CopyTo(newBuffer);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = newBuffer;
        }
    }

    public void Dispose()
    {
        var buf = Interlocked.Exchange(ref _buffer, null);
        if (buf != null)
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }
}
