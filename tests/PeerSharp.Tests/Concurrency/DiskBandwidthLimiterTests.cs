using Microsoft.Coyote;
using Microsoft.Coyote.SystematicTesting;
using PeerSharp.PieceWriter;
using PeerSharp.Internals.Bandwidth;
using Microsoft.Extensions.Time.Testing;

namespace PeerSharp.Tests.Concurrency;

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

        var engine = TestingEngine.Create(config, test);
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
}
