using Microsoft.Coyote;
using Microsoft.Coyote.SystematicTesting;
using PeerSharp.PieceWriter;
using PeerSharp.Internals.Bandwidth;
using Microsoft.Extensions.Time.Testing;

namespace PeerSharp.Tests.Concurrency;

[Collection("Coyote")]
public class DiskBandwidthLimiterTests
{
    private readonly ITestOutputHelper _output;

    public DiskBandwidthLimiterTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private void RunCoyoteTest(Action test, uint iterations = 100)
    {
        var config = Configuration.Create()
            .WithTestingIterations(iterations)
            .WithMaxSchedulingSteps(1000);

        using var engine = TestingEngine.Create(config, test);
        engine.Run();

        var report = engine.TestReport;
        if (report.NumOfFoundBugs > 0)
        {
            _output.WriteLine($"Found {report.NumOfFoundBugs} bug(s)!");
            _output.WriteLine(engine.GetReport());
            Assert.Fail($"Coyote found {report.NumOfFoundBugs} concurrency bug(s). See test output for details.");
        }
    }

    [Fact]
    public void DiskBandwidthLimiter_ConcurrentRequests_Safe()
    {
        RunCoyoteTest(() =>
        {
            var timeProvider = new FakeTimeProvider();
            var bandwidth = new BandwidthManager(10, timeProvider);
            bandwidth.Start();

            // Set some limits
            bandwidth.SetGlobalDiskLimits(1000, 1000);

            var limiter = new DiskBandwidthLimiter(bandwidth, "test_hash");
            var tasks = new List<Task>();

            for (int i = 0; i < 10; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    var readTask = limiter.RequestReadAsync(100, CancellationToken.None);
                    var writeTask = limiter.RequestWriteAsync(100, CancellationToken.None);

                    // We might not get all bandwidth immediately, but it shouldn't deadlock
                    await Task.WhenAny(Task.WhenAll(readTask, writeTask), Task.Delay(10));
                }));
            }

            Task.WaitAll(tasks.ToArray());
            bandwidth.DisposeAsync().AsTask().Wait();
        });
    }

    [Fact]
    public async Task RequestReadAsync_NoGlobalLimit_ReturnsFull()
    {
        var timeProvider = new FakeTimeProvider();
        var bandwidth = new BandwidthManager(10, timeProvider);
        // No disk limits set — unlimited path
        var limiter = new DiskBandwidthLimiter(bandwidth, "DEADBEEF");

        int result = await limiter.RequestReadAsync(1000, CancellationToken.None);

        Assert.Equal(1000, result);
        await bandwidth.DisposeAsync();
    }

    [Fact]
    public async Task RequestWriteAsync_NoGlobalLimit_ReturnsFull()
    {
        var timeProvider = new FakeTimeProvider();
        var bandwidth = new BandwidthManager(10, timeProvider);
        var limiter = new DiskBandwidthLimiter(bandwidth, "DEADBEEF");

        int result = await limiter.RequestWriteAsync(500, CancellationToken.None);

        Assert.Equal(500, result);
        await bandwidth.DisposeAsync();
    }

    [Fact(Timeout = 5000)]
    public async Task RequestReadAsync_WithGlobalLimit_QueuesUntilQuotaReplenished()
    {
        var timeProvider = new FakeTimeProvider();
        var bandwidth = new BandwidthManager(10, timeProvider);
        bandwidth.SetGlobalDiskLimits(1_000_000, 0); // 1 MB/s read limit

        var limiter = new DiskBandwidthLimiter(bandwidth, "DEADBEEF");

        // Quota starts at 0, so the request should block
        var task = limiter.RequestReadAsync(100, CancellationToken.None);
        Assert.False(task.IsCompleted);

        // Simulate 1 s of elapsed time → 1 MB of quota replenished
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        bandwidth.Update(null);

        int result = await task;
        Assert.Equal(100, result);
        await bandwidth.DisposeAsync();
    }

    [Fact(Timeout = 5000)]
    public async Task RequestWriteAsync_WithGlobalLimit_QueuesUntilQuotaReplenished()
    {
        var timeProvider = new FakeTimeProvider();
        var bandwidth = new BandwidthManager(10, timeProvider);
        bandwidth.SetGlobalDiskLimits(0, 1_000_000); // 1 MB/s write limit

        var limiter = new DiskBandwidthLimiter(bandwidth, "DEADBEEF");

        var task = limiter.RequestWriteAsync(100, CancellationToken.None);
        Assert.False(task.IsCompleted);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        bandwidth.Update(null);

        int result = await task;
        Assert.Equal(100, result);
        await bandwidth.DisposeAsync();
    }

    [Fact(Timeout = 5000)]
    public async Task ReturnRead_WithGlobalLimit_DoesNotThrow()
    {
        var timeProvider = new FakeTimeProvider();
        var bandwidth = new BandwidthManager(10, timeProvider);
        bandwidth.SetGlobalDiskLimits(1_000_000, 0);

        var limiter = new DiskBandwidthLimiter(bandwidth, "DEADBEEF");

        // Replenish so the request completes synchronously via fast path after update
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        bandwidth.Update(null);

        int granted = await limiter.RequestReadAsync(100, CancellationToken.None);
        Assert.Equal(100, granted);

        limiter.ReturnRead(granted); // should not throw
        await bandwidth.DisposeAsync();
    }

    [Fact(Timeout = 5000)]
    public async Task ReturnWrite_WithGlobalLimit_DoesNotThrow()
    {
        var timeProvider = new FakeTimeProvider();
        var bandwidth = new BandwidthManager(10, timeProvider);
        bandwidth.SetGlobalDiskLimits(0, 1_000_000);

        var limiter = new DiskBandwidthLimiter(bandwidth, "DEADBEEF");

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        bandwidth.Update(null);

        int granted = await limiter.RequestWriteAsync(100, CancellationToken.None);
        Assert.Equal(100, granted);

        limiter.ReturnWrite(granted); // should not throw
        await bandwidth.DisposeAsync();
    }
}
