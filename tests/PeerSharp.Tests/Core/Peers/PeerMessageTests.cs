using PeerSharp.Messages;

namespace PeerSharp.Tests.Core.Peers;

public class PeerMessageTests
{
    [Fact]
    public void Dispose_DisposesPooledBlock()
    {
        var block = new Block(16);
        var message = new PeerMessage(MessageId.Piece)
        {
            PooledBlock = block
        };

        message.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = block.Buffer);
    }

    [Fact]
    public void Dispose_Twice_IsSafe()
    {
        var block = new Block(8);
        var message = new PeerMessage(MessageId.Piece)
        {
            PooledBlock = block
        };

        message.Dispose();
        message.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = block.Buffer);
    }
}





