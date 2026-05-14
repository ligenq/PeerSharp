using System.Diagnostics.CodeAnalysis;

namespace PeerSharp.Core;

/// <summary>
/// Represents a BitTorrent info hash (20 bytes for V1 SHA-1, 32 bytes for V2 SHA-256).
/// This is a value type optimized for equality comparison and use as dictionary keys.
/// </summary>
public readonly struct InfoHash : IEquatable<InfoHash>
{
    /// <summary>
    /// Standard V1 info hash length (SHA-1).
    /// </summary>
    public const int V1Length = 20;

    /// <summary>
    /// V2 info hash length (SHA-256).
    /// </summary>
    public const int V2Length = 32;

    private readonly byte[] _bytes;

    /// <summary>
    /// Creates a new InfoHash from a byte array. The array is copied.
    /// </summary>
    /// <param name="bytes">The hash bytes (must be 20 or 32 bytes).</param>
    /// <exception cref="ArgumentException">If the length is not 20 or 32 bytes.</exception>
    public InfoHash(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length != V1Length && bytes.Length != V2Length)
        {
            throw new ArgumentException($"Info hash must be {V1Length} or {V2Length} bytes, got {bytes.Length}", nameof(bytes));
        }

        _bytes = new byte[bytes.Length];
        bytes.CopyTo(_bytes, 0);
    }

    /// <summary>
    /// Creates a new InfoHash from a span. The span is copied.
    /// </summary>
    /// <param name="bytes">The hash bytes (must be 20 or 32 bytes).</param>
    /// <exception cref="ArgumentException">If the length is not 20 or 32 bytes.</exception>
    public InfoHash(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != V1Length && bytes.Length != V2Length)
        {
            throw new ArgumentException($"Info hash must be {V1Length} or {V2Length} bytes, got {bytes.Length}", nameof(bytes));
        }

        _bytes = bytes.ToArray();
    }

    /// <summary>
    /// Creates a new InfoHash from a ReadOnlyMemory. The memory is copied.
    /// </summary>
    public InfoHash(ReadOnlyMemory<byte> bytes) : this(bytes.Span)
    {
    }

    /// <summary>
    /// Gets an empty info hash (all zeros, V1 length).
    /// </summary>
    public static InfoHash Empty { get; } = new(new byte[V1Length]);

    /// <summary>
    /// Gets an empty V2 info hash (all zeros, V2 length).
    /// </summary>
    public static InfoHash EmptyV2 { get; } = new(new byte[V2Length]);

    /// <summary>
    /// Gets whether this info hash is empty (all zeros or null).
    /// </summary>
    public bool IsEmpty => _bytes?.All(b => b == 0) != false;

    /// <summary>
    /// Gets whether this is a V1 info hash (20 bytes).
    /// </summary>
    public bool IsV1 => Length == V1Length;

    /// <summary>
    /// Gets whether this is a V2 info hash (32 bytes).
    /// </summary>
    public bool IsV2 => Length == V2Length;

    /// <summary>
    /// Gets the length of the info hash in bytes.
    /// </summary>
    public int Length => _bytes?.Length ?? 0;

    /// <summary>
    /// Gets the hash bytes as a ReadOnlyMemory.
    /// </summary>
    public ReadOnlyMemory<byte> Memory => _bytes ?? ReadOnlyMemory<byte>.Empty;

    /// <summary>
    /// Gets the hash bytes as a ReadOnlySpan.
    /// </summary>
    public ReadOnlySpan<byte> Span => _bytes ?? ReadOnlySpan<byte>.Empty;

    /// <summary>
    /// Creates a new random V1 InfoHash.
    /// </summary>
    public static InfoHash CreateRandom()
    {
        var bytes = new byte[V1Length];
        Random.Shared.NextBytes(bytes);
        return new InfoHash(bytes);
    }

    /// <summary>
    /// Creates a new random V2 InfoHash.
    /// </summary>
    public static InfoHash CreateRandomV2()
    {
        var bytes = new byte[V2Length];
        Random.Shared.NextBytes(bytes);
        return new InfoHash(bytes);
    }

    /// <summary>
    /// Explicit conversion to ReadOnlyMemory.
    /// </summary>
    public static explicit operator ReadOnlyMemory<byte>(InfoHash hash) => hash.Memory;

    /// <summary>
    /// Explicit conversion to ReadOnlySpan.
    /// </summary>
    public static explicit operator ReadOnlySpan<byte>(InfoHash hash) => hash.Span;

    /// <summary>
    /// Creates an InfoHash from a hex string.
    /// </summary>
    /// <param name="hex">The hex string (40 characters for V1, 64 for V2).</param>
    /// <returns>The parsed InfoHash.</returns>
    /// <exception cref="FormatException">If the hex string is invalid.</exception>
    public static InfoHash FromHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var bytes = Convert.FromHexString(hex);
        return new InfoHash(bytes);
    }

    /// <summary>
    /// Implicit conversion from byte array.
    /// </summary>
    public static implicit operator InfoHash(byte[] bytes) => new(bytes);

    /// <summary>
    /// Implicit conversion from ReadOnlySpan.
    /// </summary>
    public static implicit operator InfoHash(ReadOnlySpan<byte> bytes) => new(bytes);

    /// <summary>
    /// Implicit conversion from ReadOnlyMemory.
    /// </summary>
    public static implicit operator InfoHash(ReadOnlyMemory<byte> bytes) => new(bytes);

    /// <summary>
    /// Inequality operator.
    /// </summary>
    public static bool operator !=(InfoHash left, InfoHash right) => !left.Equals(right);

    /// <summary>
    /// Equality operator.
    /// </summary>
    public static bool operator ==(InfoHash left, InfoHash right) => left.Equals(right);

    /// <summary>
    /// Tries to parse a hex string into an InfoHash.
    /// </summary>
    /// <param name="hex">The hex string to parse.</param>
    /// <param name="result">The parsed InfoHash if successful.</param>
    /// <returns>True if parsing succeeded, false otherwise.</returns>
    public static bool TryFromHex(string? hex, out InfoHash result)
    {
        result = default;
        if (string.IsNullOrEmpty(hex))
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromHexString(hex);
            if (bytes.Length != V1Length && bytes.Length != V2Length)
            {
                return false;
            }

            result = new InfoHash(bytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Copies the hash bytes to a destination span.
    /// </summary>
    /// <param name="destination">The destination span.</param>
    /// <exception cref="ArgumentException">If the destination is too small.</exception>
    public void CopyTo(Span<byte> destination)
    {
        if (_bytes == null)
        {
            return;
        }

        if (destination.Length < _bytes.Length)
        {
            throw new ArgumentException($"Destination must be at least {_bytes.Length} bytes", nameof(destination));
        }

        _bytes.CopyTo(destination);
    }

    /// <summary>
    /// Copies the hash bytes to a destination array at the specified offset.
    /// </summary>
    public void CopyTo(byte[] destination, int destinationOffset)
    {
        _bytes?.CopyTo(destination, destinationOffset);
    }

    /// <inheritdoc/>
    public bool Equals(InfoHash other)
    {
        if (_bytes == null && other._bytes == null)
        {
            return true;
        }

        if (_bytes == null || other._bytes == null)
        {
            return false;
        }

        if (_bytes.Length != other._bytes.Length)
        {
            return false;
        }

        return _bytes.AsSpan().SequenceEqual(other._bytes);
    }

    /// <inheritdoc/>
    public override bool Equals([NotNullWhen(true)] object? obj)
    {
        return obj is InfoHash other && Equals(other);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        if (_bytes == null || _bytes.Length < 4)
        {
            return 0;
        }

        // Use first 4 bytes as hash code - sufficient for dictionary distribution
        return BitConverter.ToInt32(_bytes, 0);
    }

    /// <summary>
    /// Creates a copy of the hash bytes as a new array.
    /// </summary>
    public byte[] ToArray()
    {
        return _bytes?.ToArray() ?? [];
    }

    /// <summary>
    /// Converts the info hash to a lowercase hex string.
    /// </summary>
    public string ToHexString()
    {
        return _bytes != null ? Convert.ToHexString(_bytes).ToLowerInvariant() : string.Empty;
    }

    /// <summary>
    /// Converts the info hash to an uppercase hex string.
    /// </summary>
    public string ToHexStringUpper()
    {
        return _bytes != null ? Convert.ToHexString(_bytes) : string.Empty;
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        return ToHexString();
    }

    /// <summary>
    /// Truncates a V2 hash to V1 length (first 20 bytes).
    /// </summary>
    /// <returns>A new InfoHash containing the first 20 bytes.</returns>
    /// <exception cref="InvalidOperationException">If this is not a V2 hash.</exception>
    public InfoHash TruncateToV1()
    {
        if (!IsV2)
        {
            throw new InvalidOperationException("Can only truncate V2 hashes to V1");
        }

        return new InfoHash(_bytes.AsSpan(0, V1Length));
    }
}

