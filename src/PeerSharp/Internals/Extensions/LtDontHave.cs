using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Messages;
using System.Buffers.Binary;

namespace PeerSharp.Internals.Extensions;

/// <summary>
/// BEP 54: <c>lt_donthave</c>, the retraction the base protocol never had.
///
/// <para>
/// <c>have</c> is one-way: once a piece is advertised there is no way to take it back. So a peer that
/// loses data - a piece that failed verification, a file the user deselected, a cache eviction - keeps
/// being asked for blocks it cannot serve, and looks unreliable rather than honest. This message says
/// "that piece is gone", and the receiver stops asking.
/// </para>
///
/// <para>
/// The message body is a single four-byte piece index. BEP 54 also notes that a peer may send this
/// even without having advertised support itself, so an inbound message is handled whenever the local
/// id matches, regardless of what the remote claimed.
/// </para>
/// </summary>
internal sealed class LtDontHave
{
    public const string Name = "lt_donthave";

    private const int PayloadLength = 4;

    private readonly ILogger<LtDontHave> _logger;
    private readonly IPeerCommunication _peer;

    public LtDontHave(IPeerCommunication peer)
        : this(peer, NullLogger<LtDontHave>.Instance)
    {
    }

    internal LtDontHave(IPeerCommunication peer, ILogger<LtDontHave> logger)
    {
        _peer = peer;
        _logger = logger;
    }

    /// <summary>The id we told the remote to use when sending us this message.</summary>
    public int? LocalMessageId { get; private set; }

    /// <summary>The id the remote told us to use. Null when it does not support the extension.</summary>
    public int? RemoteMessageId { get; private set; }

    public void Init(ExtensionHandshake handshake)
    {
        if (handshake.MessageIds.TryGetValue(Name, out int id))
        {
            RemoteMessageId = id;
        }
    }

    public void SetLocalMessageId(int id)
    {
        LocalMessageId = id;
    }

    /// <summary>
    /// Applies an inbound retraction: the piece is no longer available at this peer.
    /// </summary>
    /// <param name="data">The message body, a four-byte big-endian piece index.</param>
    /// <returns>The retracted piece index, or null when the message was malformed.</returns>
    public int? HandleMessage(ReadOnlySpan<byte> data)
    {
        if (data.Length < PayloadLength)
        {
            _logger.LogDebug("Discarded a malformed lt_donthave from {PeerName}: {Length} byte(s)", _peer.RemoteEndPoint, data.Length);
            return null;
        }

        int index = BinaryPrimitives.ReadInt32BigEndian(data);
        if ((uint)index >= (uint)_peer.PeerPieces.Count)
        {
            // Untrusted input: an out-of-range index would otherwise be a way to poke at the bitfield.
            _logger.LogDebug("Discarded an out-of-range lt_donthave index {Index} from {PeerName}", index, _peer.RemoteEndPoint);
            return null;
        }

        _peer.PeerPieces.RemovePiece(index);
        _logger.LogDebug("{PeerName} no longer has piece {Index} (lt_donthave)", _peer.RemoteEndPoint, index);
        return index;
    }

    /// <summary>
    /// Tells the peer we have lost a piece. No-op when it does not support the extension.
    /// </summary>
    public Task SendAsync(int pieceIndex)
    {
        if (RemoteMessageId is not { } remoteId)
        {
            return Task.CompletedTask;
        }

        var msg = new PeerMessage(MessageId.Extended)
        {
            Data = new byte[1 + PayloadLength]
        };

        msg.Data[0] = (byte)remoteId;
        BinaryPrimitives.WriteInt32BigEndian(msg.Data.AsSpan(1), pieceIndex);

        return _peer.SendMessageAsync(msg);
    }
}
