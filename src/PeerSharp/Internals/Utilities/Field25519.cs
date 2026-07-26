using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace PeerSharp.Internals.Utilities;

/// <summary>
/// Arithmetic in the prime field GF(2^255 - 19), the field Ed25519 curve points live in.
///
/// Elements are held as five 51-bit limbs in <see cref="ulong"/>, so a value is
/// <c>l0 + l1*2^51 + l2*2^102 + l3*2^153 + l4*2^204</c>. Limbs are allowed to exceed 51 bits
/// between operations and are only carried when a multiplication needs them small again, which
/// keeps addition and subtraction to a handful of instructions.
///
/// <para>
/// This replaces a <see cref="System.Numerics.BigInteger"/> implementation. BigInteger is the
/// wrong shape for the job: it is arbitrary-precision and heap-allocating, while this field is a
/// fixed 255 bits, so every operation paid for allocation and generality it could not use. The
/// limb representation is the standard one, and <see cref="UInt128"/> - available since .NET 7 -
/// carries the double-width products without the manual carry splitting that makes C
/// implementations of this hard to read.
/// </para>
///
/// <para>
/// The BigInteger version is retained as a test oracle: <c>Field25519Tests</c> checks every
/// operation here against it over random inputs and edge values, which is what makes hand-written
/// field arithmetic defensible.
/// </para>
/// </summary>
internal readonly struct Field25519
{
    private const ulong LimbMask = (1UL << 51) - 1;

    private readonly ulong _l0;
    private readonly ulong _l1;
    private readonly ulong _l2;
    private readonly ulong _l3;
    private readonly ulong _l4;

    private Field25519(ulong l0, ulong l1, ulong l2, ulong l3, ulong l4)
    {
        _l0 = l0;
        _l1 = l1;
        _l2 = l2;
        _l3 = l3;
        _l4 = l4;
    }

    public static Field25519 Zero => new(0, 0, 0, 0, 0);

    public static Field25519 One => new(1, 0, 0, 0, 0);

    /// <summary>d = -121665/121666, the twisted Edwards curve constant.</summary>
    public static Field25519 D { get; } = FromBytes(Convert.FromHexString(
        "a3785913ca4deb75abd841414d0a700098e879777940c78c73fe6f2bee6c0352"));

    /// <summary>2*d, precomputed because the addition formula needs it every time.</summary>
    public static Field25519 DoubleD { get; } = D + D;

    /// <summary>sqrt(-1), used when recovering x from y during point decoding.</summary>
    public static Field25519 SqrtMinusOne { get; } = FromBytes(Convert.FromHexString(
        "b0a00e4a271beec478e42fad0618432fa7d7fb3d99004d2b0bdfc14f8024832b"));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Field25519 operator +(in Field25519 a, in Field25519 b)
    {
        return new Field25519(a._l0 + b._l0, a._l1 + b._l1, a._l2 + b._l2, a._l3 + b._l3, a._l4 + b._l4);
    }

    /// <summary>
    /// Subtraction. Adds 2p first so each limb stays non-negative without a borrow chain; the
    /// result is congruent mod p, which is all any caller needs.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Field25519 operator -(in Field25519 a, in Field25519 b)
    {
        // 2p in 51-bit limbs. The low limb absorbs the -38 that 2*(2^255-19) contributes.
        const ulong TwoP0 = 0xFFFFFFFFFFFDAUL;
        const ulong TwoPn = 0xFFFFFFFFFFFFEUL;

        return new Field25519(
            a._l0 + TwoP0 - b._l0,
            a._l1 + TwoPn - b._l1,
            a._l2 + TwoPn - b._l2,
            a._l3 + TwoPn - b._l3,
            a._l4 + TwoPn - b._l4).Carry();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Field25519 operator -(in Field25519 a) => Zero - a;

    /// <summary>
    /// Schoolbook multiplication with the 2^255 = 19 reduction folded into the partial products,
    /// so limbs above index 4 are multiplied by 19 and wrapped back down instead of being carried
    /// out.
    /// </summary>
    public static Field25519 operator *(in Field25519 a, in Field25519 b)
    {
        ulong a0 = a._l0, a1 = a._l1, a2 = a._l2, a3 = a._l3, a4 = a._l4;
        ulong b0 = b._l0, b1 = b._l1, b2 = b._l2, b3 = b._l3, b4 = b._l4;

        // Pre-scaling the high limbs by 19 keeps every product inside UInt128: the largest term
        // is 19 * 2^52 * 2^52, and five of those still sit far below 2^128.
        ulong b1_19 = b1 * 19, b2_19 = b2 * 19, b3_19 = b3 * 19, b4_19 = b4 * 19;

        UInt128 r0 = ((UInt128)a0 * b0) + ((UInt128)a1 * b4_19) + ((UInt128)a2 * b3_19) + ((UInt128)a3 * b2_19) + ((UInt128)a4 * b1_19);
        UInt128 r1 = ((UInt128)a0 * b1) + ((UInt128)a1 * b0) + ((UInt128)a2 * b4_19) + ((UInt128)a3 * b3_19) + ((UInt128)a4 * b2_19);
        UInt128 r2 = ((UInt128)a0 * b2) + ((UInt128)a1 * b1) + ((UInt128)a2 * b0) + ((UInt128)a3 * b4_19) + ((UInt128)a4 * b3_19);
        UInt128 r3 = ((UInt128)a0 * b3) + ((UInt128)a1 * b2) + ((UInt128)a2 * b1) + ((UInt128)a3 * b0) + ((UInt128)a4 * b4_19);
        UInt128 r4 = ((UInt128)a0 * b4) + ((UInt128)a1 * b3) + ((UInt128)a2 * b2) + ((UInt128)a3 * b1) + ((UInt128)a4 * b0);

        return FromWideLimbs(r0, r1, r2, r3, r4);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Field25519 Square() => this * this;

    /// <summary>Repeated squaring, used by the exponentiation chains.</summary>
    public Field25519 SquareRepeatedly(int count)
    {
        var result = this;
        for (int i = 0; i < count; i++)
        {
            result = result.Square();
        }
        return result;
    }

    /// <summary>
    /// Multiplicative inverse via Fermat: z^(p-2). Uses plain square-and-multiply over the fixed
    /// exponent rather than a hand-transcribed addition chain - the chain saves roughly a hundred
    /// multiplications out of the several thousand a signature verification performs, which is
    /// not worth the risk of getting a memorised sequence subtly wrong.
    /// </summary>
    public Field25519 Invert() => PowFixed(InverseExponent);

    /// <summary>z^((p-5)/8), the candidate square root used when decoding a point.</summary>
    public Field25519 PowSqrtCandidate() => PowFixed(SqrtExponent);

    // p - 2 and (p - 5) / 8, little-endian bit order is derived at runtime from these bytes.
    private static readonly byte[] InverseExponent = Convert.FromHexString(
        "ebffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f");

    private static readonly byte[] SqrtExponent = Convert.FromHexString(
        "fdffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff0f");

    private Field25519 PowFixed(byte[] exponentLittleEndian)
    {
        var result = One;
        var factor = this;

        for (int byteIndex = 0; byteIndex < exponentLittleEndian.Length; byteIndex++)
        {
            byte current = exponentLittleEndian[byteIndex];
            for (int bit = 0; bit < 8; bit++)
            {
                if ((current & (1 << bit)) != 0)
                {
                    result *= factor;
                }
                factor = factor.Square();
            }
        }

        return result;
    }

    /// <summary>True when the value is congruent to zero mod p.</summary>
    public bool IsZero()
    {
        Span<byte> bytes = stackalloc byte[32];
        WriteBytes(bytes);
        ulong difference = 0;
        for (int i = 0; i < 32; i++)
        {
            difference |= bytes[i];
        }
        return difference == 0;
    }

    /// <summary>Low bit of the canonical representative, which encodes the sign of x.</summary>
    public bool IsOdd()
    {
        Span<byte> bytes = stackalloc byte[32];
        WriteBytes(bytes);
        return (bytes[0] & 1) != 0;
    }

    public bool Equals(in Field25519 other)
    {
        Span<byte> mine = stackalloc byte[32];
        Span<byte> theirs = stackalloc byte[32];
        WriteBytes(mine);
        other.WriteBytes(theirs);
        return mine.SequenceEqual(theirs);
    }

    /// <summary>
    /// Reads a field element from 32 little-endian bytes. The top bit is ignored, matching the
    /// Ed25519 encoding where it carries the sign of x rather than part of y.
    /// </summary>
    public static Field25519 FromBytes(ReadOnlySpan<byte> bytes)
    {
        // BinaryPrimitives rather than BitConverter: the encoding is defined as little-endian by
        // RFC 8032, not as host order. Read inline rather than through a local function, because
        // a ReadOnlySpan parameter cannot be captured by one.
        ulong w0 = BinaryPrimitives.ReadUInt64LittleEndian(bytes[0..8]);
        ulong w6 = BinaryPrimitives.ReadUInt64LittleEndian(bytes[6..14]);
        ulong w12 = BinaryPrimitives.ReadUInt64LittleEndian(bytes[12..20]);
        ulong w19 = BinaryPrimitives.ReadUInt64LittleEndian(bytes[19..27]);
        ulong w24 = BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..32]);

        ulong l0 = w0 & LimbMask;
        ulong l1 = (w6 >> 3) & LimbMask;
        ulong l2 = (w12 >> 6) & LimbMask;
        ulong l3 = (w19 >> 1) & LimbMask;
        // The top bit is the sign of x in the Ed25519 encoding, not part of y; masking drops it.
        ulong l4 = (w24 >> 12) & LimbMask;

        return new Field25519(l0, l1, l2, l3, l4);
    }

    /// <summary>
    /// Writes the canonical 32-byte little-endian representative, fully reduced into [0, p).
    /// </summary>
    public void WriteBytes(Span<byte> destination)
    {
        var reduced = Carry();
        ulong l0 = reduced._l0, l1 = reduced._l1, l2 = reduced._l2, l3 = reduced._l3, l4 = reduced._l4;

        // After Carry the value is below 2^255 + small, so at most two conditional subtractions
        // of p are needed to land in [0, p). Done branchlessly to keep the shape simple.
        for (int round = 0; round < 2; round++)
        {
            // q = 1 exactly when the value is >= p.
            ulong q = (l0 + 19) >> 51;
            q = (l1 + q) >> 51;
            q = (l2 + q) >> 51;
            q = (l3 + q) >> 51;
            q = (l4 + q) >> 51;

            l0 += 19 * q;
            l1 += l0 >> 51; l0 &= LimbMask;
            l2 += l1 >> 51; l1 &= LimbMask;
            l3 += l2 >> 51; l2 &= LimbMask;
            l4 += l3 >> 51; l3 &= LimbMask;
            l4 &= LimbMask;
        }

        ulong packed0 = l0 | (l1 << 51);
        ulong packed1 = (l1 >> 13) | (l2 << 38);
        ulong packed2 = (l2 >> 26) | (l3 << 25);
        ulong packed3 = (l3 >> 39) | (l4 << 12);

        BinaryPrimitives.WriteUInt64LittleEndian(destination[0..8], packed0);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..16], packed1);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..24], packed2);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..32], packed3);
    }

    /// <summary>Propagates carries so every limb is back under 51 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Field25519 Carry()
    {
        ulong l0 = _l0, l1 = _l1, l2 = _l2, l3 = _l3, l4 = _l4;

        l1 += l0 >> 51; l0 &= LimbMask;
        l2 += l1 >> 51; l1 &= LimbMask;
        l3 += l2 >> 51; l2 &= LimbMask;
        l4 += l3 >> 51; l3 &= LimbMask;
        l0 += 19 * (l4 >> 51); l4 &= LimbMask;
        l1 += l0 >> 51; l0 &= LimbMask;

        return new Field25519(l0, l1, l2, l3, l4);
    }

    /// <summary>Reduces double-width partial products back into 51-bit limbs.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Field25519 FromWideLimbs(UInt128 r0, UInt128 r1, UInt128 r2, UInt128 r3, UInt128 r4)
    {
        r1 += (UInt128)(ulong)(r0 >> 51);
        ulong l0 = (ulong)r0 & LimbMask;

        r2 += (UInt128)(ulong)(r1 >> 51);
        ulong l1 = (ulong)r1 & LimbMask;

        r3 += (UInt128)(ulong)(r2 >> 51);
        ulong l2 = (ulong)r2 & LimbMask;

        r4 += (UInt128)(ulong)(r3 >> 51);
        ulong l3 = (ulong)r3 & LimbMask;

        ulong carry = (ulong)(r4 >> 51);
        ulong l4 = (ulong)r4 & LimbMask;

        // The overflow past 2^255 folds back in multiplied by 19.
        l0 += 19 * carry;
        l1 += l0 >> 51; l0 &= LimbMask;
        l2 += l1 >> 51; l1 &= LimbMask;

        return new Field25519(l0, l1, l2, l3, l4);
    }
}
