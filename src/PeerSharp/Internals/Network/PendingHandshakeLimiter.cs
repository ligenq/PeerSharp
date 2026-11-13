using System.Net;

namespace PeerSharp.Internals.Network;

/// <summary>
/// Bounds inbound connections that have been accepted but have not yet finished the BitTorrent,
/// MSE or TLS handshake.
///
/// Peer limits only apply once a handshake names a torrent, so without this a remote host could
/// open sockets as fast as we accept them and hold each one for the full handshake deadline -
/// every one costing a socket, buffers and, for MSE, a Diffie-Hellman exchange.
/// </summary>
internal sealed class PendingHandshakeLimiter(int maxTotal = PendingHandshakeLimiter.DefaultMaxTotal, int maxPerAddress = PendingHandshakeLimiter.DefaultMaxPerAddress)
{
    internal const int DefaultMaxTotal = 128;
    internal const int DefaultMaxPerAddress = 8;

    private readonly Lock _lock = new();
    private readonly Dictionary<IPAddress, int> _perAddress = [];
    private int _total;

    public int Pending
    {
        get
        {
            lock (_lock)
            {
                return _total;
            }
        }
    }

    /// <summary>
    /// Claims a slot for a handshake from <paramref name="address"/>. Every successful call must be
    /// paired with <see cref="Exit"/> for the same address.
    /// </summary>
    public bool TryEnter(IPAddress? address)
    {
        address = Normalize(address);
        lock (_lock)
        {
            if (_total >= maxTotal)
            {
                return false;
            }

            if (address != null)
            {
                int current = _perAddress.GetValueOrDefault(address);
                if (current >= maxPerAddress)
                {
                    return false;
                }

                _perAddress[address] = current + 1;
            }

            _total++;
            return true;
        }
    }

    public void Exit(IPAddress? address)
    {
        address = Normalize(address);
        lock (_lock)
        {
            if (_total > 0)
            {
                _total--;
            }

            if (address != null && _perAddress.TryGetValue(address, out int current))
            {
                if (current <= 1)
                {
                    _perAddress.Remove(address);
                }
                else
                {
                    _perAddress[address] = current - 1;
                }
            }
        }
    }

    private static IPAddress? Normalize(IPAddress? address)
        => address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;
}
