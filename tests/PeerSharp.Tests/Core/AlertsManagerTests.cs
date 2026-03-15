using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;

namespace PeerSharp.Tests.Core;

public class AlertsManagerTests
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly AlertsManager _alertsManager;

    public AlertsManagerTests()
    {
        _alertsManager = new AlertsManager(_timeProvider);
    }

    [Fact]
    public void RegisterAlerts_SetsMask()
    {
        uint mask = (uint)(AlertId.TorrentAdded | AlertId.TorrentRemoved);
        _alertsManager.RegisterAlerts(mask);

        // No public way to get mask, but we can test side effects
        var torrent = TorrentTestUtility.CreateMinimal();
        _alertsManager.TorrentAlert(AlertId.TorrentAdded, torrent);
        _alertsManager.TorrentAlert(AlertId.TorrentStarted, torrent); // Not in mask

        var alerts = _alertsManager.PopAlerts();
        Assert.Single(alerts);
        Assert.Equal(AlertId.TorrentAdded, alerts[0].Id);
    }

    [Fact]
    public void PostAlert_OnlyAddsRegisteredAlerts()
    {
        _alertsManager.RegisterAlerts((uint)AlertId.TorrentAdded);

        var torrent = TorrentTestUtility.CreateMinimal();
        _alertsManager.PostAlert(new SimpleTorrentAlert { Id = AlertId.TorrentAdded, Torrent = torrent });
        _alertsManager.PostAlert(new SimpleTorrentAlert { Id = AlertId.TorrentRemoved, Torrent = torrent });

        var alerts = _alertsManager.PopAlerts();
        Assert.Single(alerts);
        Assert.Equal(AlertId.TorrentAdded, alerts[0].Id);
    }

    [Fact]
    public void PopAlerts_ClearsQueue()
    {
        _alertsManager.RegisterAlerts(uint.MaxValue);
        var torrent = TorrentTestUtility.CreateMinimal();
        _alertsManager.TorrentAlert(AlertId.TorrentAdded, torrent);

        var alerts1 = _alertsManager.PopAlerts();
        Assert.Single(alerts1);

        var alerts2 = _alertsManager.PopAlerts();
        Assert.Empty(alerts2);
    }

    [Fact]
    public void MaxAlertQueueSize_Enforced()
    {
        _alertsManager.RegisterAlerts(uint.MaxValue);
        var torrent = TorrentTestUtility.CreateMinimal();

        // Max is 10000 now. Use non-critical alert.
        for (int i = 0; i < 10001; i++)
        {
            _alertsManager.ProgressChangedAlert(torrent, 0, 0, 0, 0, 0, 0);
        }

        var alerts = _alertsManager.PopAlerts();
        Assert.Equal(10000, alerts.Count);
    }

    [Fact]
    public void MaxAlertQueueSize_DoesNotDropCriticalAlerts()
    {
        _alertsManager.RegisterAlerts(uint.MaxValue);
        var torrent = TorrentTestUtility.CreateMinimal();

        // Fill the queue with non-critical alerts
        for (int i = 0; i < 10000; i++)
        {
            _alertsManager.ProgressChangedAlert(torrent, 0, 0, 0, 0, 0, 0);
        }

        // Post a critical alert
        _alertsManager.TorrentAlert(AlertId.TorrentRemoved, torrent);

        var alerts = _alertsManager.PopAlerts();
        // Should drop a non-critical one to make room
        Assert.Equal(10000, alerts.Count);
        Assert.Equal(AlertId.TorrentRemoved, alerts[^1].Id);
    }

    [Fact]
    public void SpecializedAlertMethods_Work()
    {
        _alertsManager.RegisterAlerts(uint.MaxValue);
        var torrent = TorrentTestUtility.CreateMinimal();
        var now = DateTimeOffset.UtcNow;
        _timeProvider.SetUtcNow(now);

        _alertsManager.TorrentAlert(AlertId.TorrentAdded, torrent);
        _alertsManager.MetadataAlert(AlertId.MetadataInitialized, torrent);
        _alertsManager.ConfigAlert(AlertId.ConfigChanged, "Network");
        _alertsManager.PieceCompletedAlert(torrent, 5, 10, 100);
        _alertsManager.ProgressChangedAlert(torrent, 0.5f, 0.4f, 500, 1000, 10, 100);
        _alertsManager.TransferStatsAlert(torrent, 100, 200, 10, 20, 5);
        _alertsManager.StateChangedAlert(torrent, TorrentState.Stopped, TorrentState.Active);
        _alertsManager.TorrentErrorAlert(torrent, new Exception("error"));
        _alertsManager.MetadataProgressAlert(torrent, 0.3f, 3, 10);

        var alerts = _alertsManager.PopAlerts();
        Assert.Equal(9, alerts.Count);

        Assert.All(alerts, a => Assert.Equal(now, a.Timestamp));
        Assert.IsType<SimpleTorrentAlert>(alerts[0]);
        Assert.IsType<SimpleMetadataAlert>(alerts[1]);
        Assert.IsType<ConfigAlert>(alerts[2]);
        Assert.IsType<PieceCompletedAlert>(alerts[3]);
        Assert.IsType<ProgressChangedAlert>(alerts[4]);
        Assert.IsType<TransferStatsAlert>(alerts[5]);
        Assert.IsType<StateChangedAlert>(alerts[6]);
        Assert.IsType<TorrentErrorAlert>(alerts[7]);
        Assert.IsType<MetadataProgressAlert>(alerts[8]);
    }
}





