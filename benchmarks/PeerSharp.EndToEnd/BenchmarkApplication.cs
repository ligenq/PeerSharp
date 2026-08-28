using System.Runtime.InteropServices;

namespace PeerSharp.EndToEnd;

internal static class BenchmarkApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        BenchmarkOptions? options;
        try
        {
            options = BenchmarkOptions.Parse(args, Console.Error);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        if (options is null) return 2;
        if (options.Help)
        {
            BenchmarkOptions.PrintUsage(Console.Out);
            return 0;
        }

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopping.Cancel();
        };

        try
        {
            var toolchain = new Toolchain(options);
            if (options.Command == "doctor")
            {
                DoctorResult result = toolchain.Inspect();
                result.Print(Console.Out);
                return result.CanBuild ? 0 : 1;
            }

            ToolPaths tools = options.SkipBuild
                ? toolchain.FindBuiltTools()
                : await toolchain.BuildAsync(stopping.Token).ConfigureAwait(false);
            if (options.Command == "build")
            {
                PrintTools(tools);
                return 0;
            }

            var runner = new BenchmarkOrchestrator(options, tools);
            BenchmarkRunSummary summary = await runner.RunAsync(stopping.Token).ConfigureAwait(false);
            Console.WriteLine();
            Console.WriteLine($"Reports: {summary.OutputDirectory}");
            return summary.FailedTrials == 0 ? 0 : 1;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            Console.Error.WriteLine("Benchmark cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void PrintTools(ToolPaths tools)
    {
        Console.WriteLine("Built benchmark tools:");
        Console.WriteLine($"  PeerSharp CLI      {tools.PeerSharpCli}");
        Console.WriteLine($"  libtorrent client  {tools.ClientTest}");
        Console.WriteLine($"  common peer        {tools.ConnectionTester}");
    }
}
