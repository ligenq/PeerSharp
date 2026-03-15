namespace PeerSharp.Messages;

internal enum MessageId : byte
{
    Choke = 0,
    Unchoke = 1,
    Interested = 2,
    NotInterested = 3,
    Have = 4,
    Bitfield = 5,
    Request = 6,
    Piece = 7,
    Cancel = 8,
    Port = 9,
    Suggest = 13,
    HaveAll = 14,
    HaveNone = 15,
    Reject = 16,
    AllowedFast = 17,
    Extended = 20,
    Handshake = 254, // Custom ID for internal use
    KeepAlive = 255, // Custom ID for internal use
    Invalid = 253
}
