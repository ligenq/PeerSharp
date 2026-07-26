using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.BEncoding;
using PeerSharp.Internals.Utilities;

namespace PeerSharp.Internals.Dht;

/// <summary>
/// The result of resolving a BEP 46 public key to a torrent.
/// </summary>
/// <param name="InfoHash">The info-hash the record currently points at.</param>
/// <param name="SequenceNumber">
/// The record's version. A caller watching for updates keeps this and treats any higher value as
/// a new release.
/// </param>
internal readonly record struct Bep46Resolution(InfoHash InfoHash, long SequenceNumber);

/// <summary>
/// BEP 46: self-updating torrents.
///
/// A publisher owns an Ed25519 key and stores a signed DHT record saying "the current version of
/// this torrent is info-hash X". Subscribers hold the public key rather than an info-hash, so the
/// publisher can release a new version - a later episode, a corrected file, a newer build - and
/// everyone following the key picks it up without a new link.
///
/// The record's value is a bencoded dictionary with a single <c>ih</c> key holding the 20-byte
/// info-hash. The sequence number carried by BEP 44 is what makes updates safe: a subscriber only
/// accepts a record newer than the one it already has, so a captured old record cannot be replayed
/// to roll someone back to superseded content.
/// </summary>
internal sealed class Bep46Resolver
{
    /// <summary>The single key BEP 46 defines in a record's value.</summary>
    private const string InfoHashKey = "ih";

    private readonly DhtManager _dht;
    private readonly ILogger<Bep46Resolver> _logger;

    public Bep46Resolver(DhtManager dht)
        : this(dht, NullLoggerFactory.Instance)
    {
    }

    public Bep46Resolver(DhtManager dht, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(dht);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _dht = dht;
        _logger = loggerFactory.CreateLogger<Bep46Resolver>();
    }

    /// <summary>
    /// Computes the DHT address a publisher's records live at.
    /// </summary>
    /// <param name="publicKey">The publisher's 32-byte Ed25519 public key.</param>
    /// <param name="salt">Optional salt, letting one key publish several independent torrents.</param>
    public static DhtTarget ComputeTarget(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> salt)
    {
        return DhtItemCodec.ComputeMutableTarget(publicKey, salt);
    }

    /// <summary>
    /// Resolves a public key to the info-hash it currently points at.
    /// </summary>
    /// <param name="publicKey">The publisher's 32-byte Ed25519 public key.</param>
    /// <param name="salt">Optional salt.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The current info-hash and version, or null when no node holds a usable record. A malformed
    /// or unverifiable record reads as absent rather than raising: the DHT is untrusted input, and
    /// a caller cannot do anything useful with the distinction.
    /// </returns>
    public async Task<Bep46Resolution?> ResolveAsync(
        byte[] publicKey,
        byte[]? salt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publicKey);

        if (publicKey.Length != Ed25519.PublicKeySize)
        {
            throw new ArgumentException($"A public key must be {Ed25519.PublicKeySize} bytes.", nameof(publicKey));
        }

        var target = ComputeTarget(publicKey, salt ?? []);

        // GetItemAsync already discards anything whose signature fails or whose key and salt do
        // not address the target, so whatever comes back is genuinely from this publisher.
        var item = await _dht.GetItemAsync(target, salt, cancellationToken).ConfigureAwait(false);

        if (item is not DhtMutableItem mutable)
        {
            _logger.LogDebug("No BEP 46 record found at {Target}", target);
            return null;
        }

        if (!TryReadInfoHash(mutable.Value, out var infoHash))
        {
            _logger.LogWarning("BEP 46 record at {Target} does not carry a usable '{Key}' value", target, InfoHashKey);
            return null;
        }

        return new Bep46Resolution(infoHash, mutable.SequenceNumber);
    }

    /// <summary>
    /// Publishes a new version, replacing whatever the key currently points at.
    /// </summary>
    /// <param name="seed">The publisher's 32-byte private seed.</param>
    /// <param name="infoHash">The info-hash of the new version. Must be a v1 (20-byte) hash.</param>
    /// <param name="sequenceNumber">
    /// The new version number. Must exceed the previously published one, or storage nodes will
    /// reject it - use <see cref="ResolveAsync"/> first if the current value is unknown.
    /// </param>
    /// <param name="salt">Optional salt.</param>
    /// <param name="compareAndSwap">
    /// Optional expected current sequence number, so a concurrent update by another instance of
    /// the same publisher is detected rather than silently overwritten.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of nodes that accepted the record.</returns>
    public async Task<int> PublishAsync(
        byte[] seed,
        InfoHash infoHash,
        long sequenceNumber,
        byte[]? salt = null,
        long? compareAndSwap = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seed);

        // BEP 46 records carry a v1 info-hash. A v2 hash is 32 bytes and would not fit the
        // contract subscribers expect, so reject it rather than truncate silently.
        if (infoHash.Length != InfoHash.V1Length)
        {
            throw new ArgumentException(
                $"A BEP 46 record carries a {InfoHash.V1Length}-byte v1 info-hash; this one is {infoHash.Length} bytes.",
                nameof(infoHash));
        }

        var value = new BDict();
        value.Dict[InfoHashKey] = new BString(infoHash.Span.ToArray());

        var item = DhtItemCodec.CreateSigned(seed, salt ?? [], sequenceNumber, value);

        int accepted = await _dht.PutItemAsync(item, compareAndSwap, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Published BEP 46 version {Sequence} pointing at {InfoHash}; accepted by {Accepted} node(s)",
            sequenceNumber,
            infoHash,
            accepted);

        return accepted;
    }

    /// <summary>
    /// Publishes the next version, reading the current sequence number first so the caller does
    /// not have to track it. Uses compare-and-swap, so a concurrent publish by another instance
    /// fails loudly instead of one silently winning.
    /// </summary>
    /// <returns>
    /// The number of nodes that accepted the record, and the sequence number used.
    /// </returns>
    public async Task<(int Accepted, long SequenceNumber)> PublishNextAsync(
        byte[] seed,
        InfoHash infoHash,
        byte[]? salt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seed);

        var publicKey = Ed25519.PublicKeyFromSeed(seed);
        var current = await ResolveAsync(publicKey, salt, cancellationToken).ConfigureAwait(false);

        long next = (current?.SequenceNumber ?? -1) + 1;
        long? cas = current?.SequenceNumber;

        int accepted = await PublishAsync(seed, infoHash, next, salt, cas, cancellationToken).ConfigureAwait(false);
        return (accepted, next);
    }

    /// <summary>
    /// Reads the info-hash out of a record's value, tolerating anything malformed - this is
    /// attacker-supplied data that merely happens to be correctly signed.
    /// </summary>
    private static bool TryReadInfoHash(IBNode value, out InfoHash infoHash)
    {
        infoHash = InfoHash.Empty;

        if (value is not BDict dict || dict.GetBytes(InfoHashKey) is not { } raw)
        {
            return false;
        }

        if (raw.Length != InfoHash.V1Length)
        {
            return false;
        }

        infoHash = new InfoHash(raw.Span);
        return true;
    }
}
