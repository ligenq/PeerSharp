namespace PeerSharp.WebTorrent.Network;

internal sealed class TrackerMessageTooLargeException : IOException
{
    public TrackerMessageTooLargeException(int maxMessageBytes)
        : base($"WebTorrent tracker message exceeded the configured limit of {maxMessageBytes} bytes.")
    {
    }
}

internal sealed class CappedMemoryStream : MemoryStream
{
    private readonly int _maxLength;

    public CappedMemoryStream(int maxLength)
    {
        _maxLength = Math.Max(1, maxLength);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        EnsureWithinLimit(count);
        base.Write(buffer, offset, count);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureWithinLimit(buffer.Length);
        return base.WriteAsync(buffer, cancellationToken);
    }

    private void EnsureWithinLimit(int count)
    {
        if (Length + count > _maxLength)
        {
            throw new TrackerMessageTooLargeException(_maxLength);
        }
    }
}
