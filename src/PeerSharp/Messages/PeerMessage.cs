namespace PeerSharp.Messages;

internal class PeerMessage : IDisposable
{
    private AtomicDisposal _disposal = new();

    public PeerMessage(MessageId id)
    {
        Id = id;
    }

    public int BlockLength { get; set; }
    public int BlockOffset { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public byte[]? HandshakeInfoHash { get; set; }
    public byte[]? HandshakePeerId { get; set; }

    // Handshake specific
    public byte[]? HandshakeReserved { get; set; }

    public int HashBaseLayer { get; set; }
    public int HashIndex { get; set; }
    public int HashLength { get; set; }
    public int HashProofLayers { get; set; }
    public byte[]? HashPiecesRoot { get; set; }

    // Have
    public int HavePieceIndex { get; set; }

    public MessageId Id { get; set; }

    // Request/Piece/Cancel
    public int PieceIndex { get; set; }

    public Block? PooledBlock { get; set; }

    // Port
    public ushort Port { get; set; }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposal.MarkDisposed() && disposing)
        {
            PooledBlock?.Dispose();
        }
    }
}
