using PeerSharp.BEncoding;

namespace PeerSharp.Internals.Dht;

/// <summary>
/// BEP 44 error codes, sent back in the <c>e</c> list of an error reply.
/// </summary>
internal enum DhtPutError
{
    /// <summary>The put was accepted.</summary>
    None = 0,

    /// <summary>Generic protocol error - a required field was missing or malformed.</summary>
    Protocol = 203,

    /// <summary>The bencoded value exceeded <see cref="DhtItem.MaxValueLength"/> bytes.</summary>
    ValueTooBig = 205,

    /// <summary>The Ed25519 signature did not verify against the supplied key.</summary>
    InvalidSignature = 206,

    /// <summary>The salt exceeded <see cref="DhtItem.MaxSaltLength"/> bytes.</summary>
    SaltTooBig = 207,

    /// <summary>The compare-and-swap sequence number did not match what is stored.</summary>
    CasMismatch = 301,

    /// <summary>The sequence number was not newer than the stored one.</summary>
    SequenceNumberTooLow = 302,
}

/// <summary>
/// A value stored in the DHT under BEP 44.
///
/// Items come in two kinds. An immutable item is addressed by the hash of its own contents, so it
/// can never change. A mutable item is addressed by the publisher's public key plus an optional
/// salt, and carries a sequence number and an Ed25519 signature - which is what lets the
/// publisher replace it later, and lets everyone else verify that the replacement came from the
/// same key. BEP 46 builds self-updating torrents on exactly that.
/// </summary>
internal abstract record DhtItem
{
    /// <summary>Maximum length of the bencoded value, per BEP 44.</summary>
    public const int MaxValueLength = 1000;

    /// <summary>Maximum length of a salt, per BEP 44.</summary>
    public const int MaxSaltLength = 64;

    /// <summary>The stored value.</summary>
    public required IBNode Value { get; init; }

    /// <summary>The keyspace address this item is stored under.</summary>
    public abstract DhtTarget Target { get; }
}

/// <summary>
/// An item addressed by the SHA-1 of its own bencoded value. It cannot be updated - a different
/// value is simply a different item at a different address.
/// </summary>
internal sealed record DhtImmutableItem : DhtItem
{
    private DhtTarget? _target;

    /// <inheritdoc/>
    public override DhtTarget Target => _target ??= DhtItemCodec.ComputeImmutableTarget(Value);
}

/// <summary>
/// An item addressed by its publisher's Ed25519 public key and optional salt, signed so that any
/// node can verify an update really came from the key that owns the address.
///
/// The salt exists so one key can own several independent records: the target is derived from
/// key and salt together, so <c>(key, "photos")</c> and <c>(key, "profile")</c> are different
/// addresses.
/// </summary>
internal sealed record DhtMutableItem : DhtItem
{
    private DhtTarget? _target;

    /// <summary>The publisher's 32-byte Ed25519 public key.</summary>
    public required byte[] PublicKey { get; init; }

    /// <summary>
    /// Monotonically increasing version. Storage nodes reject anything not newer than what they
    /// already hold, which is what stops an old record being replayed over a newer one.
    /// </summary>
    public required long SequenceNumber { get; init; }

    /// <summary>The 64-byte Ed25519 signature over salt, sequence number and value.</summary>
    public required byte[] Signature { get; init; }

    /// <summary>Optional salt, up to <see cref="DhtItem.MaxSaltLength"/> bytes. Null means none.</summary>
    public byte[]? Salt { get; init; }

    /// <inheritdoc/>
    public override DhtTarget Target => _target ??= DhtItemCodec.ComputeMutableTarget(PublicKey, Salt);

    /// <summary>Verifies the signature over this item's salt, sequence number and value.</summary>
    public bool VerifySignature() => DhtItemCodec.VerifySignature(this);
}
