using PeerSharp.Interfaces;

namespace PeerSharp.Core;

/// <summary>
/// Decides whether a hash names a given torrent.
/// </summary>
/// <remarks>
/// <para>
/// A torrent carries a v1 and a v2 hash and almost never has both: a v1 torrent's
/// <see cref="ITorrent.HashV2"/> is <see cref="InfoHash.EmptyV2"/>, a v2 torrent's
/// <see cref="ITorrent.Hash"/> is <see cref="InfoHash.Empty"/>, and only a hybrid has two real ones.
/// Empty hashes are equal to each other, so comparing the stored pairs with <c>==</c> says every
/// torrent lacking a v2 hash is every other torrent lacking one. An absent hash is not evidence of
/// identity, and this is the one place that knows it.
/// </para>
/// <para>
/// Offered as a named method rather than as <c>==</c> on some identity type, because sharing a hash
/// is not an equivalence relation and cannot honestly be equality: a hybrid torrent matches the v1
/// torrent holding its v1 hash and the v2 torrent holding its v2 hash, while those two match nothing
/// of each other. Equality has to be transitive, and a type whose <c>Equals</c> is not corrupts every
/// dictionary it is a key in. So this is a predicate, and says so in its name.
/// </para>
/// <para>
/// For two torrents rather than a torrent and a hash, ask
/// <see cref="ITorrent.HasSameIdentity(ITorrent?)"/>, which has always been the place for it.
/// </para>
/// </remarks>
public static class TorrentIdentity
{
    /// <summary>
    /// Whether <paramref name="hash"/> names <paramref name="torrent"/>, in any of the forms the
    /// world refers to it by.
    /// </summary>
    /// <remarks>
    /// BEP 52 gives a v2 torrent two identities: the full SHA-256 of its info dictionary, and that
    /// hash truncated to twenty bytes. Everything with a twenty-byte field uses the second - the
    /// peer handshake, tracker announces, DHT lookups - so a v2-only torrent is reached almost
    /// entirely by a hash it does not store.
    /// </remarks>
    public static bool HasHash(ITorrent torrent, InfoHash hash)
    {
        ArgumentNullException.ThrowIfNull(torrent);

        if (torrent.Hash.Matches(hash) || torrent.HashV2.Matches(hash))
        {
            return true;
        }

        return hash.IsV1
            && torrent.HashV2.IsV2
            && !torrent.HashV2.IsEmpty
            && torrent.HashV2.TruncateToV1().Matches(hash);
    }
}
