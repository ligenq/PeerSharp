using PeerSharp.Internals.Utilities;
using System.Numerics;
using System.Security.Cryptography;

namespace PeerSharp.Tests.Core.Utilities;

/// <summary>
/// Differential tests for the limb-based field arithmetic, using <see cref="BigInteger"/> as the
/// oracle.
///
/// The point of this file is that <see cref="Field25519"/> is hand-written carry-propagating
/// arithmetic, which is exactly the kind of code that passes casual testing and then fails on one
/// input in a million - the carry that only triggers when a limb is within a few of 2^51, the
/// reduction that is off by one only when the value sits just above p. BigInteger is slow but
/// obviously correct, so every operation is checked against it over random values and over the
/// boundary values where limb code actually breaks.
/// </summary>
public class Field25519Tests
{
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;

    /// <summary>
    /// Values chosen to sit on the edges where carry and reduction bugs live: zero, one, p-1,
    /// exactly p (non-canonical), limb boundaries, and all-ones.
    /// </summary>
    private static IEnumerable<BigInteger> EdgeValues()
    {
        yield return BigInteger.Zero;
        yield return BigInteger.One;
        yield return 2;
        yield return 19;
        yield return P - 1;
        yield return P - 2;
        yield return P - 19;
        yield return BigInteger.Pow(2, 51) - 1;
        yield return BigInteger.Pow(2, 51);
        yield return BigInteger.Pow(2, 51) + 1;
        yield return BigInteger.Pow(2, 102);
        yield return BigInteger.Pow(2, 204);
        yield return BigInteger.Pow(2, 255) - 20;
        yield return BigInteger.Pow(2, 254);
    }

    private static IEnumerable<BigInteger> RandomValues(int count, int seed)
    {
        var random = new Random(seed);
        var buffer = new byte[32];
        for (int i = 0; i < count; i++)
        {
            random.NextBytes(buffer);
            buffer[31] &= 0x7F;
            yield return new BigInteger(buffer, isUnsigned: true, isBigEndian: false) % P;
        }
    }

    private static IEnumerable<BigInteger> AllTestValues(int seed) => EdgeValues().Concat(RandomValues(200, seed));

    private static Field25519 ToField(BigInteger value)
    {
        var bytes = new byte[32];
        Mod(value).TryWriteBytes(bytes, out _, isUnsigned: true, isBigEndian: false);
        return Field25519.FromBytes(bytes);
    }

    private static BigInteger ToBig(in Field25519 value)
    {
        Span<byte> bytes = stackalloc byte[32];
        value.WriteBytes(bytes);
        return new BigInteger(bytes, isUnsigned: true, isBigEndian: false);
    }

    private static BigInteger Mod(BigInteger value)
    {
        var result = value % P;
        return result.Sign < 0 ? result + P : result;
    }

    [Fact]
    public void FromBytes_WriteBytes_RoundTripsCanonically()
    {
        foreach (var value in AllTestValues(seed: 1))
        {
            Assert.Equal(Mod(value), ToBig(ToField(value)));
        }
    }

    /// <summary>
    /// A value of exactly p must encode as zero: the canonical representative of p mod p is 0.
    /// Getting this wrong produces two encodings for the same point.
    /// </summary>
    [Fact]
    public void WriteBytes_ReducesNonCanonicalInput()
    {
        var bytes = new byte[32];
        P.TryWriteBytes(bytes, out _, isUnsigned: true, isBigEndian: false);

        Assert.Equal(BigInteger.Zero, ToBig(Field25519.FromBytes(bytes)));

        // p + 1 must come back as 1.
        (P + 1).TryWriteBytes(bytes, out _, isUnsigned: true, isBigEndian: false);
        Assert.Equal(BigInteger.One, ToBig(Field25519.FromBytes(bytes)));
    }

    [Fact]
    public void Add_MatchesBigInteger()
    {
        var values = AllTestValues(seed: 2).ToArray();
        for (int i = 0; i < values.Length; i++)
        {
            for (int j = 0; j < values.Length; j += 7)
            {
                var expected = Mod(values[i] + values[j]);
                var actual = ToBig(ToField(values[i]) + ToField(values[j]));
                Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public void Subtract_MatchesBigInteger()
    {
        var values = AllTestValues(seed: 3).ToArray();
        for (int i = 0; i < values.Length; i++)
        {
            for (int j = 0; j < values.Length; j += 7)
            {
                var expected = Mod(values[i] - values[j]);
                var actual = ToBig(ToField(values[i]) - ToField(values[j]));
                Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public void Multiply_MatchesBigInteger()
    {
        var values = AllTestValues(seed: 4).ToArray();
        for (int i = 0; i < values.Length; i++)
        {
            for (int j = 0; j < values.Length; j += 5)
            {
                var expected = Mod(values[i] * values[j]);
                var actual = ToBig(ToField(values[i]) * ToField(values[j]));
                Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public void Square_MatchesBigInteger()
    {
        foreach (var value in AllTestValues(seed: 5))
        {
            Assert.Equal(Mod(value * value), ToBig(ToField(value).Square()));
        }
    }

    /// <summary>
    /// Chained operations without intermediate reduction are where limb overflow shows up: each
    /// step leaves limbs slightly larger, and a representation that only holds for freshly
    /// reduced inputs fails after a few rounds.
    /// </summary>
    [Fact]
    public void ChainedOperations_MatchBigInteger()
    {
        var random = new Random(6);
        var buffer = new byte[32];

        for (int trial = 0; trial < 50; trial++)
        {
            random.NextBytes(buffer);
            buffer[31] &= 0x7F;
            var expected = new BigInteger(buffer, isUnsigned: true, isBigEndian: false) % P;
            var actual = ToField(expected);

            for (int step = 0; step < 40; step++)
            {
                random.NextBytes(buffer);
                buffer[31] &= 0x7F;
                var operand = new BigInteger(buffer, isUnsigned: true, isBigEndian: false) % P;
                var fieldOperand = ToField(operand);

                switch (step % 4)
                {
                    case 0:
                        expected = Mod(expected + operand);
                        actual += fieldOperand;
                        break;
                    case 1:
                        expected = Mod(expected - operand);
                        actual -= fieldOperand;
                        break;
                    case 2:
                        expected = Mod(expected * operand);
                        actual *= fieldOperand;
                        break;
                    default:
                        expected = Mod(expected * expected);
                        actual = actual.Square();
                        break;
                }
            }

            Assert.Equal(expected, ToBig(actual));
        }
    }

    [Fact]
    public void Negate_MatchesBigInteger()
    {
        foreach (var value in AllTestValues(seed: 7))
        {
            Assert.Equal(Mod(-value), ToBig(-ToField(value)));
        }
    }

    [Fact]
    public void Invert_MatchesBigInteger()
    {
        foreach (var value in AllTestValues(seed: 8))
        {
            if (Mod(value).IsZero)
            {
                continue;
            }

            var expected = BigInteger.ModPow(Mod(value), P - 2, P);
            Assert.Equal(expected, ToBig(ToField(value).Invert()));
        }
    }

    [Fact]
    public void Invert_ProducesMultiplicativeIdentity()
    {
        foreach (var value in RandomValues(50, seed: 9))
        {
            if (Mod(value).IsZero)
            {
                continue;
            }

            var field = ToField(value);
            Assert.Equal(BigInteger.One, ToBig(field * field.Invert()));
        }
    }

    [Fact]
    public void PowSqrtCandidate_MatchesBigInteger()
    {
        foreach (var value in AllTestValues(seed: 10))
        {
            var expected = BigInteger.ModPow(Mod(value), (P - 5) / 8, P);
            Assert.Equal(expected, ToBig(ToField(value).PowSqrtCandidate()));
        }
    }

    [Fact]
    public void IsZero_And_IsOdd_MatchBigInteger()
    {
        foreach (var value in AllTestValues(seed: 11))
        {
            var reduced = Mod(value);
            var field = ToField(value);

            Assert.Equal(reduced.IsZero, field.IsZero());
            Assert.Equal(!reduced.IsEven, field.IsOdd());
        }
    }

    [Fact]
    public void Equals_MatchesBigInteger()
    {
        var values = AllTestValues(seed: 12).ToArray();
        for (int i = 0; i < values.Length; i += 3)
        {
            for (int j = 0; j < values.Length; j += 11)
            {
                bool expected = Mod(values[i]) == Mod(values[j]);
                Assert.Equal(expected, ToField(values[i]).Equals(ToField(values[j])));
            }
        }
    }

    /// <summary>
    /// The hardcoded curve constants are derived independently here rather than trusted. A wrong
    /// d silently produces a different curve, on which signatures still verify against each other
    /// but interoperate with nothing.
    /// </summary>
    [Fact]
    public void Constants_MatchTheirDefinitions()
    {
        var expectedD = Mod(-121665 * BigInteger.ModPow(121666, P - 2, P));
        Assert.Equal(expectedD, ToBig(Field25519.D));

        Assert.Equal(Mod(expectedD * 2), ToBig(Field25519.DoubleD));

        var expectedSqrtMinusOne = BigInteger.ModPow(2, (P - 1) / 4, P);
        Assert.Equal(expectedSqrtMinusOne, ToBig(Field25519.SqrtMinusOne));

        // sqrt(-1)^2 must be -1.
        Assert.Equal(Mod(-1), ToBig(Field25519.SqrtMinusOne.Square()));
    }

    [Fact]
    public void Zero_And_One_AreCorrect()
    {
        Assert.Equal(BigInteger.Zero, ToBig(Field25519.Zero));
        Assert.Equal(BigInteger.One, ToBig(Field25519.One));
        Assert.True(Field25519.Zero.IsZero());
        Assert.False(Field25519.One.IsZero());
        Assert.True(Field25519.One.IsOdd());
    }

    [Fact]
    public void FromBytes_IgnoresTheSignBit()
    {
        // Bit 255 carries the sign of x in the Ed25519 encoding and is not part of the field
        // element, so setting it must not change the decoded value.
        var bytes = RandomNumberGenerator.GetBytes(32);
        bytes[31] &= 0x7F;
        var withoutSignBit = Field25519.FromBytes(bytes);

        bytes[31] |= 0x80;
        var withSignBit = Field25519.FromBytes(bytes);

        Assert.Equal(ToBig(withoutSignBit), ToBig(withSignBit));
    }
}
