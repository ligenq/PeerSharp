using PeerSharp.Internals.Trackers;

namespace PeerSharp.Tests.Core.Trackers;

public class UdpTrackerExceptionTests
{
    [Fact]
    public void Constructor_SetsTransientFlag()
    {
        var ex = new UdpTrackerException("temp", isTransient: true);

        Assert.Equal("temp", ex.Message);
        Assert.True(ex.IsTransient);
    }

    [Fact]
    public void Constructor_WithInner_PreservesInner()
    {
        var inner = new InvalidOperationException("inner");

        var ex = new UdpTrackerException("wrapped", inner, isTransient: false);

        Assert.Same(inner, ex.InnerException);
        Assert.False(ex.IsTransient);
    }
}




