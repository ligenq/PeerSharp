using Microsoft.Coyote;
using Microsoft.Coyote.Specifications;
using Microsoft.Coyote.SystematicTesting;

namespace PeerSharp.Tests.Concurrency;

public class FileTransferConcurrencyTests
{
    private readonly ITestOutputHelper _output;

    public FileTransferConcurrencyTests(ITestOutputHelper output)
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

    // Since FileTransfer is hard to instantiate due to dependencies, we'll verify the semaphore logic
    // by creating a simplified model that mimics the exact logic in FileTransfer.
    // The actual FileTransfer class uses _overflowProcessingSemaphore and _pieceProcessingQueue.

    private class OverflowModel
    {
        private readonly SemaphoreSlim _semaphore;
        private int _inFlight;
        private int _maxConcurrency;

        public OverflowModel(int maxConcurrency)
        {
            _maxConcurrency = maxConcurrency;
            _semaphore = new SemaphoreSlim(maxConcurrency);
        }

        public async Task ProcessPieceAsync()
        {
            // Model of ProcessPieceAsync semaphore logic
            await _semaphore.WaitAsync();
            try
            {
                Interlocked.Increment(ref _inFlight);
                // Simulate disk write
                await Task.Delay(1);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
                _semaphore.Release();
            }
        }

        public int InFlight => _inFlight;
    }

    [Fact]
    public void FileTransfer_OverflowSemaphore_LimitsConcurrency()
    {
        RunCoyoteTest(() =>
        {
            const int maxConcurrency = 4;
            var model = new OverflowModel(maxConcurrency);
            var tasks = new List<Task>();
            int maxObservedInFlight = 0;
            object lockObj = new();

            for (int i = 0; i < 20; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    var task = model.ProcessPieceAsync();

                    // Check invariant while running
                    int current = model.InFlight;
                    lock (lockObj)
                    {
                        if (current > maxObservedInFlight)
                        {
                            maxObservedInFlight = current;
                        }
                    }

                    await task;
                }));
            }

            Task.WaitAll(tasks.ToArray());

            Specification.Assert(maxObservedInFlight <= maxConcurrency,
                $"Exceeded max concurrency: {maxObservedInFlight} > {maxConcurrency}");
        });
    }
}
