using System.Globalization;
using Xunit.Sdk;

namespace PeerSharp.Tests.Core;

internal sealed class ReplayableScenario(int seed)
{
    private readonly List<string> _events = [];

    public Random Random { get; } = new(seed);

    public void Record(string description) => _events.Add(description);

    public static IEnumerable<TheoryDataRow<int>> Seeds()
    {
        for (int seed = 0; seed < 64; seed++)
        {
            yield return new TheoryDataRow<int>(seed)
                .WithExplicit(seed >= 4)
                .WithTrait("FaultSeed", seed.ToString(CultureInfo.InvariantCulture));
        }
    }

    public static async Task RunAsync(int seed, Func<int, ReplayableScenario, CancellationToken, Task> run, CancellationToken ct)
    {
        var scenario = new ReplayableScenario(seed);
        try
        {
            await run(seed, scenario, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            throw new XunitException(
                $"Replay with --explicit on --filter-trait FaultSeed={seed.ToString(CultureInfo.InvariantCulture)}. Events:{Environment.NewLine}" +
                string.Join(Environment.NewLine, scenario._events), ex);
        }
    }
}
