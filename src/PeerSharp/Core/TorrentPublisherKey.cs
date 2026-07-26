using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Utilities;

namespace PeerSharp.Core;

/// <summary>
/// BEP 46: a publisher identity for self-updating torrents.
///
/// A publisher owns an Ed25519 key pair and stores a signed record in the DHT naming the current
/// version of a torrent. Subscribers hold the public key rather than an info-hash, so a new
/// release reaches everyone following the key without a new link having to be distributed.
///
/// <para>
/// Not sealed only so the expanded-key form can be a private subclass; it is not an extension
/// point and has no public constructor. Use the static factories.
/// </para>
///
/// <para>
/// The private seed is the identity. Lose it and you can never publish an update under that key
/// again; leak it and anyone can publish in your name. Persist <see cref="Seed"/> somewhere you
/// would be comfortable keeping a signing key, and treat it accordingly.
/// </para>
/// </summary>
public class TorrentPublisherKey
{
    private readonly byte[] _seed;
    private readonly byte[] _publicKey;

    private TorrentPublisherKey(byte[] seed, byte[] publicKey)
    {
        _seed = seed;
        _publicKey = publicKey;
    }

    /// <summary>Length in bytes of a private seed.</summary>
    public const int SeedLength = 32;

    /// <summary>Length in bytes of a public key.</summary>
    public const int PublicKeyLength = 32;

    /// <summary>
    /// Length in bytes of the expanded private key form accepted by
    /// <see cref="FromExpandedKey"/>.
    /// </summary>
    public const int ExpandedKeyLength = 64;

    /// <summary>Creates a new random publisher identity.</summary>
    public static TorrentPublisherKey Create()
    {
        var seed = Ed25519.GenerateSeed();
        return new TorrentPublisherKey(seed, Ed25519.PublicKeyFromSeed(seed));
    }

    /// <summary>
    /// Restores an identity from a previously persisted seed.
    /// </summary>
    /// <param name="seed">The 32-byte private seed.</param>
    public static TorrentPublisherKey FromSeed(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != SeedLength)
        {
            throw new ArgumentException($"A seed must be {SeedLength} bytes.", nameof(seed));
        }

        var copy = seed.ToArray();
        return new TorrentPublisherKey(copy, Ed25519.PublicKeyFromSeed(copy));
    }

    /// <summary>
    /// Creates a read-only identity for a publisher you follow but do not control.
    /// </summary>
    /// <param name="publicKey">The publisher's 32-byte public key.</param>
    /// <remarks>
    /// Cannot publish: <see cref="CanPublish"/> is false and any attempt to sign with it throws.
    /// </remarks>
    public static TorrentPublisherKey FromPublicKey(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != PublicKeyLength)
        {
            throw new ArgumentException($"A public key must be {PublicKeyLength} bytes.", nameof(publicKey));
        }

        return new TorrentPublisherKey([], publicKey.ToArray());
    }

    /// <summary>
    /// Restores an identity from a 64-byte expanded private key - the clamped scalar followed by
    /// the nonce prefix.
    /// </summary>
    /// <param name="expandedKey">The 64-byte expanded private key.</param>
    /// <remarks>
    /// This is the form libtorrent's key generation produces, and the form BEP 44's own test
    /// vectors use, so an existing key store can be carried over. The original seed cannot be
    /// recovered from it, so <see cref="Seed"/> is empty for such a key - persist the expanded
    /// bytes you started with.
    /// </remarks>
    public static TorrentPublisherKey FromExpandedKey(ReadOnlySpan<byte> expandedKey)
    {
        if (expandedKey.Length != ExpandedKeyLength)
        {
            throw new ArgumentException($"An expanded key must be {ExpandedKeyLength} bytes.", nameof(expandedKey));
        }

        return new ExpandedPublisherKey(expandedKey.ToArray());
    }

    /// <summary>The public key. Safe to publish; this is what subscribers need.</summary>
    public ReadOnlyMemory<byte> PublicKey => _publicKey;

    /// <summary>
    /// The private seed. Empty for a key created from a public key or an expanded key. Guard this
    /// as you would any signing key.
    /// </summary>
    public ReadOnlyMemory<byte> Seed => _seed;

    /// <summary>Whether this identity holds the private material needed to publish.</summary>
    public virtual bool CanPublish => _seed.Length == SeedLength;

    /// <summary>
    /// Builds the magnet link subscribers use to follow this publisher.
    /// </summary>
    /// <param name="salt">
    /// Optional salt, letting one identity publish several independent torrents. Must be at most
    /// 64 bytes.
    /// </param>
    public string ToMagnetLink(ReadOnlySpan<byte> salt = default)
    {
        if (salt.Length > MaxSaltLength)
        {
            throw new ArgumentException($"A salt may be at most {MaxSaltLength} bytes.", nameof(salt));
        }

        // BEP 46 specifies both the key and the salt as hex.
        var link = $"magnet:?xs=urn:btpk:{Convert.ToHexStringLower(_publicKey)}";
        return salt.Length == 0 ? link : $"{link}&s={Convert.ToHexStringLower(salt)}";
    }

    /// <summary>Maximum salt length, inherited from BEP 44.</summary>
    public const int MaxSaltLength = 64;

    /// <summary>Signs a BEP 44 value on behalf of this identity.</summary>
    internal virtual byte[] Sign(ReadOnlySpan<byte> buffer)
    {
        if (!CanPublish)
        {
            throw new InvalidOperationException(
                "This identity holds no private key, so it cannot publish. Use Create, FromSeed or FromExpandedKey.");
        }

        return Ed25519.Sign(buffer, _seed);
    }

    /// <summary>
    /// An identity backed by a 64-byte expanded key rather than a seed. Kept as a subclass so the
    /// common case stays a plain seed and does not carry a redundant field.
    /// </summary>
    private sealed class ExpandedPublisherKey : TorrentPublisherKey
    {
        private readonly byte[] _expandedKey;

        public ExpandedPublisherKey(byte[] expandedKey)
            : base([], Ed25519.PublicKeyFromExpandedKey(expandedKey))
        {
            _expandedKey = expandedKey;
        }

        public override bool CanPublish => true;

        internal override byte[] Sign(ReadOnlySpan<byte> buffer) => Ed25519.SignWithExpandedKey(buffer, _expandedKey);
    }
}

/// <summary>
/// BEP 46: what a publisher's DHT record currently points at.
/// </summary>
/// <param name="InfoHash">The info-hash of the current version.</param>
/// <param name="Version">
/// The record's sequence number. Increases with each release, so a subscriber holding an older
/// value knows a newer one is available.
/// </param>
public readonly record struct SelfUpdatingTorrentInfo(InfoHash InfoHash, long Version);
