namespace PeerSharp.Tests.Core;

public class TorrentOptionValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(0f)]
    [InlineData(float.MaxValue)]
    public void DisabledAndFiniteRatioLimitsAreSupported(float? ratio)
    {
        var options = new AddTorrentOptions { RatioLimit = ratio, SeedTimeLimit = TimeSpan.Zero };
        var snapshot = options.Snapshot();
        Assert.Equal(ratio, snapshot.RatioLimit);
        Assert.Equal(TimeSpan.Zero, snapshot.SeedTimeLimit);
    }
}
