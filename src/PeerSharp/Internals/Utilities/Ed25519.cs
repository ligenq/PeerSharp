using System.Numerics;
using System.Security.Cryptography;

namespace PeerSharp.Internals.Utilities;

/// <summary>
/// Ed25519 signatures (RFC 8032), needed by BEP 44 mutable DHT items.
///
/// .NET 10 ships no Ed25519 primitive - the only occurrences in the BCL are the composite
/// post-quantum identifiers MLDsa44WithEd25519 and MLDsa65WithEd25519 - so this is vendored
/// rather than referenced. It is written against <see cref="BigInteger"/> in preference to a
/// ref10-style port of packed field arithmetic: the whole implementation stays close enough to
/// the RFC that it can be read and checked against the spec, which matters far more here than
/// throughput. It is validated against the RFC 8032 section 7.1 vectors plus malleability and
/// small-order rejection cases.
///
/// <para>
/// Threat model, stated plainly. <see cref="Verify"/> handles no secret material, so its timing
/// leaks nothing. <see cref="Sign"/> is <b>not</b> constant-time: BigInteger arithmetic branches
/// on values, so a local attacker able to measure signing precisely could in principle recover
/// the nonce and hence the key. That is acceptable for the use BEP 44 puts it to - a publisher
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

    // Curve constants (RFC 8032 section 5.1).
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;

    /// <summary>Order of the base point.</summary>
    private static readonly BigInteger L =
        BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");

    /// <summary>d = -121665/121666 mod p.</summary>
    private static readonly BigInteger D =
        Mod(-121665 * Inverse(121666));

    /// <summary>sqrt(-1) mod p, used when recovering x during point decoding.</summary>
    private static readonly BigInteger SqrtMinusOne =
        BigInteger.ModPow(2, (P - 1) / 4, P);

    private static readonly Point BasePoint = MakeBasePoint();

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
        var scalar = ClampScalar(hash[..32]);
        return Encode(ScalarMultiply(BasePoint, scalar));
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

        Span<byte> expanded = stackalloc byte[64];
        SHA512.HashData(seed, expanded);

        var scalar = ClampScalar(expanded[..32]);
        var publicKey = Encode(ScalarMultiply(BasePoint, scalar));

        // r = SHA512(prefix || message) mod L, where prefix is the upper half of the expansion.
        var prefixed = new byte[32 + message.Length];
        expanded[32..].CopyTo(prefixed);
        message.CopyTo(prefixed.AsSpan(32));
        var r = Mod(LittleEndianToBigInteger(SHA512.HashData(prefixed)), L);

        var rPoint = Encode(ScalarMultiply(BasePoint, r));

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
        var left = ScalarMultiply(BasePoint, s);
        var right = Add(rPoint, ScalarMultiply(aPoint, k));

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
    private static BigInteger ClampScalar(ReadOnlySpan<byte> lower)
    {
        Span<byte> clamped = stackalloc byte[32];
        lower.CopyTo(clamped);
        clamped[0] &= 248;
        clamped[31] &= 127;
        clamped[31] |= 64;
        return LittleEndianToBigInteger(clamped);
    }

    // ---- Field and group arithmetic -------------------------------------------------------

    /// <summary>
    /// A curve point in extended twisted Edwards coordinates: x = X/Z, y = Y/Z, xy = T/Z.
    /// Projective form keeps a modular inversion out of every addition; one inversion is paid
    /// at encode time instead.
    /// </summary>
    private readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);

    private static Point MakeBasePoint()
    {
        var y = Mod(4 * Inverse(5));
        var x = RecoverX(y, isNegative: false) ?? throw new InvalidOperationException("Ed25519 base point is invalid.");
        return new Point(x, y, BigInteger.One, Mod(x * y));
    }

    private static Point Identity => new(BigInteger.Zero, BigInteger.One, BigInteger.One, BigInteger.Zero);

    /// <summary>Extended coordinate addition for a = -1 (add-2008-hwcd-3).</summary>
    private static Point Add(in Point p, in Point q)
    {
        var a = Mod((p.Y - p.X) * (q.Y - q.X));
        var b = Mod((p.Y + p.X) * (q.Y + q.X));
        var c = Mod(p.T * 2 * D * q.T);
        var d = Mod(p.Z * 2 * q.Z);
        var e = b - a;
        var f = d - c;
        var g = d + c;
        var h = b + a;
        return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    /// <summary>Extended coordinate doubling for a = -1 (dbl-2008-hwcd).</summary>
    private static Point Double(in Point p)
    {
        var a = Mod(p.X * p.X);
        var b = Mod(p.Y * p.Y);
        var c = Mod(2 * p.Z * p.Z);
        var h = a + b;
        var e = h - Mod((p.X + p.Y) * (p.X + p.Y));
        var g = a - b;
        var f = c + g;
        return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static Point ScalarMultiply(in Point point, BigInteger scalar)
    {
        var result = Identity;
        var addend = point;

        // Montgomery-style double-and-add. Not constant-time; see the type remarks.
        while (scalar > BigInteger.Zero)
        {
            if (!scalar.IsEven)
            {
                result = Add(result, addend);
            }
            addend = Double(addend);
            scalar >>= 1;
        }

        return result;
    }

    private static byte[] Encode(in Point point)
    {
        var inverseZ = Inverse(point.Z);
        var x = Mod(point.X * inverseZ);
        var y = Mod(point.Y * inverseZ);

        var encoded = new byte[32];
        BigIntegerToLittleEndian(y, encoded);
        // The sign of x rides in the top bit, which y never uses because y < p < 2^255.
        encoded[31] |= (byte)((x & BigInteger.One) == BigInteger.One ? 0x80 : 0x00);
        return encoded;
    }

    private static bool TryDecode(ReadOnlySpan<byte> encoded, out Point point)
    {
        point = default;

        Span<byte> copy = stackalloc byte[32];
        encoded.CopyTo(copy);
        bool xIsNegative = (copy[31] & 0x80) != 0;
        copy[31] &= 0x7F;

        var y = LittleEndianToBigInteger(copy);
        if (y >= P)
        {
            // Non-canonical y encoding.
            return false;
        }

        var x = RecoverX(y, xIsNegative);
        if (x is null)
        {
            return false;
        }

        point = new Point(x.Value, y, BigInteger.One, Mod(x.Value * y));
        return true;
    }

    /// <summary>
    /// Recovers x from y on the curve, choosing the root whose parity matches
    /// <paramref name="isNegative"/>. Returns null when no such point exists.
    /// </summary>
    private static BigInteger? RecoverX(BigInteger y, bool isNegative)
    {
        var y2 = Mod(y * y);
        var u = Mod(y2 - 1);
        var v = Mod((D * y2) + 1);

        // Candidate root: x = u*v^3 * (u*v^7)^((p-5)/8)
        var v3 = Mod(v * v * v);
        var v7 = Mod(v3 * v3 * v);
        var x = Mod(u * v3 * BigInteger.ModPow(Mod(u * v7), (P - 5) / 8, P));

        var check = Mod(v * x * x);
        if (check == u)
        {
            // Correct root already.
        }
        else if (check == Mod(-u))
        {
            x = Mod(x * SqrtMinusOne);
        }
        else
        {
            return null;
        }

        if (x == BigInteger.Zero && isNegative)
        {
            // x = 0 has no negative form; the encoding is invalid.
            return null;
        }

        if (((x & BigInteger.One) == BigInteger.One) != isNegative)
        {
            x = Mod(-x);
        }

        return x;
    }

    private static BigInteger Inverse(BigInteger value) => BigInteger.ModPow(Mod(value), P - 2, P);

    private static BigInteger Mod(BigInteger value) => Mod(value, P);

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
        // TryWriteBytes emits the minimal little-endian representation, which is what we want -
        // the destination is pre-cleared so the value is zero-padded to full width.
        if (!value.TryWriteBytes(destination, out _, isUnsigned: true, isBigEndian: false))
        {
            throw new ArgumentException("Value does not fit in the destination buffer.", nameof(value));
        }
    }
}
