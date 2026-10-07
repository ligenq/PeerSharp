using PeerSharp.Internals.Transfers;

namespace PeerSharp.Tests.Core.Transfers;

public class WaitingRequestsTests
{
    [Fact]
    public void OnlyAsManyAreRefusedAsWereCancelled_WhileTheyWait()
    {
        var waiting = new WaitingRequests();
        waiting.Cancel(1, 0); // nothing waiting: ignored
        waiting.Add(1, 0);
        waiting.Add(1, 0);
        waiting.Cancel(1, 0);
        waiting.Cancel(1, 0);
        waiting.Cancel(1, 0); // more cancels than requests waiting: capped

        Assert.True(waiting.Take(1, 0));
        Assert.True(waiting.Take(1, 0));
        Assert.False(waiting.Take(1, 0));

        waiting.Add(1, 0);
        Assert.False(waiting.Take(1, 0));
    }
}
