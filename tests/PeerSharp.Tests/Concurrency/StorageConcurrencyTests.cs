using Microsoft.Coyote;
using Microsoft.Coyote.SystematicTesting;

namespace PeerSharp.Tests.Concurrency;

public class StorageConcurrencyTests
{
    private readonly ITestOutputHelper _output;

    public StorageConcurrencyTests(ITestOutputHelper output)
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

    // Model of Storage file locking logic
    private class StorageLockModel
    {
        private readonly SemaphoreSlim[] _fileLocks;
        private readonly int _fileCount;

        public StorageLockModel(int fileCount)
        {
            _fileCount = fileCount;
            _fileLocks = new SemaphoreSlim[fileCount];
            for (int i = 0; i < fileCount; i++)
            {
                _fileLocks[i] = new SemaphoreSlim(1, 1);
            }
        }

        public async Task AccessFileAsync(int fileIndex)
        {
            if (fileIndex < 0 || fileIndex >= _fileCount)
            {
                return;
            }

            await _fileLocks[fileIndex].WaitAsync();
            try
            {
                // Simulate I/O
                await Task.Delay(1);
            }
            finally
            {
                _fileLocks[fileIndex].Release();
            }
        }

        // Simulate multi-file operation (e.g. piece spanning two files)
        // Storage typically locks sequentially or independently.
        // If it locks multiple, order matters.
        public async Task AccessRangeAsync(int startFile, int endFile)
        {
            // Simple sequential access model as used in Storage.ReadAsync loop
            for (int i = startFile; i <= endFile; i++)
            {
                await AccessFileAsync(i);
            }
        }
    }

    [Fact]
    public void Storage_ConcurrentFileAccess_NoDeadlocks()
    {
        RunCoyoteTest(() =>
        {
            const int fileCount = 5;
            var model = new StorageLockModel(fileCount);
            var tasks = new List<Task>();

            for (int i = 0; i < 10; i++)
            {
                int start = i % fileCount;
                int end = (i + 1) % fileCount;
                // Ensure order is start -> end
                if (start > end)
                {
                    (start, end) = (end, start);
                }

                tasks.Add(Task.Run(async () => await model.AccessRangeAsync(start, end)));
            }

            Task.WaitAll(tasks.ToArray());
        });
    }
}
