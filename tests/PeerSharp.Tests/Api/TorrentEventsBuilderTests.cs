namespace PeerSharp.Tests.Api;

public class TorrentEventsBuilderTests
{
    [Fact]
    public void Build_WithAllHandlers_SetsAllProperties()
    {
        // Arrange
        var builder = new TorrentEventsBuilder();
        bool pieceCompletedCalled = false;
        bool progressChangedCalled = false;
        bool transferStatsCalled = false;
        bool stateChangedCalled = false;
        bool errorCalled = false;
        bool finishedCalled = false;
        bool metadataProgressCalled = false;
        bool metadataReceivedCalled = false;

        // Act
        var events = builder
            .OnPieceCompleted((t, p) => pieceCompletedCalled = true)
            .OnProgressChanged((t, p) => progressChangedCalled = true)
            .OnTransferStats((t, s) => transferStatsCalled = true)
            .OnStateChanged((t, s) => stateChangedCalled = true)
            .OnError((t, e) => errorCalled = true)
            .OnFinished((t, f) => finishedCalled = true)
            .OnMetadataProgress((t, p) => metadataProgressCalled = true)
            .OnMetadataReceived(t => metadataReceivedCalled = true)
            .Build();

        // Assert
        Assert.NotNull(events.PieceCompleted);
        Assert.NotNull(events.ProgressChanged);
        Assert.NotNull(events.TransferStats);
        Assert.NotNull(events.StateChanged);
        Assert.NotNull(events.Error);
        Assert.NotNull(events.Finished);
        Assert.NotNull(events.MetadataProgress);
        Assert.NotNull(events.MetadataReceived);

        // Verify they are the same handlers by invoking them
        events.PieceCompleted!(null!, default);
        events.ProgressChanged!(null!, default);
        events.TransferStats!(null!, default);
        events.StateChanged!(null!, default);
        events.Error!(null!, new Exception());
        events.Finished!(null!, false);
        events.MetadataProgress!(null!, default);
        events.MetadataReceived!(null!);

        Assert.True(pieceCompletedCalled);
        Assert.True(progressChangedCalled);
        Assert.True(transferStatsCalled);
        Assert.True(stateChangedCalled);
        Assert.True(errorCalled);
        Assert.True(finishedCalled);
        Assert.True(metadataProgressCalled);
        Assert.True(metadataReceivedCalled);
    }

    [Fact]
    public void Build_WithNoHandlers_PropertiesAreNull()
    {
        // Arrange & Act
        var events = new TorrentEventsBuilder().Build();

        // Assert
        Assert.Null(events.PieceCompleted);
        Assert.Null(events.ProgressChanged);
        Assert.Null(events.TransferStats);
        Assert.Null(events.StateChanged);
        Assert.Null(events.Error);
        Assert.Null(events.Finished);
        Assert.Null(events.MetadataProgress);
        Assert.Null(events.MetadataReceived);
    }

    [Fact]
    public void Build_PartialHandlers_SetsOnlyProvided()
    {
        // Arrange
        var builder = new TorrentEventsBuilder();

        // Act
        var events = builder
            .OnFinished((t, f) => { })
            .Build();

        // Assert
        Assert.NotNull(events.Finished);
        Assert.Null(events.PieceCompleted);
        Assert.Null(events.ProgressChanged);
        Assert.Null(events.TransferStats);
        Assert.Null(events.StateChanged);
        Assert.Null(events.Error);
        Assert.Null(events.MetadataProgress);
        Assert.Null(events.MetadataReceived);
    }
}





