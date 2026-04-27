namespace PeerSharp.Core;

/// <summary>
/// Thread-safe bitfield tracking piece completion.
/// Uses lock-free operations for individual piece updates.
/// </summary>
internal sealed class PiecesProgress
{
    private readonly int[] _pieces;
    private int _hasAll;
    private int _receivedCount;
    // 0 = false, 1 = true (atomic)

    public PiecesProgress(int piecesCount)
    {
        Count = piecesCount;
        _pieces = new int[(piecesCount + 31) / 32];
    }

    public int Count { get; }

    public bool IsFull => Interlocked.CompareExchange(ref _hasAll, 0, 0) == 1 || ReceivedCount == Count;

    public float Progress => Count == 0 ? 0f : (float)ReceivedCount / Count;

    public int ReceivedCount
    {
        get
        {
            if (Interlocked.CompareExchange(ref _hasAll, 0, 0) == 1)
            {
                return Count;
            }
            return Interlocked.CompareExchange(ref _receivedCount, 0, 0);
        }
    }

    public void AddPiece(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _hasAll, 0, 0) == 1)
        {
            return;
        }

        int arrayIdx = index >> 5; // index / 32
        int mask = 1 << (index & 31); // index % 32

        int oldVal, newVal;
        do
        {
            oldVal = Interlocked.CompareExchange(ref _pieces[arrayIdx], 0, 0);
            if ((oldVal & mask) != 0)
            {
                return; // Already set
            }
            newVal = oldVal | mask;
        }
        while (Interlocked.CompareExchange(ref _pieces[arrayIdx], newVal, oldVal) != oldVal);

        int newReceived = Interlocked.Increment(ref _receivedCount);
        if (newReceived == Count)
        {
            Interlocked.Exchange(ref _hasAll, 1);
        }
    }

    /// <summary>
    /// Load pieces from bitfield. Should only be called during initialization
    /// before any concurrent access.
    /// </summary>
    public void FromBitfield(ReadOnlySpan<byte> data)
    {
        // Check if all bits are set (O(N/8) check)
        bool allSet = true;
        int expectedBytes = (Count + 7) / 8;
        int bytesToCheck = Math.Min(data.Length, expectedBytes);

        for (int i = 0; i < bytesToCheck; i++)
        {
            byte expected = 0xFF;
            if (i == expectedBytes - 1)
            {
                // Mask spare bits for last byte
                int spareBits = 8 - (Count % 8);
                if (spareBits < 8)
                {
                    expected = (byte)(0xFF << spareBits);
                }
            }

            if ((data[i] & expected) != expected)
            {
                allSet = false;
                break;
            }
        }

        if (allSet && bytesToCheck == expectedBytes)
        {
            SetHaveAll();
            return;
        }

        int totalSet = 0;
        Interlocked.Exchange(ref _hasAll, 0);
        Array.Clear(_pieces, 0, _pieces.Length);

        // Process 4 bytes (32 bits) at a time to match internal storage
        int fullInts = Math.Min(_pieces.Length, data.Length / 4);
        for (int i = 0; i < fullInts; i++)
        {
            int byteOffset = i * 4;
            int val = 0;

            for (int b = 0; b < 4; b++)
            {
                byte dataByte = data[byteOffset + b];
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((dataByte & (1 << (7 - bit))) != 0)
                    {
                        int pieceIdx = (i * 32) + (b * 8) + bit;
                        if (pieceIdx < Count)
                        {
                            val |= 1 << ((b * 8) + bit);
                            totalSet++;
                        }
                    }
                }
            }

            // Use Interlocked for thread-safety even during init
            Interlocked.Exchange(ref _pieces[i], val);
        }

        // Handle remaining bytes
        int remainingByteStart = fullInts * 4;
        for (int byteIdx = remainingByteStart; byteIdx < data.Length; byteIdx++)
        {
            byte dataByte = data[byteIdx];
            for (int bit = 0; bit < 8; bit++)
            {
                int pieceIdx = (byteIdx * 8) + bit;
                if (pieceIdx >= Count)
                {
                    break;
                }

                if ((dataByte & (1 << (7 - bit))) != 0)
                {
                    int arrayIdx = pieceIdx >> 5;
                    int mask = 1 << (pieceIdx & 31);

                    int oldVal, newVal;
                    do
                    {
                        oldVal = Interlocked.CompareExchange(ref _pieces[arrayIdx], 0, 0);
                        newVal = oldVal | mask;
                    }
                    while (Interlocked.CompareExchange(ref _pieces[arrayIdx], newVal, oldVal) != oldVal);

                    totalSet++;
                }
            }
        }

        Interlocked.Exchange(ref _receivedCount, totalSet);
        if (totalSet == Count)
        {
            Interlocked.Exchange(ref _hasAll, 1);
        }
    }

    public bool HasPiece(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref _hasAll, 0, 0) == 1)
        {
            return true;
        }

        int arrayIdx = index >> 5;
        int mask = 1 << (index & 31);

        // Atomic read for memory visibility
        int val = Interlocked.CompareExchange(ref _pieces[arrayIdx], 0, 0);
        return (val & mask) != 0;
    }

    public void SetHaveAll()
    {
        Interlocked.Exchange(ref _hasAll, 1);
        Interlocked.Exchange(ref _receivedCount, Count);
    }

    public void SetHaveNone()
    {
        Interlocked.Exchange(ref _hasAll, 0);
        Interlocked.Exchange(ref _receivedCount, 0);
        Array.Clear(_pieces, 0, _pieces.Length);
    }

    /// <summary>
    /// Creates a bitfield snapshot. Thread-safe but may see concurrent modifications.
    /// </summary>
    public byte[] ToBitfield()
    {
        int bytes = (Count + 7) / 8;
        byte[] buffer = new byte[bytes];

        if (Interlocked.CompareExchange(ref _hasAll, 0, 0) == 1)
        {
            Array.Fill(buffer, (byte)0xFF);
            // Mask spare bits for last byte if necessary
            int spareBits = 8 - (Count % 8);
            if (spareBits < 8)
            {
                buffer[bytes - 1] = (byte)(0xFF << spareBits);
            }
            return buffer;
        }

        for (int i = 0; i < Count; i++)
        {
            int arrayIdx = i >> 5;
            int mask = 1 << (i & 31);

            // Atomic read
            int val = Interlocked.CompareExchange(ref _pieces[arrayIdx], 0, 0);
            if ((val & mask) != 0)
            {
                int byteIdx = i / 8;
                int bitIdx = 7 - (i % 8);
                buffer[byteIdx] |= (byte)(1 << bitIdx);
            }
        }
        return buffer;
    }
}
