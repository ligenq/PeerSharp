using PeerSharp.Internals.Peers;

namespace PeerSharp.Tests.Core.Peers;

public class ConnectionGovernorTests
{
    [Fact]
    public void TryAcquireConnectionSlot_UnderLimit_IncrementsAndReturnsTrue()
    {
        // Arrange
        var settings = new Settings();
        settings.Connection.MaxConnections = 2;
        var governor = new ConnectionGovernor(settings);

        // Act & Assert
        Assert.True(governor.TryAcquireConnectionSlot());
        Assert.Equal(1, governor.ActiveConnections);
        Assert.True(governor.TryAcquireConnectionSlot());
        Assert.Equal(2, governor.ActiveConnections);
    }

    [Fact]
    public void TryAcquireConnectionSlot_AtLimit_ReturnsFalse()
    {
        // Arrange
        var settings = new Settings();
        settings.Connection.MaxConnections = 1;
        var governor = new ConnectionGovernor(settings);

        // Act
        governor.TryAcquireConnectionSlot();
        bool result = governor.TryAcquireConnectionSlot();

        // Assert
        Assert.False(result);
        Assert.Equal(1, governor.ActiveConnections);
    }

    [Fact]
    public void ReleaseConnectionSlot_Decrements()
    {
        // Arrange
        var settings = new Settings();
        var governor = new ConnectionGovernor(settings);
        governor.TryAcquireConnectionSlot();

        // Act
        governor.ReleaseConnectionSlot();

        // Assert
        Assert.Equal(0, governor.ActiveConnections);
    }

    [Fact]
    public void ReleaseConnectionSlot_BelowZero_ClampsToZero()
    {
        // Arrange
        var settings = new Settings();
        var governor = new ConnectionGovernor(settings);

        // Act
        governor.ReleaseConnectionSlot();

        // Assert
        Assert.Equal(0, governor.ActiveConnections);
    }

    [Fact]
    public void TryAcquirePendingSlot_EnforcesLimit()
    {
        // Arrange
        var settings = new Settings();
        settings.Connection.MaxPendingConnections = 1;
        var governor = new ConnectionGovernor(settings);

        // Act & Assert
        Assert.True(governor.TryAcquirePendingSlot());
        Assert.False(governor.TryAcquirePendingSlot());
        Assert.Equal(1, governor.PendingConnections);

        governor.ReleasePendingSlot();
        Assert.Equal(0, governor.PendingConnections);
        Assert.True(governor.TryAcquirePendingSlot());
    }
}





