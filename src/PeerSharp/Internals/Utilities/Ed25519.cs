using System.Numerics;
using System.Security.Cryptography;

namespace PeerSharp.Internals.Utilities;

/// <summary>
/// Ed25519 signatures (RFC 8032), needed by BEP 44 mutable DHT items.
///
/// .NET 10 ships no Ed25519 primitive - the only occurrences in the BCL are the composite
/// post-quantum identifiers MLDsa44WithEd25519 and MLDsa65WithEd25519 - so this is vendored
/// rather than referenced.
///
/// Field arithmetic lives in <see cref="Field25519"/>, which uses fixed 51-bit limbs; scalar
/// arithmetic mod L stays on <see cref="BigInteger"/>, where it runs a handful of times per
/// operation rather than thousands and the clarity is worth more than the speed. Validated
/// against the RFC 8032 section 7.1 vectors plus malleability and non-canonical encoding cases.
///
/// <para>
/// Threat model, stated plainly. <see cref="Verify"/> handles no secret material, so its timing
/// leaks nothing. <see cref="Sign"/> is <b>not</b> constant-time: neither the windowed scalar
/// ladder nor BigInteger branches uniformly, so a local attacker able to measure signing
/// precisely could in principle recover the nonce and hence the key. That is acceptable for the use BEP 44 puts it to - a publisher
/// signs their own records locally, on their own schedule, with no remotely triggerable or
/// remotely observable timing - but this type should not be reused for signing that an attacker
/// can trigger at will or time over a channel.
/// </para>
/// </summary>
internal static class Ed25519
{
    /// <summary>Length in bytes of a private key seed.</summary>
    public const int SeedSize = 32;

    /// <summary>Length in bytes of an encoded public key.</summary>
    public const int PublicKeySize = 32;

    /// <summary>Length in bytes of a signature.</summary>
    public const int SignatureSize = 64;

    /// <summary>
    /// Length in bytes of an expanded private key: the clamped scalar followed by the nonce
    /// prefix, i.e. SHA-512 of the seed.
    /// </summary>
    public const int ExpandedKeySize = 64;

    /// <summary>Order of the base point (RFC 8032 section 5.1).</summary>
    private static readonly BigInteger L =
        BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");

    private static readonly Point BasePoint = MakeBasePoint();

    /// <summary>
    /// Window table for the base point, built once. B is fixed, so rebuilding its 16 multiples on
    /// every sign, key derivation and verification was pure waste - and it was most of the
    /// allocation those operations performed.
    /// </summary>
    private static readonly Point[] BasePointTable = BuildWindowTable(BasePoint);

    /// <summary>
    /// Derives the public key for a 32-byte private seed.
    /// </summary>
    /// <param name="seed">The 32-byte private seed.</param>
    /// <returns>The 32-byte encoded public key.</returns>
    public static byte[] PublicKeyFromSeed(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != SeedSize)
        {
            throw new ArgumentException($"Seed must be {SeedSize} bytes.", nameof(seed));
        }

        Span<byte> hash = stackalloc byte[64];
        SHA512.HashData(seed, hash);
        Span<byte> scalar = stackalloc byte[32];
        ClampScalar(hash[..32], scalar);
        return Encode(ScalarMultiplyBase(scalar));
    }

    /// <summary>
    /// Generates a new random private seed. The caller derives the public key with
    /// <see cref="PublicKeyFromSeed"/> and is responsible for storing the seed securely.
    /// </summary>
    public static byte[] GenerateSeed()
    {
        var seed = new byte[SeedSize];
        RandomNumberGenerator.Fill(seed);
        return seed;
    }

    /// <summary>
    /// Signs <paramref name="message"/> with the key derived from <paramref name="seed"/>.
    /// </summary>
    /// <param name="message">The message to sign.</param>
    /// <param name="seed">The 32-byte private seed.</param>
    /// <returns>A 64-byte signature.</returns>
    /// <remarks>Not constant-time; see the remarks on <see cref="Ed25519"/>.</remarks>
    public static byte[] Sign(ReadOnlySpan<byte> message, ReadOnlySpan<byte> seed)
    {
        if (seed.Length != SeedSize)
        {
            throw new ArgumentException($"Seed must be {SeedSize} bytes.", nameof(seed));
        }

        Span<byte> expanded = stackalloc byte[ExpandedKeySize];
        SHA512.HashData(seed, expanded);
        return SignWithExpandedKey(message, expanded);
    }

    /// <summary>
    /// Derives the public key from a 64-byte expanded private key.
    /// </summary>
    /// <param name="expandedKey">
    /// The clamped scalar followed by the nonce prefix - the form produced by libsodium-style
    /// keypair generation, and the form BEP 44's own test vectors use.
    /// </param>
    public static byte[] PublicKeyFromExpandedKey(ReadOnlySpan<byte> expandedKey)
    {
        if (expandedKey.Length != ExpandedKeySize)
        {
            throw new ArgumentException($"An expanded key must be {ExpandedKeySize} bytes.", nameof(expandedKey));
        }

        Span<byte> scalar = stackalloc byte[32];
        ClampScalar(expandedKey[..32], scalar);
        return Encode(ScalarMultiplyBase(scalar));
    }

    /// <summary>
    /// Signs with a 64-byte expanded private key rather than a seed.
    /// </summary>
    /// <param name="message">The message to sign.</param>
    /// <param name="expandedKey">
    /// The clamped scalar followed by the nonce prefix. Existing BEP 44 publishers commonly
    /// persist this form rather than the seed - it is what libtorrent's keypair API yields - so a
    /// key store built against another implementation can be used directly.
    /// </param>
    /// <remarks>Not constant-time; see the remarks on <see cref="Ed25519"/>.</remarks>
    public static byte[] SignWithExpandedKey(ReadOnlySpan<byte> message, ReadOnlySpan<byte> expandedKey)
    {
        if (expandedKey.Length != ExpandedKeySize)
        {
            throw new ArgumentException($"An expanded key must be {ExpandedKeySize} bytes.", nameof(expandedKey));
        }

        Span<byte> scalarBytes = stackalloc byte[32];
        // Already clamped in a well-formed expanded key; clamping is idempotent, and re-applying
        // it means a hand-assembled key cannot produce an out-of-subgroup scalar.
        ClampScalar(expandedKey[..32], scalarBytes);
        var scalar = LittleEndianToBigInteger(scalarBytes);
        var publicKey = Encode(ScalarMultiplyBase(scalarBytes));

        // r = SHA512(prefix || message) mod L, where prefix is the upper half of the expansion.
        var prefixed = new byte[32 + message.Length];
        expandedKey[32..].CopyTo(prefixed);
        message.CopyTo(prefixed.AsSpan(32));
        var r = Mod(LittleEndianToBigInteger(SHA512.HashData(prefixed)), L);

        Span<byte> rBytes = stackalloc byte[32];
        BigIntegerToLittleEndian(r, rBytes);
        var rPoint = Encode(ScalarMultiplyBase(rBytes));

        // k = SHA512(encode(R) || publicKey || message) mod L
        var k = HashToScalar(rPoint, publicKey, message);

        var s = Mod(r + (k * scalar), L);

        var signature = new byte[SignatureSize];
        rPoint.CopyTo(signature.AsSpan(0, 32));
        BigIntegerToLittleEndian(s, signature.AsSpan(32, 32));
        return signature;
    }

    /// <summary>
    /// Verifies an Ed25519 signature. Returns false rather than throwing for any malformed input,
    /// since every caller here is validating data that arrived from the network.
    /// </summary>
    /// <param name="signature">The 64-byte signature.</param>
    /// <param name="message">The signed message.</param>
    /// <param name="publicKey">The 32-byte encoded public key.</param>
    public static bool Verify(ReadOnlySpan<byte> signature, ReadOnlySpan<byte> message, ReadOnlySpan<byte> publicKey)
    {
        if (signature.Length != SignatureSize || publicKey.Length != PublicKeySize)
        {
            return false;
        }

        // Reject non-canonical S. Without this a signature can be mauled into a different but
        // still-valid encoding, which breaks any caller treating the signature bytes as an
        // identity.
        var s = LittleEndianToBigInteger(signature[32..]);
        if (s >= L)
        {
            return false;
        }

        if (!TryDecode(signature[..32], out var rPoint) || !TryDecode(publicKey, out var aPoint))
        {
            return false;
        }

        var k = HashToScalar(signature[..32], publicKey, message);

        // Check [S]B == R + [k]A by comparing encodings, which is canonical.
        Span<byte> sBytes = stackalloc byte[32];
        Span<byte> kBytes = stackalloc byte[32];
        BigIntegerToLittleEndian(s, sBytes);
        BigIntegerToLittleEndian(k, kBytes);

        var left = ScalarMultiplyBase(sBytes);
        var right = Add(rPoint, ScalarMultiply(aPoint, kBytes));

        return Encode(left).AsSpan().SequenceEqual(Encode(right));
    }

    private static BigInteger HashToScalar(ReadOnlySpan<byte> rPoint, ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message)
    {
        var buffer = new byte[64 + message.Length];
        rPoint.CopyTo(buffer);
        publicKey.CopyTo(buffer.AsSpan(32));
        message.CopyTo(buffer.AsSpan(64));
        return Mod(LittleEndianToBigInteger(SHA512.HashData(buffer)), L);
    }

    /// <summary>
    /// Applies the RFC 8032 clamping: clear the low three bits, clear the top bit, set bit 254.
    /// This forces the scalar into the prime-order subgroup and fixes its bit length.
    /// </summary>
    private static void ClampScalar(ReadOnlySpan<byte> lower, Span<byte> destination)
    {
        lower.CopyTo(destination);
        destination[0] &= 248;
        destination[31] &= 127;
        destination[31] |= 64;
    }

    // ---- Group arithmetic ------------------------------------------------------------------

    /// <summary>
    /// A curve point in extended twisted Edwards coordinates: x = X/Z, y = Y/Z, xy = T/Z.
    /// Projective form keeps a modular inversion out of every addition; one is paid at encode
    /// time instead.
    /// </summary>
    private readonly record struct Point(Field25519 X, Field25519 Y, Field25519 Z, Field25519 T);

    private static Point MakeBasePoint()
    {
        // y = 4/5; x is the even root, per RFC 8032 section 5.1.
        var five = Field25519.One + Field25519.One + Field25519.One + Field25519.One + Field25519.One;
        var four = five - Field25519.One;
        var y = four * five.Invert();
        var x = RecoverX(y, isNegative: false) ?? throw new InvalidOperationException("Ed25519 base point is invalid.");
        return new Point(x, y, Field25519.One, x * y);
    }

    private static Point Identity => new(Field25519.Zero, Field25519.One, Field25519.One, Field25519.Zero);

    /// <summary>Extended coordinate addition for a = -1 (add-2008-hwcd-3).</summary>
    private static Point Add(in Point p, in Point q)
    {
        var a = (p.Y - p.X) * (q.Y - q.X);
        var b = (p.Y + p.X) * (q.Y + q.X);
        var c = p.T * Field25519.DoubleD * q.T;
        var d = p.Z * (q.Z + q.Z);
        var e = b - a;
        var f = d - c;
        var g = d + c;
        var h = b + a;
        return new Point(e * f, g * h, f * g, e * h);
    }

    /// <summary>Extended coordinate doubling for a = -1 (dbl-2008-hwcd).</summary>
    private static Point Double(in Point p)
    {
        var a = p.X.Square();
        var b = p.Y.Square();
        var zz = p.Z.Square();
        var c = zz + zz;
        var h = a + b;
        var xy = p.X + p.Y;
        var e = h - xy.Square();
        var g = a - b;
        var f = c + g;
        return new Point(e * f, g * h, f * g, e * h);
    }

    /// <summary>
    /// Fixed-window scalar multiplication. A 4-bit window trades 15 precomputed multiples for
    /// roughly a quarter of the additions a bit-at-a-time ladder performs, which matters because
    /// verification does two of these.
    /// </summary>
    private static Point ScalarMultiply(in Point point, ReadOnlySpan<byte> scalarLittleEndian)
    {
        return ScalarMultiply(BuildWindowTable(point), scalarLittleEndian);
    }

    private static Point ScalarMultiply(Point[] table, ReadOnlySpan<byte> scalarLittleEndian)
    {
        var result = Identity;
        bool started = false;

        // Most significant nibble first.
        for (int byteIndex = scalarLittleEndian.Length - 1; byteIndex >= 0; byteIndex--)
        {
            for (int shift = 8 - WindowBits; shift >= 0; shift -= WindowBits)
            {
                if (started)
                {
                    for (int i = 0; i < WindowBits; i++)
                    {
                        result = Double(result);
                    }
                }

                int window = (scalarLittleEndian[byteIndex] >> shift) & (WindowTableSize - 1);
                if (window != 0)
                {
                    result = started ? Add(result, table[window]) : table[window];
                    started = true;
                }
            }
        }

        return started ? result : Identity;
    }

    /// <summary>Multiplies the base point, reusing the precomputed table.</summary>
    private static Point ScalarMultiplyBase(ReadOnlySpan<byte> scalarLittleEndian)
    {
        return ScalarMultiply(BasePointTable, scalarLittleEndian);
    }

    /// <summary>table[i] = i * point, for the fixed 4-bit window.</summary>
    private static Point[] BuildWindowTable(in Point point)
    {
        var table = new Point[WindowTableSize];
        table[0] = Identity;
        table[1] = point;
        for (int i = 2; i < WindowTableSize; i++)
        {
            table[i] = (i % 2 == 0) ? Double(table[i / 2]) : Add(table[i - 1], point);
        }
        return table;
    }

    private const int WindowBits = 4;
    private const int WindowTableSize = 1 << WindowBits;

    private static byte[] Encode(in Point point)
    {
        var inverseZ = point.Z.Invert();
        var x = point.X * inverseZ;
        var y = point.Y * inverseZ;

        var encoded = new byte[32];
        y.WriteBytes(encoded);
        // The sign of x rides in the top bit, which y never uses because y < p < 2^255.
        encoded[31] |= (byte)(x.IsOdd() ? 0x80 : 0x00);
        return encoded;
    }

    private static bool TryDecode(ReadOnlySpan<byte> encoded, out Point point)
    {
        point = default;

        bool xIsNegative = (encoded[31] & 0x80) != 0;

        // Reject non-canonical y. Values in [p, 2^255) name a point that already has a canonical
        // encoding, so accepting them would give a public key two valid representations.
        Span<byte> withoutSign = stackalloc byte[32];
        encoded.CopyTo(withoutSign);
        withoutSign[31] &= 0x7F;
        if (!IsCanonical(withoutSign))
        {
            return false;
        }

        var y = Field25519.FromBytes(encoded);
        var x = RecoverX(y, xIsNegative);
        if (x is null)
        {
            return false;
        }

        point = new Point(x.Value, y, Field25519.One, x.Value * y);
        return true;
    }

    /// <summary>True when the 32-byte little-endian value is strictly below p.</summary>
    private static bool IsCanonical(ReadOnlySpan<byte> value)
    {
        // p = 2^255 - 19, so the only non-canonical encodings are 2^255-19 .. 2^255-1: every byte
        // above the low one is 0xFF (with the top byte 0x7F) and the low byte is at least 0xED.
        if (value[31] != 0x7F)
        {
            return true;
        }

        for (int i = 30; i >= 1; i--)
        {
            if (value[i] != 0xFF)
            {
                return true;
            }
        }

        return value[0] < 0xED;
    }

    /// <summary>
    /// Recovers x from y on the curve, choosing the root whose parity matches
    /// <paramref name="isNegative"/>. Returns null when no such point exists.
    /// </summary>
    private static Field25519? RecoverX(in Field25519 y, bool isNegative)
    {
        var y2 = y.Square();
        var u = y2 - Field25519.One;
        var v = (Field25519.D * y2) + Field25519.One;

        // Candidate root: x = u*v^3 * (u*v^7)^((p-5)/8)
        var v3 = v.Square() * v;
        var v7 = v3.Square() * v;
        var x = u * v3 * (u * v7).PowSqrtCandidate();

        var check = v * x.Square();
        if (!check.Equals(u))
        {
            if (check.Equals(-u))
            {
                x *= Field25519.SqrtMinusOne;
            }
            else
            {
                return null;
            }
        }

        if (x.IsZero() && isNegative)
        {
            // x = 0 has no negative form; the encoding is invalid.
            return null;
        }

        if (x.IsOdd() != isNegative)
        {
            x = -x;
        }

        return x;
    }

    // ---- Scalar arithmetic mod L -------------------------------------------------------------

    private static BigInteger Mod(BigInteger value, BigInteger modulus)
    {
        var result = value % modulus;
        return result.Sign < 0 ? result + modulus : result;
    }

    private static BigInteger LittleEndianToBigInteger(ReadOnlySpan<byte> bytes)
    {
        return new BigInteger(bytes, isUnsigned: true, isBigEndian: false);
    }

    private static void BigIntegerToLittleEndian(BigInteger value, Span<byte> destination)
    {
        destination.Clear();
        if (!value.TryWriteBytes(destination, out _, isUnsigned: true, isBigEndian: false))
        {
            throw new ArgumentException("Value does not fit in the destination buffer.", nameof(value));
        }
    }
}
