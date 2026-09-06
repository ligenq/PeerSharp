using PeerSharp.Internals.Framework;
using System.Reflection;

namespace PeerSharp.Tests.Concurrency;

public class AdjustableConcurrencyLimiterTests
{
    [Fact]
    public async Task IncreaseWakesWaiters_DecreaseDrainsExistingWork()
    {
        using var limiter = new AdjustableConcurrencyLimiter(1);
        await limiter.WaitAsync(CancellationToken.None);
        var second = limiter.WaitAsync(CancellationToken.None);
        Assert.False(second.IsCompleted);
        limiter.SetLimit(2);
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        limiter.SetLimit(1);
        var third = limiter.WaitAsync(CancellationToken.None);
        limiter.Release();
        Assert.False(third.IsCompleted);
        limiter.Release();
        await third.WaitAsync(TimeSpan.FromSeconds(5));
        limiter.Release();
    }

    [Fact]
    public async Task CancellationAndDisposal_DoNotLeakPermitsOrStrandWaiters()
    {
        using var limiter = new AdjustableConcurrencyLimiter(1);
        await limiter.WaitAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var waiting = limiter.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        limiter.Release();
        await limiter.WaitAsync(CancellationToken.None);
        var disposedWaiter = limiter.WaitAsync(CancellationToken.None);
        limiter.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => disposedWaiter);
        limiter.Release(); // An operation already running can finish after disposal.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingTransfer_ReceivesConcurrencySettingChanges(bool hashing)
    {
        await using var torrent = TorrentTestUtility.CreateMinimal();
        var settings = torrent.Settings.Transfer;
        if (hashing) settings.MaxConcurrentPieceHashing = 1;
        else settings.MaxConcurrentPieceWrites = 1;
        var field = torrent.FileTransferInternal.GetType().GetField(hashing ? "_hashSemaphore" : "_writeSemaphore", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var limiter = Assert.IsType<AdjustableConcurrencyLimiter>(field.GetValue(torrent.FileTransferInternal));
        await limiter.WaitAsync(CancellationToken.None);
        var waiting = limiter.WaitAsync(CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        if (hashing) settings.MaxConcurrentPieceHashing = 2;
        else settings.MaxConcurrentPieceWrites = 2;
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        limiter.Release();
        limiter.Release();
    }
}
