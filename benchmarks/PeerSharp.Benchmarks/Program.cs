using BenchmarkDotNet.Running;

namespace PeerSharp.Benchmarks;

public static class Program
{
    /// <summary>
    /// Entry point. Run with no arguments for an interactive picker, <c>--filter *</c> for
    /// everything, or e.g. <c>--filter *Storage*</c> for one suite.
    /// </summary>
    public static void Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
