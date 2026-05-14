using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Transfers;
using PeerSharp.Messages;
using System.Net;
using System.Reflection;

namespace PeerSharp.Tests.Core.Transfers;

public class UploadQueueManagerTests
{
    private class MockPeerListener : IPeerListener
    {
        public Task ConnectionClosedAsync(IPeerCommunication peer, int code) => Task.CompletedTask;
        public Task ExtendedHandshakeFinishedAsync(IPeerCommunication peer, ExtensionHandshake handshake) => Task.CompletedTask;
        public Task ExtendedMessageReceivedAsync(IPeerCommunication peer, int type, byte[] data) => Task.CompletedTask;
        public Task HandshakeFinishedAsync(IPeerCommunication peer) => Task.CompletedTask;
        public Task HolepunchMessageReceivedAsync(IPeerCommunication peer, UtHolepunch.MsgId id, IPEndPoint endpoint, UtHolepunch.ErrorCode error) => Task.CompletedTask;
        public Task MessageReceivedAsync(IPeerCommunication peer, PeerMessage msg) => Task.CompletedTask;
        public Task PexReceivedAsync(IPeerCommunication peer, List<IPEndPoint> added, List<byte> addedFlags, List<IPEndPoint> dropped) => Task.CompletedTask;
        public Task PortReceivedAsync(IPeerCommunication peer, ushort dhtPort) => Task.CompletedTask;
    }

    private static PeerCommunication CreateUnchockedPeer()
    {
        var torrent = TorrentTestUtility.CreateMinimal(new TorrentFileMetadata());
        var peer = new PeerCommunication(torrent, new MockPeerListener(), TimeProvider.System);
        peer.Unchoke(); // peers start AmChoking=true by default; must unchoke for pump to execute items
        return peer;
    }

    private static UploadQueueManager CreateManager(
        Func<PeerCommunication, UploadQueueItem, CancellationToken, Task> execute,
        CancellationToken stopToken)
        => new(execute, NullLogger<UploadQueueManager>.Instance, stopToken);

    [Fact]
    public async Task TryEnqueue_ExecutesItemViaCallback()
    {
        using var cts = new CancellationTokenSource();
        var done = new SemaphoreSlim(0);
        UploadQueueItem? received = null;

        await using var manager = CreateManager((_, item, _) => { received = item; done.Release(); return Task.CompletedTask; }, cts.Token);

        var peer = CreateUnchockedPeer();
        Assert.True(manager.TryEnqueue(peer, new UploadQueueItem(7, 16384, 16384)));

        Assert.True(await done.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(7, received!.Value.PieceIndex);
        Assert.Equal(16384, received.Value.Offset);
    }

    [Fact]
    public async Task TryEnqueue_ReturnsFalse_WhenQueueFull()
    {
        using var cts = new CancellationTokenSource();
        var pumpOnFirst = new SemaphoreSlim(0);
        var gate = new TaskCompletionSource();
        await using var manager = CreateManager(async (_, _, _) => { pumpOnFirst.Release(); await gate.Task; }, cts.Token);

        var peer = CreateUnchockedPeer();

        // Start executing item 0 (pump blocks in gate), leaving the channel free.
        manager.TryEnqueue(peer, new UploadQueueItem(0, 0, 16384));
        Assert.True(await pumpOnFirst.WaitAsync(TimeSpan.FromSeconds(5)));

        // Fill the queue by enqueuing until TryEnqueue returns false (queue full).
        // Count how many items were accepted to confirm the limit is respected.
        int accepted = 0;
        for (int i = 1; i <= 500; i++)
        {
            if (manager.TryEnqueue(peer, new UploadQueueItem(i, 0, 16384)))
            {
                accepted++;
            }
        }

        // Exactly MaxQueueDepthPerPeer (250) items should fit; the rest are dropped.
        Assert.Equal(250, accepted);

        gate.SetResult();
    }

    [Fact]
    public async Task Cancel_SkipsExecuteForPendingItem()
    {
        using var cts = new CancellationTokenSource();
        var pumpOnFirst = new SemaphoreSlim(0);
        var gate = new TaskCompletionSource();
        var executed = new List<int>();

        await using var manager = CreateManager(async (_, item, _) =>
        {
            if (item.PieceIndex == 0) { pumpOnFirst.Release(); await gate.Task; }
            executed.Add(item.PieceIndex);
        }, cts.Token);

        var peer = CreateUnchockedPeer();
        manager.TryEnqueue(peer, new UploadQueueItem(0, 0, 16384)); // blocks pump
        manager.TryEnqueue(peer, new UploadQueueItem(1, 0, 16384)); // will be cancelled

        Assert.True(await pumpOnFirst.WaitAsync(TimeSpan.FromSeconds(5)));
        manager.Cancel(peer, 1, 0); // cancel while pump is blocked on item 0
        gate.SetResult();

        await Task.Delay(200);

        Assert.Equal([0], executed);
    }

    [Fact]
    public async Task Cancel_SameItem_IsDeduplicated()
    {
        using var cts = new CancellationTokenSource();
        var pumpOnFirst = new SemaphoreSlim(0);
        var gate = new TaskCompletionSource();
        var executed = new List<int>();

        await using var manager = CreateManager(async (_, item, _) =>
        {
            if (item.PieceIndex == 0) { pumpOnFirst.Release(); await gate.Task; }
            executed.Add(item.PieceIndex);
        }, cts.Token);

        var peer = CreateUnchockedPeer();
        manager.TryEnqueue(peer, new UploadQueueItem(0, 0, 16384));
        manager.TryEnqueue(peer, new UploadQueueItem(1, 0, 16384));

        Assert.True(await pumpOnFirst.WaitAsync(TimeSpan.FromSeconds(5)));
        // Cancel same block multiple times — must not throw
        manager.Cancel(peer, 1, 0);
        manager.Cancel(peer, 1, 0);
        gate.SetResult();

        await Task.Delay(200);
        Assert.Equal([0], executed);
    }

    [Fact]
    public async Task Cancel_ForUnknownPeer_DoesNotThrow()
    {
        using var cts = new CancellationTokenSource();
        await using var manager = CreateManager((_, _, _) => Task.CompletedTask, cts.Token);

        var peer = CreateUnchockedPeer();
        // No queue created for this peer yet — must not throw
        manager.Cancel(peer, 5, 0);
    }

    [Fact]
    public async Task ProcessesItemsInFifoOrder()
    {
        using var cts = new CancellationTokenSource();
        var order = new List<int>();
        var allDone = new SemaphoreSlim(0);

        await using var manager = CreateManager((_, item, _) =>
        {
            order.Add(item.PieceIndex);
            if (order.Count == 3)
            {
                allDone.Release();
            }

            return Task.CompletedTask;
        }, cts.Token);

        var peer = CreateUnchockedPeer();
        manager.TryEnqueue(peer, new UploadQueueItem(10, 0, 16384));
        manager.TryEnqueue(peer, new UploadQueueItem(20, 0, 16384));
        manager.TryEnqueue(peer, new UploadQueueItem(30, 0, 16384));

        Assert.True(await allDone.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal([10, 20, 30], order);
    }

    [Fact]
    public async Task ChokedPeer_ItemsAreSkippedWithoutCallingExecute()
    {
        using var cts = new CancellationTokenSource();
        var executed = false;
        var done = new SemaphoreSlim(0);

        // Peer is AmChoking=true by default — no Unchoke() call
        var torrent = TorrentTestUtility.CreateMinimal(new TorrentFileMetadata());
        var peer = new PeerCommunication(torrent, new MockPeerListener(), TimeProvider.System);
        Assert.True(peer.AmChoking);

        await using var manager = CreateManager((_, _, _) => { executed = true; return Task.CompletedTask; }, cts.Token);

        // Enqueue directly — bypasses BlockRequestedAsync's own choke check
        // to test that the pump's dequeue-time choke guard works independently
        manager.TryEnqueue(peer, new UploadQueueItem(0, 0, 16384));
        // Give pump time to process (and skip) the item
        await Task.Delay(200);

        Assert.False(executed);
    }

    [Fact]
    public async Task RemovePeer_StopsPumpImmediately()
    {
        using var cts = new CancellationTokenSource();
        var pumpOnFirst = new SemaphoreSlim(0);
        var gate = new TaskCompletionSource();
        var executed = new List<int>();

        await using var manager = CreateManager(async (_, item, _) =>
        {
            pumpOnFirst.Release();
            await gate.Task;
            executed.Add(item.PieceIndex);
        }, cts.Token);

        var peer = CreateUnchockedPeer();
        manager.TryEnqueue(peer, new UploadQueueItem(0, 0, 16384)); // pump blocks here
        manager.TryEnqueue(peer, new UploadQueueItem(1, 0, 16384)); // should never execute

        Assert.True(await pumpOnFirst.WaitAsync(TimeSpan.FromSeconds(5)));
        manager.RemovePeer(peer); // cancels peer's CTS — pump will stop after current item
        gate.SetResult();          // unblock current execute

        await Task.Delay(200);

        // Item 0 was already executing when RemovePeer was called; item 1 was not started
        Assert.Equal([0], executed);
    }

    [Fact]
    public async Task DisposeAsync_CompletesGracefully_WithPendingItems()
    {
        using var cts = new CancellationTokenSource();
        var gate = new TaskCompletionSource();

        var manager = CreateManager(async (_, _, _) => await gate.Task, cts.Token);

        var peer = CreateUnchockedPeer();
        manager.TryEnqueue(peer, new UploadQueueItem(0, 0, 16384));
        await Task.Delay(50); // let pump start

        await cts.CancelAsync();
        gate.SetResult(); // unblock execute so pump can observe cancellation

        await manager.DisposeAsync(); // must not hang
    }

    [Fact]
    public async Task MultiplePeers_HaveIndependentQueues()
    {
        using var cts = new CancellationTokenSource();
        var executedByPeer = new System.Collections.Concurrent.ConcurrentDictionary<PeerCommunication, List<int>>();
        var done = new SemaphoreSlim(0);

        await using var manager = CreateManager((peer, item, _) =>
        {
            executedByPeer.GetOrAdd(peer, _ => []).Add(item.PieceIndex);
            if (executedByPeer.Values.Sum(l => l.Count) == 4)
            {
                done.Release();
            }

            return Task.CompletedTask;
        }, cts.Token);

        var peer1 = CreateUnchockedPeer();
        var peer2 = CreateUnchockedPeer();

        manager.TryEnqueue(peer1, new UploadQueueItem(1, 0, 16384));
        manager.TryEnqueue(peer1, new UploadQueueItem(2, 0, 16384));
        manager.TryEnqueue(peer2, new UploadQueueItem(3, 0, 16384));
        manager.TryEnqueue(peer2, new UploadQueueItem(4, 0, 16384));

        Assert.True(await done.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal([1, 2], executedByPeer[peer1]);
        Assert.Equal([3, 4], executedByPeer[peer2]);
    }

    [Fact]
    public async Task AllowedFastPiece_ExecutesEvenWhenPeerIsChoked()
    {
        using var cts = new CancellationTokenSource();
        var done = new SemaphoreSlim(0);
        UploadQueueItem? received = null;

        await using var manager = CreateManager((_, item, _) => { received = item; done.Release(); return Task.CompletedTask; }, cts.Token);

        var torrent = TorrentTestUtility.CreateMinimal(new TorrentFileMetadata());
        var peer = new PeerCommunication(torrent, new MockPeerListener(), TimeProvider.System);
        Assert.True(peer.AmChoking); // choked — no Unchoke() call

        // Inject piece 5 into the peer's AllowedFast set directly (AddAllowedFastPiece is private)
        var field = typeof(PeerCommunication).GetField("_allowedFastPieces", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((HashSet<int>)field.GetValue(peer)!).Add(5);

        manager.TryEnqueue(peer, new UploadQueueItem(5, 0, 16384));

        Assert.True(await done.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(5, received!.Value.PieceIndex);
    }
}
