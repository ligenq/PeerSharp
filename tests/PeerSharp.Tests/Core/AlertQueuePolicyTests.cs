using Microsoft.Extensions.Time.Testing;
using PeerSharp.Config;
using PeerSharp.Internals;

namespace PeerSharp.Tests.Core;

/// <summary>
/// The alert queue's capacity, what gives way when it is full, and how a consumer learns it has gaps.
/// </summary>
public class AlertQueuePolicyTests
{
    private const uint AllAlerts = uint.MaxValue;

    [Fact]
    public void Capacity_IsConfigurable()
    {
        var settings = new AlertSettings { MaxQueueSize = 5 };
        var manager = CreateManager(settings);

        for (int i = 0; i < 50; i++)
        {
            manager.PostAlert(Droppable());
        }

        var popped = manager.PopAlerts();
        Assert.Equal(5, popped.Count(alert => alert.Id != AlertId.AlertsDropped));
    }

    [Fact]
    public void Capacity_IsNotExceededByCriticalAlerts()
    {
        // This is the case that used to be unbounded: every queued alert critical, so nothing could
        // be evicted, so the queue "grew temporarily" - with nothing to stop it.
        var manager = CreateManager(new AlertSettings { MaxQueueSize = 4 });

        for (int i = 0; i < 200; i++)
        {
            manager.PostAlert(Critical());
        }

        var popped = manager.PopAlerts();
        Assert.Equal(4, popped.Count(alert => alert.Id != AlertId.AlertsDropped));
        Assert.Equal(196, manager.DroppedAlertCount);
    }

    [Fact]
    public void Overflow_EvictsADroppableAlertBeforeACriticalOne()
    {
        var manager = CreateManager(new AlertSettings { MaxQueueSize = 3 });

        manager.PostAlert(Critical());
        manager.PostAlert(Droppable());
        manager.PostAlert(Droppable());

        // Full. The next one has to displace something, and it should not be the critical head.
        manager.PostAlert(Droppable());

        var kept = manager.PopAlerts().Where(alert => alert.Id != AlertId.AlertsDropped).ToList();
        Assert.Equal(3, kept.Count);
        Assert.Contains(kept, alert => alert.Id == AlertId.TorrentFinished);
    }

    [Fact]
    public void DropNewest_KeepsTheBacklogAndRefusesTheArrival()
    {
        var manager = CreateManager(new AlertSettings
        {
            MaxQueueSize = 2,
            OverflowPolicy = AlertOverflowPolicy.DropNewest
        });

        var first = Critical();
        var second = Critical();
        manager.PostAlert(first);
        manager.PostAlert(second);
        manager.PostAlert(Critical());

        var kept = manager.PopAlerts().Where(alert => alert.Id != AlertId.AlertsDropped).ToList();
        Assert.Equal([first, second], kept);
        Assert.Equal(1, manager.DroppedAlertCount);
    }

    [Fact]
    public void DropOldest_KeepsTheArrivalAndDiscardsTheHead()
    {
        var manager = CreateManager(new AlertSettings
        {
            MaxQueueSize = 2,
            OverflowPolicy = AlertOverflowPolicy.DropOldest
        });

        var first = Critical();
        var second = Critical();
        var third = Critical();
        manager.PostAlert(first);
        manager.PostAlert(second);
        manager.PostAlert(third);

        var kept = manager.PopAlerts().Where(alert => alert.Id != AlertId.AlertsDropped).ToList();
        Assert.Equal([second, third], kept);
    }

    [Fact]
    public void DroppedAlerts_AreReportedInBandAndCounted()
    {
        var manager = CreateManager(new AlertSettings { MaxQueueSize = 2 });

        for (int i = 0; i < 10; i++)
        {
            manager.PostAlert(Droppable());
        }

        var popped = manager.PopAlerts();

        // First in the batch, so a consumer sees the gap before the alerts either side of it, and
        // it cannot itself be dropped by the overflow it is reporting.
        var notice = Assert.IsType<AlertsDroppedAlert>(popped[0]);
        Assert.Equal(8, notice.Dropped);
        Assert.Equal(8, notice.TotalDropped);
        Assert.Equal(2, notice.Capacity);
        Assert.Equal(8, manager.DroppedAlertCount);

        // Reported once. The running total stays; the per-report count resets.
        Assert.DoesNotContain(manager.PopAlerts(), alert => alert.Id == AlertId.AlertsDropped);
        Assert.Equal(8, manager.DroppedAlertCount);
    }

    [Fact]
    public void DroppedAlerts_AreNotReportedWhenNothingWasDropped()
    {
        var manager = CreateManager(new AlertSettings { MaxQueueSize = 100 });
        manager.PostAlert(Droppable());

        var popped = manager.PopAlerts();

        Assert.Single(popped);
        Assert.Equal(0, manager.DroppedAlertCount);
    }

    [Fact]
    public void Capacity_IsRereadOnEachPost()
    {
        var settings = new AlertSettings { MaxQueueSize = 100 };
        var manager = CreateManager(settings);

        for (int i = 0; i < 50; i++) manager.PostAlert(Droppable());

        settings.MaxQueueSize = 10;
        for (int i = 0; i < 50; i++) manager.PostAlert(Droppable());

        Assert.Equal(10, manager.PopAlerts().Count(alert => alert.Id != AlertId.AlertsDropped));
    }

    [Fact]
    public void Capacity_RejectsValuesBelowOne()
    {
        var settings = new AlertSettings();
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.MaxQueueSize = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.MaxQueueSize = -1);
        Assert.Equal(10000, settings.MaxQueueSize);
    }

    private static AlertsManager CreateManager(AlertSettings settings)
    {
        var manager = new AlertsManager(new FakeTimeProvider(), settings);
        manager.RegisterAlerts(AllAlerts);
        return manager;
    }

    private static Alert Droppable() => new ConfigAlert
    {
        Id = AlertId.ConfigChanged,
        ConfigType = "test",
        Timestamp = DateTimeOffset.UnixEpoch
    };

    // Any alert carrying a critical id: criticality is decided by the id, not the record type.
    private static Alert Critical() => new ListenPortChangedAlert
    {
        Id = AlertId.TorrentFinished,
        RequestedPort = 0,
        ActualPort = 0,
        Transport = ListenTransport.Tcp,
        Timestamp = DateTimeOffset.UnixEpoch
    };
}
