using PeerSharp.BEncoding;
using PeerSharp.Internals.Utilities;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PeerSharp.Internals.Dht;

/// <summary>
/// Target derivation and signature encoding for BEP 44 items.
///
/// Everything here is byte-exact interoperability surface. Getting the signature buffer wrong by
/// a single character produces signatures that verify perfectly against our own implementation
/// and against nobody else's, so the layout is spelled out rather than assembled ad hoc, and the
/// worked example from the BEP is asserted in the tests.
/// </summary>
internal static class DhtItemCodec
{
    /// <summary>
    /// Target for an immutable item: the SHA-1 of its bencoded value.
    /// </summary>
    public static DhtTarget ComputeImmutableTarget(IBNode value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new DhtTarget(SHA1.HashData(BencodeWriter.Write(value)));
    }

    /// <summary>
    /// Target for a mutable item: the SHA-1 of the public key with the salt appended.
    /// </summary>
    /// <param name="publicKey">The 32-byte Ed25519 public key.</param>
    /// <param name="salt">Optional salt; null or empty means the key alone.</param>
    public static DhtTarget ComputeMutableTarget(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> salt)
    {
        if (publicKey.Length != Ed25519.PublicKeySize)
        {
            throw new ArgumentException($"A public key must be {Ed25519.PublicKeySize} bytes.", nameof(publicKey));
        }

        Span<byte> buffer = stackalloc byte[Ed25519.PublicKeySize + DhtItem.MaxSaltLength];
        publicKey.CopyTo(buffer);
        salt.CopyTo(buffer[Ed25519.PublicKeySize..]);

        return new DhtTarget(SHA1.HashData(buffer[..(Ed25519.PublicKeySize + salt.Length)]));
    }

    /// <summary>
    /// Builds the exact byte sequence a mutable item's signature covers.
    ///
    /// Per BEP 44 this is a concatenation of bencoded key/value pairs and deliberately <b>not</b>
    /// a bencoded dictionary - there is no enclosing <c>d</c>/<c>e</c>. The salt pair is present
    /// only when a salt is set. The BEP's worked example is
    /// <c>4:salt6:foobar3:seqi1e1:v12:Hello World!</c>.
    /// </summary>
    public static byte[] BuildSignatureBuffer(ReadOnlySpan<byte> salt, long sequenceNumber, IBNode value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var encodedValue = BencodeWriter.Write(value);
        var buffer = new ArrayBufferWriterAdapter();

        if (salt.Length > 0)
        {
            buffer.Write("4:salt"u8);
            buffer.Write(Encoding.ASCII.GetBytes(salt.Length.ToString(CultureInfo.InvariantCulture)));
            buffer.Write(":"u8);
            buffer.Write(salt);
        }

        buffer.Write("3:seqi"u8);
        buffer.Write(Encoding.ASCII.GetBytes(sequenceNumber.ToString(CultureInfo.InvariantCulture)));
        buffer.Write("e1:v"u8);
        buffer.Write(encodedValue);

        return buffer.ToArray();
    }

    /// <summary>
    /// Signs a value for publication as a mutable item.
    /// </summary>
    /// <param name="seed">The publisher's 32-byte private seed.</param>
    /// <param name="salt">Optional salt, up to <see cref="DhtItem.MaxSaltLength"/> bytes.</param>
    /// <param name="sequenceNumber">Version number; must exceed any previously published value.</param>
    /// <param name="value">The value to store.</param>
    public static DhtMutableItem CreateSigned(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> salt, long sequenceNumber, IBNode value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(sequenceNumber);

        if (salt.Length > DhtItem.MaxSaltLength)
        {
            throw new ArgumentException($"A salt may be at most {DhtItem.MaxSaltLength} bytes.", nameof(salt));
        }

        int encodedLength = BencodeWriter.Write(value).Length;
        if (encodedLength > DhtItem.MaxValueLength)
        {
            throw new ArgumentException(
                $"The bencoded value is {encodedLength} bytes, over the {DhtItem.MaxValueLength}-byte limit.",
                nameof(value));
        }

        var signature = Ed25519.Sign(BuildSignatureBuffer(salt, sequenceNumber, value), seed);

        return new DhtMutableItem
        {
            Value = value,
            PublicKey = Ed25519.PublicKeyFromSeed(seed),
            SequenceNumber = sequenceNumber,
            Signature = signature,
            Salt = salt.Length > 0 ? salt.ToArray() : null,
        };
    }

    /// <summary>Verifies a mutable item's signature.</summary>
    public static bool VerifySignature(DhtMutableItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.PublicKey.Length != Ed25519.PublicKeySize || item.Signature.Length != Ed25519.SignatureSize)
        {
            return false;
        }

        var buffer = BuildSignatureBuffer(item.Salt ?? [], item.SequenceNumber, item.Value);
        return Ed25519.Verify(item.Signature, buffer, item.PublicKey);
    }

    /// <summary>
    /// Applies the checks a storage node performs before accepting a put, in the order BEP 44
    /// defines the error codes. Cheap structural checks come before the signature so a malformed
    /// put costs an attacker a rejection rather than an elliptic curve operation.
    /// </summary>
    /// <param name="item">The item being stored.</param>
    /// <returns><see cref="DhtPutError.None"/> when the item may be stored.</returns>
    public static DhtPutError Validate(DhtItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (BencodeWriter.Write(item.Value).Length > DhtItem.MaxValueLength)
        {
            return DhtPutError.ValueTooBig;
        }

        if (item is not DhtMutableItem mutable)
        {
            return DhtPutError.None;
        }

        if (mutable.Salt is { Length: > DhtItem.MaxSaltLength })
        {
            return DhtPutError.SaltTooBig;
        }

        if (mutable.PublicKey.Length != Ed25519.PublicKeySize ||
            mutable.Signature.Length != Ed25519.SignatureSize ||
            mutable.SequenceNumber < 0)
        {
            return DhtPutError.Protocol;
        }

        return mutable.VerifySignature() ? DhtPutError.None : DhtPutError.InvalidSignature;
    }

    /// <summary>
    /// Decides whether an incoming mutable item may replace one already stored.
    /// </summary>
    /// <param name="stored">The item currently held, or null if the address is empty.</param>
    /// <param name="incoming">The item being offered.</param>
    /// <param name="compareAndSwap">
    /// Optional expected sequence number. When supplied it must equal the stored item's sequence
    /// number, which lets a publisher detect that someone else updated the record underneath them.
    /// </param>
    public static DhtPutError CheckReplacement(DhtMutableItem? stored, DhtMutableItem incoming, long? compareAndSwap)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        if (stored is null)
        {
            // Nothing stored yet. A cas against an absent item cannot be satisfied.
            return compareAndSwap is null ? DhtPutError.None : DhtPutError.CasMismatch;
        }

        if (compareAndSwap is not null && stored.SequenceNumber != compareAndSwap.Value)
        {
            return DhtPutError.CasMismatch;
        }

        if (incoming.SequenceNumber < stored.SequenceNumber)
        {
            return DhtPutError.SequenceNumberTooLow;
        }

        // An equal sequence number is only acceptable as an idempotent republish of the same
        // bytes. Allowing a different value at the same sequence would let two publishers with
        // the same key fork the record with no way for readers to tell which is current.
        if (incoming.SequenceNumber == stored.SequenceNumber &&
            !BencodeWriter.Write(incoming.Value).AsSpan().SequenceEqual(BencodeWriter.Write(stored.Value)))
        {
            return DhtPutError.SequenceNumberTooLow;
        }

        return DhtPutError.None;
    }

    /// <summary>Minimal growable byte sink; the signature buffer is assembled from small pieces.</summary>
    private sealed class ArrayBufferWriterAdapter
    {
        private byte[] _buffer = new byte[128];
        private int _written;

        public void Write(ReadOnlySpan<byte> data)
        {
            if (_written + data.Length > _buffer.Length)
            {
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _written + data.Length));
            }

            data.CopyTo(_buffer.AsSpan(_written));
            _written += data.Length;
        }

        public byte[] ToArray() => _buffer.AsSpan(0, _written).ToArray();
    }
}
