using System.Diagnostics.CodeAnalysis;

namespace PeerSharp.Internals.Dht;

/// <summary>
/// A 20-byte address in the DHT keyspace.
///
/// Deliberately distinct from <see cref="InfoHash"/> even though both are 20 bytes and the
/// routing table treats them identically. A BEP 44 target is derived from a public key and salt,
/// or from the hash of a stored value - it is not a torrent's info-hash, and BEP 46 makes the
/// difference load-bearing: there the target addresses a record whose <em>contents</em> are an
/// info-hash. Keeping the types apart stops the two being swapped by accident.
/// </summary>
internal readonly struct DhtTarget : IEquatable<DhtTarget>
{
    /// <summary>Length of a target in bytes.</summary>
    public const int Length = 20;

    private readonly byte[] _bytes;

    /// <summary>Wraps 20 bytes as a target.</summary>
    /// <param name="bytes">Exactly 20 bytes.</param>
    public DhtTarget(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Length)
        {
            throw new ArgumentException($"A DHT target must be exactly {Length} bytes.", nameof(bytes));
        }

        _bytes = bytes.ToArray();
    }

    /// <summary>The raw bytes. Empty for a default-constructed target.</summary>
    public ReadOnlySpan<byte> Span => _bytes ?? ReadOnlySpan<byte>.Empty;

    /// <summary>The raw bytes as memory.</summary>
    public ReadOnlyMemory<byte> Memory => _bytes ?? ReadOnlyMemory<byte>.Empty;

    /// <summary>True when this is a default-constructed target with no bytes.</summary>
    public bool IsEmpty => _bytes is null;

    /// <summary>Parses a 40-character hex string.</summary>
    public static DhtTarget FromHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        return new DhtTarget(Convert.FromHexString(hex));
    }

    /// <summary>Renders the target as lowercase hex.</summary>
    public override string ToString() => _bytes is null ? string.Empty : Convert.ToHexStringLower(_bytes);

    public bool Equals(DhtTarget other) => Span.SequenceEqual(other.Span);

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is DhtTarget other && Equals(other);

    public override int GetHashCode()
    {
        // The bytes are already a hash, so the leading four are as good a bucket as any.
        var span = Span;
        return span.Length >= 4 ? BitConverter.ToInt32(span[..4]) : 0;
    }

    public static bool operator ==(DhtTarget left, DhtTarget right) => left.Equals(right);

    public static bool operator !=(DhtTarget left, DhtTarget right) => !left.Equals(right);
}
