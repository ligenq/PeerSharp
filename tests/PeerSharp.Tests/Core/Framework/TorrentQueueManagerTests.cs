using PeerSharp.Internals.Framework;

namespace PeerSharp.Tests.Core.Framework;

public class TorrentQueueManagerTests
{
    [Fact]
    public void BuildPlan_RatioLimitExceeded_StopsTorrent()
    {
        var settings = new QueueSettings { Enabled = false, EnforceAutoStop = true };
        var manager = new TorrentQueueManager(settings, TimeProvider.System);
        var hash = InfoHash.CreateRandom();

        var items = new[]
        {
            new TorrentQueueManager.QueueItem
            {
                Hash = hash,
                Started = true,
                Finished = true,
                QueueAutoStart = true,
                QueuePriority = 0,
                TimeAdded = DateTimeOffset.UtcNow,
                Ratio = 2.0,
                SeedingTime = TimeSpan.Zero,
                RatioLimit = 1.0f
            }
        };

        var plan = manager.BuildPlan(items);

        Assert.Contains(hash, plan.Stop);
        Assert.Empty(plan.Start);
    }

    [Fact]
    public void BuildPlan_SeedTimeExceeded_StopsTorrent()
    {
        var settings = new QueueSettings { Enabled = false, EnforceAutoStop = true };
        var manager = new TorrentQueueManager(settings, TimeProvider.System);
        var hash = InfoHash.CreateRandom();

        var items = new[]
        {
            new TorrentQueueManager.QueueItem
            {
                Hash = hash,
                Started = true,
                Finished = true,
                QueueAutoStart = true,
                QueuePriority = 0,
                TimeAdded = DateTimeOffset.UtcNow,
                Ratio = 0.5,
                SeedingTime = TimeSpan.FromMinutes(30),
                SeedTimeLimit = TimeSpan.FromMinutes(10)
            }
        };

        var plan = manager.BuildPlan(items);

        Assert.Contains(hash, plan.Stop);
        Assert.Empty(plan.Start);
    }

    [Fact]
    public void BuildPlan_StartsHighestPriorityDownload()
    {
        var settings = new QueueSettings { Enabled = true, MaxActiveDownloads = 1, MaxActiveSeeds = 0 };
        var manager = new TorrentQueueManager(settings, TimeProvider.System);

        var low = InfoHash.CreateRandom();
        var high = InfoHash.CreateRandom();
        var time = DateTimeOffset.UtcNow;

        var items = new[]
        {
            new TorrentQueueManager.QueueItem
            {
                Hash = low,
                Started = false,
                Finished = false,
                QueueAutoStart = true,
                QueuePriority = 1,
                TimeAdded = time.AddMinutes(-2),
                Ratio = 0.0,
                SeedingTime = TimeSpan.Zero
            },
            new TorrentQueueManager.QueueItem
            {
                Hash = high,
                Started = false,
                Finished = false,
                QueueAutoStart = true,
                QueuePriority = 10,
                TimeAdded = time.AddMinutes(-1),
                Ratio = 0.0,
                SeedingTime = TimeSpan.Zero
            }
        };

        var plan = manager.BuildPlan(items);

        Assert.Single(plan.Start);
        Assert.Equal(high, plan.Start[0]);
    }

    [Fact]
    public void BuildPlan_StopsLowestPriorityDownloadWhenOverLimit()
    {
        var settings = new QueueSettings { Enabled = true, MaxActiveDownloads = 1, MaxActiveSeeds = 0 };
        var manager = new TorrentQueueManager(settings, TimeProvider.System);

        var low = InfoHash.CreateRandom();
        var high = InfoHash.CreateRandom();

        var items = new[]
        {
            new TorrentQueueManager.QueueItem
            {
                Hash = low,
                Started = true,
                Finished = false,
                QueueAutoStart = true,
                QueuePriority = 1,
                TimeAdded = DateTimeOffset.UtcNow.AddMinutes(-1),
                Ratio = 0.0,
                SeedingTime = TimeSpan.Zero
            },
            new TorrentQueueManager.QueueItem
            {
                Hash = high,
                Started = true,
                Finished = false,
                QueueAutoStart = true,
                QueuePriority = 10,
                TimeAdded = DateTimeOffset.UtcNow.AddMinutes(-2),
                Ratio = 0.0,
                SeedingTime = TimeSpan.Zero
            }
        };

        var plan = manager.BuildPlan(items);

        Assert.Single(plan.Stop);
        Assert.Equal(low, plan.Stop[0]);
    }
}





