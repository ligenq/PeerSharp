using System.Runtime.ExceptionServices;

namespace PeerSharp.Tests.Concurrency;

/// <summary>
/// Runs a concurrent scenario repeatedly, so a race has many chances to show itself.
///
/// <para>
/// This replaces Microsoft Coyote, which the suite used to drive these same bodies. The reason is
/// not that Coyote went quiet - it is that measurement showed it was not doing what its presence
/// implied. Nothing ran <c>coyote rewrite</c>, so the engine controlled one operation rather than
/// the scenario's; and Coyote 1.7.11 does not model <see cref="Lock"/>, which this engine uses
/// almost everywhere, so a critical section guarded by one is atomic as far as the explorer is
/// concerned. A test passed against an implementation with its synchronisation deleted outright.
/// The measurements are in <c>INVESTIGATION_NOTES.md</c>.
/// </para>
///
/// <para>
/// What is left is what was actually happening: repetition on real threads. That is worth keeping -
/// it catches gross races, deadlocks and torn state - but it is stress, not proof, and this type is
/// named so nobody reads more into it. If a systematic explorer that understands
/// <see cref="Lock"/> appears, the bodies are unchanged and only this runner needs replacing.
/// </para>
/// </summary>
internal static class ConcurrencyStress
{
    /// <summary>
    /// Runs <paramref name="scenario"/> <paramref name="iterations"/> times, stopping at the first
    /// failure.
    /// </summary>
    /// <param name="scenario">The concurrent scenario, including its assertions.</param>
    /// <param name="iterations">How many times to repeat it.</param>
    /// <param name="output">Optional sink; the failing iteration is written here before rethrowing.</param>
    public static void Run(Action scenario, uint iterations = 100, ITestOutputHelper? output = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        for (uint i = 0; i < iterations; i++)
        {
            try
            {
                scenario();
            }
            catch (Exception ex)
            {
                // Which iteration failed is the one piece of context repetition adds, and it matters
                // when a failure is intermittent. The exception itself is rethrown unchanged rather
                // than wrapped: a test asserting on an exception type must still see that type, and
                // the original stack is what points at the line that broke.
                output?.WriteLine($"Failed on iteration {i + 1} of {iterations}: {ex.GetType().Name}: {ex.Message}");
                ExceptionDispatchInfo.Capture(ex).Throw();
                throw;
            }
        }
    }
}
