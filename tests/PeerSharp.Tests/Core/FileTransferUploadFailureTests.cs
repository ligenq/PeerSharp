using PeerSharp.Internals;

namespace PeerSharp.Tests.Core;

/// <summary>
/// Which failed reads while serving a block mean the data is gone, and so are worth retracting the
/// piece from the whole swarm over (BEP 54), rather than refusing the one request.
/// </summary>
public class FileTransferUploadFailureTests
{
    public static TheoryData<Exception> DataLoss() =>
    [
        new StorageException("File 'a' is marked failed after repeated I/O errors", null, isRecoverable: false),
        new StorageException("Read failed for file 'a'", new FileNotFoundException(), isRecoverable: true),
        new StorageException("Read failed for file 'a'", new DirectoryNotFoundException(), isRecoverable: true),
        new StorageException("Read failed for file 'a'", new EndOfStreamException(), isRecoverable: true),
        new FileNotFoundException(),
    ];

    public static TheoryData<Exception> NotDataLoss() =>
    [
        // Another process holding the file open: a sharing violation clears when it lets go.
        new StorageException("Read failed for file 'a'", new IOException("The process cannot access the file"), isRecoverable: true),
        new StorageException("Read failed for file 'a'", new UnauthorizedAccessException(), isRecoverable: true),
        new ObjectDisposedException("Storage", "Storage is shutting down"),
        new InvalidOperationException("Storage is not initialized"),
    ];

    [Theory]
    [MemberData(nameof(DataLoss))]
    public void IsDataLoss_WhenTheFileIsGoneOrGivenUpOn(Exception failure)
    {
        Assert.True(FileTransfer.IsDataLoss(failure));
    }

    [Theory]
    [MemberData(nameof(NotDataLoss))]
    public void IsNotDataLoss_WhenTheReadCouldSucceedLater(Exception failure)
    {
        Assert.False(FileTransfer.IsDataLoss(failure));
    }
}
