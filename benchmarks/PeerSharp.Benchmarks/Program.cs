using BenchmarkDotNet.Running;

namespace PeerSharp.Benchmarks;

public static class Program
{
    /// <summary>
    /// Entry point. Run with no arguments for an interactive picker, <c>--filter *</c> for
    /// everything, or e.g. <c>--filter *Storage*</c> for one suite.
    /// </summary>
    public static async Task Main(string[] args)
    {
        if (args is ["--loopback", ..])
        {
            await LoopbackTransfer.RunAsync(args[1..]).ConfigureAwait(false);
            return;
        }
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
