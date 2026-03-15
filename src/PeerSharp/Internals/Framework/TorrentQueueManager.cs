namespace PeerSharp.Internals.Framework;

internal sealed class TorrentQueueManager
{
    private readonly QueueSettings _settings;
    private readonly TimeProvider _timeProvider;

    public TorrentQueueManager(QueueSettings settings, TimeProvider timeProvider)
    {
        _settings = settings;
        _timeProvider = timeProvider;
    }

    internal QueuePlan BuildPlan(IReadOnlyList<Torrent> torrents)
    {
        var now = _timeProvider.GetUtcNow();
        var items = new List<QueueItem>(torrents.Count);
        foreach (var torrent in torrents)
        {
            items.Add(new QueueItem
            {
                Hash = torrent.Hash,
                Started = torrent.Started,
                Finished = torrent.Finished,
                QueueAutoStart = torrent.QueueAutoStart,
                QueuePriority = torrent.QueuePriority,
                TimeAdded = torrent.TimeAdded,
                Ratio = torrent.GetRatio(),
                SeedingTime = torrent.GetSeedingTime(now),
                RatioLimit = torrent.RatioLimit,
                SeedTimeLimit = torrent.SeedTimeLimit
            });
        }

        return BuildPlan(items);
    }

    internal QueuePlan BuildPlan(IReadOnlyList<QueueItem> items)
    {
        var plan = new QueuePlan();
        if (!_settings.Enabled && !_settings.EnforceAutoStop)
        {
            return plan;
        }

        var stopSet = new HashSet<InfoHash>();

        if (_settings.EnforceAutoStop)
        {
            foreach (var item in items)
            {
                if (!item.Started || !item.Finished)
                {
                    continue;
                }

                if (item.RatioLimit.HasValue && item.Ratio >= item.RatioLimit.Value)
                {
                    stopSet.Add(item.Hash);
                    continue;
                }

                if (item.SeedTimeLimit.HasValue && item.SeedingTime >= item.SeedTimeLimit.Value)
                {
                    stopSet.Add(item.Hash);
                }
            }
        }

        if (!_settings.Enabled)
        {
            plan.Stop.AddRange(stopSet);
            return plan;
        }

        int activeDownloads = items.Count(i => i.Started && !i.Finished && !stopSet.Contains(i.Hash));
        int activeSeeds = items.Count(i => i.Started && i.Finished && !stopSet.Contains(i.Hash));

        if (_settings.MaxActiveDownloads > 0 && activeDownloads > _settings.MaxActiveDownloads)
        {
            int toStop = activeDownloads - _settings.MaxActiveDownloads;
            foreach (var item in OrderForStop(items.Where(i => i.Started && !i.Finished && !stopSet.Contains(i.Hash))))
            {
                stopSet.Add(item.Hash);
                if (--toStop == 0)
                {
                    break;
                }
            }
        }

        if (_settings.MaxActiveSeeds > 0 && activeSeeds > _settings.MaxActiveSeeds)
        {
            int toStop = activeSeeds - _settings.MaxActiveSeeds;
            foreach (var item in OrderForStop(items.Where(i => i.Started && i.Finished && !stopSet.Contains(i.Hash))))
            {
                stopSet.Add(item.Hash);
                if (--toStop == 0)
                {
                    break;
                }
            }
        }

        plan.Stop.AddRange(stopSet);

        activeDownloads = items.Count(i => i.Started && !i.Finished && !stopSet.Contains(i.Hash));
        activeSeeds = items.Count(i => i.Started && i.Finished && !stopSet.Contains(i.Hash));

        int downloadSlots = _settings.MaxActiveDownloads == 0
            ? int.MaxValue
            : Math.Max(0, _settings.MaxActiveDownloads - activeDownloads);

        int seedSlots = _settings.MaxActiveSeeds == 0
            ? int.MaxValue
            : Math.Max(0, _settings.MaxActiveSeeds - activeSeeds);

        if (downloadSlots > 0)
        {
            foreach (var item in OrderForStart(items.Where(i => !i.Started && !i.Finished && i.QueueAutoStart)))
            {
                plan.Start.Add(item.Hash);
                if (--downloadSlots == 0)
                {
                    break;
                }
            }
        }

        if (seedSlots > 0)
        {
            foreach (var item in OrderForStart(items.Where(i => !i.Started && i.Finished && i.QueueAutoStart)))
            {
                plan.Start.Add(item.Hash);
                if (--seedSlots == 0)
                {
                    break;
                }
            }
        }

        return plan;
    }

    private static IEnumerable<QueueItem> OrderForStart(IEnumerable<QueueItem> items)
    {
        return items.OrderByDescending(i => i.QueuePriority)
            .ThenBy(i => i.TimeAdded);
    }

    private static IEnumerable<QueueItem> OrderForStop(IEnumerable<QueueItem> items)
    {
        return items.OrderBy(i => i.QueuePriority)
            .ThenByDescending(i => i.TimeAdded);
    }

    internal sealed class QueueItem
    {
        public bool Finished { get; init; }
        public InfoHash Hash { get; init; }
        public bool QueueAutoStart { get; init; }
        public int QueuePriority { get; init; }
        public double Ratio { get; init; }
        public float? RatioLimit { get; init; }
        public TimeSpan SeedingTime { get; init; }
        public TimeSpan? SeedTimeLimit { get; init; }
        public bool Started { get; init; }
        public DateTimeOffset TimeAdded { get; init; }
    }

    internal sealed class QueuePlan
    {
        public List<InfoHash> Start { get; } = new();
        public List<InfoHash> Stop { get; } = new();
    }
}
