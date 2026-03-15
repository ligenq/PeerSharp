using PeerSharp.Internals.Peers;
using System.Net;

namespace PeerSharp.Tests.Core.Peers;

public class PeerHistoryTests
{
    private static readonly IPEndPoint DefaultEp = new IPEndPoint(IPAddress.Loopback, 80);

    [Fact]
    public void GetScore_PreferExchangedData()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var p1 = new PeerHistory { EndPoint = DefaultEp, ExchangedData = true, LastAttempt = now };
        var p2 = new PeerHistory { EndPoint = DefaultEp, ExchangedData = false, LastAttempt = now };

        // Act & Assert
        Assert.True(p1.GetScore(false, Priority.Normal, now) < p2.GetScore(false, Priority.Normal, now));
    }

    [Fact]
    public void GetScore_PreferEarlierAttempt()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var p1 = new PeerHistory { EndPoint = DefaultEp, LastAttempt = now.AddMinutes(-5) };
        var p2 = new PeerHistory { EndPoint = DefaultEp, LastAttempt = now.AddMinutes(-1) };

        // Act & Assert
        Assert.True(p1.GetScore(false, Priority.Normal, now) < p2.GetScore(false, Priority.Normal, now));
    }

    [Fact]
    public void GetScore_PreferHigherPriorityTorrent()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var history = new PeerHistory { EndPoint = DefaultEp, LastAttempt = now };

        // Act
        long high = history.GetScore(false, Priority.High, now);
        long normal = history.GetScore(false, Priority.Normal, now);
        long low = history.GetScore(false, Priority.Low, now);

        // Assert
        Assert.True(high < normal);
        Assert.True(normal < low);
    }

    [Fact]
    public void GetScore_PreferConnectable()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var p1 = new PeerHistory { EndPoint = DefaultEp, IsConnectable = true, LastAttempt = now };
        var p2 = new PeerHistory { EndPoint = DefaultEp, IsConnectable = false, LastAttempt = now };

        // Act & Assert
        Assert.True(p1.GetScore(false, Priority.Normal, now) < p2.GetScore(false, Priority.Normal, now));
    }

    [Fact]
    public void GetScore_PreferNonSeedWhenDownloading()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var p1 = new PeerHistory { EndPoint = DefaultEp, IsSeed = false, LastAttempt = now };
        var p2 = new PeerHistory { EndPoint = DefaultEp, IsSeed = true, LastAttempt = now };

        // Act & Assert
        Assert.True(p1.GetScore(false, Priority.Normal, now) < p2.GetScore(false, Priority.Normal, now));
    }

    [Fact]
    public void UpdateSource_PrefersBestRank()
    {
        var history = new PeerHistory { EndPoint = DefaultEp };

        history.UpdateSource(PeerSourceKind.Pex);
        history.UpdateSource(PeerSourceKind.Tracker);

        Assert.Equal((int)PeerSourceKind.Tracker, history.BestSourceRank);
    }

    [Fact]
    public void RegisterUtpFailure_BacksOffAndDisablesAfterLimit()
    {
        var settings = new ConnectionSettings
        {
            UtpPenaltyBaseSeconds = 10,
            UtpPenaltyMaxSeconds = 40,
            UtpFailureHardLimit = 3
        };
        var now = DateTimeOffset.UtcNow;
        var history = new PeerHistory { EndPoint = DefaultEp };

        history.RegisterUtpFailure(now, settings);
        Assert.Equal(now.AddSeconds(10), history.UtpPenaltyUntil);
        Assert.True(history.UtpSupported);

        history.RegisterUtpFailure(now, settings);
        Assert.Equal(now.AddSeconds(20), history.UtpPenaltyUntil);
        Assert.True(history.UtpSupported);

        history.RegisterUtpFailure(now, settings);
        Assert.Equal(now.AddSeconds(40), history.UtpPenaltyUntil);
        Assert.False(history.UtpSupported);
    }

    [Fact]
    public void RegisterUtpSlow_RespectsCooldown()
    {
        var settings = new ConnectionSettings
        {
            UtpSlowPenaltySeconds = 30,
            UtpSlowPenaltyCooldownSeconds = 60,
            UtpFailureHardLimit = 5
        };
        var now = DateTimeOffset.UtcNow;
        var history = new PeerHistory { EndPoint = DefaultEp };

        Assert.True(history.RegisterUtpSlow(now, settings));
        var firstPenalty = history.UtpPenaltyUntil;

        Assert.False(history.RegisterUtpSlow(now.AddSeconds(10), settings));
        Assert.Equal(firstPenalty, history.UtpPenaltyUntil);
    }

    [Fact]
    public void GetScore_PrefersLowerSourceRank()
    {
        var now = DateTimeOffset.UtcNow;
        var p1 = new PeerHistory { EndPoint = DefaultEp, LastAttempt = now, BestSourceRank = (int)PeerSourceKind.Tracker };
        var p2 = new PeerHistory { EndPoint = DefaultEp, LastAttempt = now, BestSourceRank = (int)PeerSourceKind.Unknown };

        Assert.True(p1.GetScore(false, Priority.Normal, now) < p2.GetScore(false, Priority.Normal, now));
    }

    [Fact]
    public void GetScore_PenalizesFruitlessConnections()
    {
        var now = DateTimeOffset.UtcNow;
        var p1 = new PeerHistory { EndPoint = DefaultEp, LastAttempt = now, FruitlessConnectionCount = 0, ExchangedData = false };
        var p2 = new PeerHistory { EndPoint = DefaultEp, LastAttempt = now, FruitlessConnectionCount = 2, ExchangedData = false };

        Assert.True(p1.GetScore(false, Priority.Normal, now) < p2.GetScore(false, Priority.Normal, now));
    }
}





