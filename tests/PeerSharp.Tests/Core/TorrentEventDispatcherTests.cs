using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using PeerSharp.Interfaces;

namespace PeerSharp.Tests.Core;

public sealed class TorrentEventDispatcherTests
{
    [Fact]
    public async Task EveryMetadataSubscriberRunsWhenAnEarlierSubscriberThrows()
    {
        await using var torrent = TorrentTestUtility.CreateMinimal();
        int received = 0;
        Action<ITorrent> callbacks = _ => throw new IOException("first subscriber");
        callbacks += _ => received++;
        TorrentEventDispatcher.Invoke(callbacks, torrent, NullLogger.Instance);
        Assert.Equal(1, received);
    }

    [Fact]
    public async Task EveryProgressSubscriberReceivesTheSameValueDespiteSubscriberFailures()
    {
        await using var torrent = TorrentTestUtility.CreateMinimal();
        int received = 0;
        Action<ITorrent, int> callbacks = (_, _) => throw new IOException("first subscriber");
        callbacks += (_, value) => received = value;
        TorrentEventDispatcher.Invoke(callbacks, torrent, 42, NullLogger.Instance);
        Assert.Equal(42, received);
    }
}
