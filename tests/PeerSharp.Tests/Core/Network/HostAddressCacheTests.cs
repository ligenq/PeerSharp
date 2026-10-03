using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals.Network;

namespace PeerSharp.Tests.Core.Network;

public class HostAddressCacheTests
{
    [Fact]
    public async Task AFullCacheOfPendingLookups_DoesNotEvictOrStartMoreWork()
    {
        var answer = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new HostAddressCache(_clock, (_, _) =>
        {
            Interlocked.Increment(ref _lookups);
            return answer.Task;
        });
        var pending = Enumerable.Range(0, HostAddressCache.SweepThreshold)
            .Select(index => cache.ResolveAsync($"tracker{index}.example", TestContext.Current.CancellationToken)).ToArray();
        var duplicate = cache.ResolveAsync("tracker0.example", TestContext.Current.CancellationToken);
        Assert.Empty(await cache.ResolveAsync("overflow.example", TestContext.Current.CancellationToken));
        Assert.Equal(HostAddressCache.SweepThreshold, _lookups);
        answer.SetResult(TrackerAddresses);
        Assert.All(await Task.WhenAll(pending.Append(duplicate)), addresses => Assert.Equal(TrackerAddresses, addresses));
        Assert.Equal(TrackerAddresses, await cache.ResolveAsync("overflow.example", TestContext.Current.CancellationToken));
        Assert.Equal(HostAddressCache.SweepThreshold + 1, _lookups);
    }

    private static readonly IPAddress[] TrackerAddresses = [IPAddress.Parse("198.51.100.7")];

    private readonly FakeTimeProvider _clock = new();
    private int _lookups;

    [Fact]
    public async Task AnAddress_IsItsOwnAnswer_WithoutALookup()
    {
        var cache = new HostAddressCache(_clock, Lookup(TrackerAddresses));

        var addresses = await cache.ResolveAsync("2001:db8::1", TestContext.Current.CancellationToken);

        Assert.Equal([IPAddress.Parse("2001:db8::1")], addresses);
        Assert.Equal(0, _lookups);
    }

    [Fact]
    public async Task AName_IsLookedUpOnce_WhileTheAnswerIsFresh()
    {
        var cache = new HostAddressCache(_clock, Lookup(TrackerAddresses));

        await cache.ResolveAsync("tracker.example", TestContext.Current.CancellationToken);
        _clock.Advance(HostAddressCache.FoundLifetime - TimeSpan.FromSeconds(1));
        var addresses = await cache.ResolveAsync("TRACKER.example", TestContext.Current.CancellationToken);

        Assert.Equal(TrackerAddresses, addresses);
        Assert.Equal(1, _lookups);
    }

    [Fact]
    public async Task AStaleAnswer_IsLookedUpAgain()
    {
        var cache = new HostAddressCache(_clock, Lookup(TrackerAddresses));

        await cache.ResolveAsync("tracker.example", TestContext.Current.CancellationToken);
        _clock.Advance(HostAddressCache.FoundLifetime);
        await cache.ResolveAsync("tracker.example", TestContext.Current.CancellationToken);

        Assert.Equal(2, _lookups);
    }

    [Fact]
    public async Task ANameThatDoesNotResolve_IsNoAddresses_AndIsNotAskedAgainAtOnce()
    {
        // The point of the cache: a dead tracker name costs one exception, not one per announce.
        var cache = new HostAddressCache(_clock, Failing(new SocketException((int)SocketError.HostNotFound)));

        var first = await cache.ResolveAsync("gone.example", TestContext.Current.CancellationToken);
        var second = await cache.ResolveAsync("gone.example", TestContext.Current.CancellationToken);

        Assert.Empty(first);
        Assert.Empty(second);
        Assert.Equal(1, _lookups);
    }

    [Fact]
    public async Task AFailure_IsForgottenSoonerThanAnAnswer()
    {
        // A failure can be the network rather than the name, so it must not outlive the network's return.
        var cache = new HostAddressCache(_clock, Failing(new SocketException((int)SocketError.TryAgain)));

        await cache.ResolveAsync("flaky.example", TestContext.Current.CancellationToken);
        _clock.Advance(HostAddressCache.NotFoundLifetime);
        await cache.ResolveAsync("flaky.example", TestContext.Current.CancellationToken);

        Assert.True(HostAddressCache.NotFoundLifetime < HostAddressCache.FoundLifetime);
        Assert.Equal(2, _lookups);
    }

    [Fact]
    public async Task AMalformedName_IsNoAddresses()
    {
        var cache = new HostAddressCache(_clock, Failing(new ArgumentException("not a host name")));

        Assert.Empty(await cache.ResolveAsync("bad..name", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LookupsAtTheSameTime_AreOneLookup()
    {
        // An announce goes out over IPv4 and IPv6 together, and both want the same name.
        var answer = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new HostAddressCache(_clock, (_, _) =>
        {
            Interlocked.Increment(ref _lookups);
            return answer.Task;
        });

        var first = cache.ResolveAsync("tracker.example", TestContext.Current.CancellationToken);
        var second = cache.ResolveAsync("tracker.example", TestContext.Current.CancellationToken);
        answer.SetResult(TrackerAddresses);

        Assert.Equal(TrackerAddresses, await first);
        Assert.Equal(TrackerAddresses, await second);
        Assert.Equal(1, _lookups);
    }

    [Fact]
    public async Task OneCallerGivingUp_LeavesTheLookupForTheOthers()
    {
        var answer = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new HostAddressCache(_clock, (_, _) => answer.Task);
        using var impatient = new CancellationTokenSource();

        var abandoned = cache.ResolveAsync("tracker.example", impatient.Token);
        var patient = cache.ResolveAsync("tracker.example", TestContext.Current.CancellationToken);
        await impatient.CancelAsync();
        answer.SetResult(TrackerAddresses);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        Assert.Equal(TrackerAddresses, await patient);
    }

    [Theory(Timeout = 30000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentMisses_StartOnlyOneLookup(bool expiredEntry)
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var duplicateLookup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseLookup = new ManualResetEventSlim();
        bool warming = expiredEntry;
        var cache = new HostAddressCache(_clock, (_, _) =>
        {
            if (warming)
            {
                return Task.FromResult(TrackerAddresses);
            }

            if (Interlocked.Increment(ref _lookups) == 1)
            {
                firstEntered.SetResult();
                // Hold the resolver's synchronous portion open. The old implementation had not
                // published its entry yet, so a second caller started another lookup here.
                if (!releaseLookup.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("The test did not release the lookup.");
                }
            }
            else
            {
                duplicateLookup.TrySetResult();
            }

            return Task.FromResult(TrackerAddresses);
        });

        if (expiredEntry)
        {
            await cache.ResolveAsync("tracker.example", TestContext.Current.CancellationToken);
            warming = false;
            _clock.Advance(HostAddressCache.FoundLifetime);
        }

        var first = Task.Factory.StartNew(
            () => cache.ResolveAsync("tracker.example", TestContext.Current.CancellationToken),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        await firstEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = Task.Factory.StartNew(() =>
        {
            secondStarted.SetResult();
            return cache.ResolveAsync("TRACKER.example", TestContext.Current.CancellationToken);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        try
        {
            await secondStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            await Task.WhenAny(duplicateLookup.Task, Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken));
        }
        finally
        {
            releaseLookup.Set();
        }

        Assert.Equal(TrackerAddresses, await first);
        Assert.Equal(TrackerAddresses, await second);
        Assert.Equal(1, _lookups);
    }

    [Fact]
    public async Task ExpiredNames_AreSweptOnceThereAreMany()
    {
        var cache = new HostAddressCache(_clock, Lookup(TrackerAddresses));
        for (int i = 0; i < HostAddressCache.SweepThreshold; i++)
        {
            await cache.ResolveAsync($"tracker{i}.example", TestContext.Current.CancellationToken);
        }

        _clock.Advance(HostAddressCache.FoundLifetime);
        await cache.ResolveAsync("another.example", TestContext.Current.CancellationToken);
        await cache.ResolveAsync("tracker0.example", TestContext.Current.CancellationToken);

        // Swept, so asked again rather than served stale.
        Assert.Equal(HostAddressCache.SweepThreshold + 2, _lookups);
    }

    [Fact]
    public async Task TheSystemResolver_AnswersForLocalhost()
    {
        var cache = new HostAddressCache(_clock);

        var addresses = await cache.ResolveAsync("localhost", TestContext.Current.CancellationToken);

        Assert.Contains(addresses, IPAddress.IsLoopback);
    }

    private Func<string, CancellationToken, Task<IPAddress[]>> Lookup(IPAddress[] addresses) => (_, _) =>
    {
        Interlocked.Increment(ref _lookups);
        return Task.FromResult(addresses);
    };

    private Func<string, CancellationToken, Task<IPAddress[]>> Failing(Exception error) => (_, _) =>
    {
        Interlocked.Increment(ref _lookups);
        return Task.FromException<IPAddress[]>(error);
    };
}
