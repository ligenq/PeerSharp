using System.Runtime.CompilerServices;

namespace PeerSharp.Internals.Utilities;

internal sealed class RC4
{
    private readonly byte[] _s = new byte[256];
    private int _x;
    private int _y;

    public RC4 Clone()
    {
        var c = new RC4();
        _s.AsSpan().CopyTo(c._s);
        c._x = _x;
        c._y = _y;
        return c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Decrypt(byte[] buffer, int offset, int count)
    {
        Process(buffer.AsSpan(offset, count));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Decrypt(Span<byte> buffer)
    {
        Process(buffer);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Encrypt(byte[] buffer, int offset, int count)
    {
        Process(buffer.AsSpan(offset, count));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Encrypt(Span<byte> buffer)
    {
        Process(buffer);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Init(ReadOnlySpan<byte> key)
    {
        int keyLen = key.Length;
        ref byte s0 = ref _s[0];

        // Initialize S-box with identity permutation
        for (int i = 0; i < 256; i++)
        {
            Unsafe.Add(ref s0, i) = (byte)i;
        }

        // Key scheduling algorithm (KSA) - no temporary array allocation
        int j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (j + Unsafe.Add(ref s0, i) + key[i % keyLen]) & 255;

            // Inline swap
            byte tmp = Unsafe.Add(ref s0, i);
            Unsafe.Add(ref s0, i) = Unsafe.Add(ref s0, j);
            Unsafe.Add(ref s0, j) = tmp;
        }

        _x = 0;
        _y = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Init(byte[] key)
    {
        Init(key.AsSpan());
    }

    public void Restore(RC4 source)
    {
        source._s.AsSpan().CopyTo(_s);
        _x = source._x;
        _y = source._y;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Skip(int count)
    {
        if (count <= 0)
        {
            return;
        }

        ref byte s0 = ref _s[0];
        int x = _x;
        int y = _y;

        // Process 4 iterations at a time (loop unrolling)
        int i = 0;
        int unrolledEnd = count - 3;
        while (i < unrolledEnd)
        {
            // Iteration 1
            x = (x + 1) & 255;
            byte sx = Unsafe.Add(ref s0, x);
            y = (y + sx) & 255;
            byte sy = Unsafe.Add(ref s0, y);
            Unsafe.Add(ref s0, x) = sy;
            Unsafe.Add(ref s0, y) = sx;

            // Iteration 2
            x = (x + 1) & 255;
            sx = Unsafe.Add(ref s0, x);
            y = (y + sx) & 255;
            sy = Unsafe.Add(ref s0, y);
            Unsafe.Add(ref s0, x) = sy;
            Unsafe.Add(ref s0, y) = sx;

            // Iteration 3
            x = (x + 1) & 255;
            sx = Unsafe.Add(ref s0, x);
            y = (y + sx) & 255;
            sy = Unsafe.Add(ref s0, y);
            Unsafe.Add(ref s0, x) = sy;
            Unsafe.Add(ref s0, y) = sx;

            // Iteration 4
            x = (x + 1) & 255;
            sx = Unsafe.Add(ref s0, x);
            y = (y + sx) & 255;
            sy = Unsafe.Add(ref s0, y);
            Unsafe.Add(ref s0, x) = sy;
            Unsafe.Add(ref s0, y) = sx;

            i += 4;
        }

        // Handle remaining iterations
        while (i < count)
        {
            x = (x + 1) & 255;
            byte sx = Unsafe.Add(ref s0, x);
            y = (y + sx) & 255;
            byte sy = Unsafe.Add(ref s0, y);
            Unsafe.Add(ref s0, x) = sy;
            Unsafe.Add(ref s0, y) = sx;
            i++;
        }

        _x = x;
        _y = y;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    private void Process(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        ref byte s0 = ref _s[0];
        ref byte b0 = ref buffer[0];
        int x = _x;
        int y = _y;
        int len = buffer.Length;

        // Process 4 bytes at a time (loop unrolling)
        int i = 0;
        int unrolledEnd = len - 3;
        while (i < unrolledEnd)
        {
            // Iteration 1
            x = (x + 1) & 255;
            byte sx = Unsafe.Add(ref s0, x);
            y = (y + sx) & 255;
            byte sy = Unsafe.Add(ref s0, y);
            Unsafe.Add(ref s0, x) = sy;
            Unsafe.Add(ref s0, y) = sx;
            Unsafe.Add(ref b0, i) ^= Unsafe.Add(ref s0, (sx + sy) & 255);

            // Iteration 2
            x = (x + 1) & 255;
            sx = Unsafe.Add(ref s0, x);
            y = (y + sx) & 255;
            sy = Unsafe.Add(ref s0, y);
            Unsafe.Add(ref s0, x) = sy;
            Unsafe.Add(ref s0, y) = sx;
            Unsafe.Add(ref b0, i + 1) ^= Unsafe.Add(ref s0, (sx + sy) & 255);

            // Iteration 3
            x = (x + 1) & 255;
            sx = Unsafe.Add(ref s0, x);
            y = (y + sx) & 255;
            sy = Unsafe.Add(ref s0, y);
            Unsafe.Add(ref s0, x) = sy;
            Unsafe.Add(ref s0, y) = sx;
            Unsafe.Add(ref b0, i + 2) ^= Unsafe.Add(ref s0, (sx + sy) & 255);

            // Iteration 4
            x = (x + 1) & 255;
            sx = Unsafe.Add(ref s0, x);
            y = (y + sx) & 255;
            sy = Unsafe.Add(ref s0, y);
            Unsafe.Add(ref s0, x) = sy;
            Unsafe.Add(ref s0, y) = sx;
            Unsafe.Add(ref b0, i + 3) ^= Unsafe.Add(ref s0, (sx + sy) & 255);

            i += 4;
        }

        // Handle remaining bytes
        while (i < len)
        {
            x = (x + 1) & 255;
            byte sx = Unsafe.Add(ref s0, x);
            y = (y + sx) & 255;
            byte sy = Unsafe.Add(ref s0, y);
            Unsafe.Add(ref s0, x) = sy;
            Unsafe.Add(ref s0, y) = sx;
            Unsafe.Add(ref b0, i) ^= Unsafe.Add(ref s0, (sx + sy) & 255);
            i++;
        }

        _x = x;
        _y = y;
    }
}
