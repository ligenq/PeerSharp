using PeerSharp.PieceWriter;

namespace PeerSharp.Tests.Core.IO;

public class StorageExceptionTests
{
    [Fact]
    public void Constructor_SetsRecoverableAndInner()
    {
        var inner = new IOException("disk");

        var ex = new StorageException("failed", inner, isRecoverable: false);

        Assert.Equal("failed", ex.Message);
        Assert.Same(inner, ex.InnerException);
        Assert.False(ex.IsRecoverable);
    }

    [Fact]
    public void Constructor_Defaults_Work()
    {
        var ex = new StorageException();
        Assert.False(ex.IsRecoverable);
    }

    [Fact]
    public void Constructor_Message_StoresMessage()
    {
        var ex = new StorageException("oops");
        Assert.Equal("oops", ex.Message);
    }
}




