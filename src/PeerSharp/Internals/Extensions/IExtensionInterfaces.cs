using PeerSharp.Internals.Peers;
using System.Net;
using PeerSharp.Messages;

namespace PeerSharp.Internals.Extensions;

internal interface IPeerCommunication
{
    IPeerListener Listener { get; }
    byte[] PeerId { get; }
    IPEndPoint? RemoteEndPoint { get; }
    ExtensionHandshake? RemoteExtensions { get; }
    bool RemoteSupportsExtensions { get; }
    IUtHashPiece? UtHashPiece { get; }
    IUtHolepunch UtHolepunch { get; }
    IUtMetadata UtMetadata { get; }
    IUtPex UtPex { get; }

    Task SendMessageAsync(PeerMessage msg);

    Task SetInterestedAsync(bool interested);
}

internal interface IUtHolepunch
{
    int? LocalMessageId { get; }
    int? RemoteMessageId { get; }

    void Init(ExtensionHandshake handshake);

    void SetLocalMessageId(int id);

    void SendConnect(IPEndPoint endpoint);

    void SendError(IPEndPoint endpoint, UtHolepunch.ErrorCode error);

    void SendRendezvous(IPEndPoint endpoint);
}

internal interface IUtMetadata
{
    int? LocalMessageId { get; }
    int? RemoteMessageId { get; }

    void Init(ExtensionHandshake handshake);

    void SetLocalMessageId(int id);

    void SendData(int piece, byte[] data, int totalSize);

    void SendReject(int piece);

    void SendRequest(int piece);
}

internal interface IUtPex
{
    int? LocalMessageId { get; }
    int? RemoteMessageId { get; }

    void Init(ExtensionHandshake handshake);

    void SetLocalMessageId(int id);

    Task HandleMessageAsync(byte[] data);

    void SendPex(List<IPEndPoint> added, List<byte> addedFlags, List<IPEndPoint> dropped);

    void Update(IEnumerable<(IPEndPoint Ep, byte Flags)> peers);
}

internal interface IUtHashPiece
{
    int? LocalMessageId { get; }
    int? RemoteMessageId { get; set; }

    void SetLocalMessageId(int id);

    bool CanVerifyPiece(int pieceIndex);

    byte[]? GetPieceHash(int pieceIndex);

    void HandleMessage(byte[] data);

    void RequestHashes(int pieceIndex);

    void SetPieceHash(int pieceIndex, byte[] hash);

    bool VerifyPiece(int pieceIndex, byte[] pieceData);
}
