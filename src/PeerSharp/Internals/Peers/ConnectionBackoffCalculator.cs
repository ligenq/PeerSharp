namespace PeerSharp.Internals.Peers;

internal static class ConnectionBackoffCalculator
{
    public static TimeSpan Calculate(int fruitlessConnectionCount, int baseSeconds, int maxSeconds, int jitterMs, Func<int, int>? jitterPicker = null)
    {
        int safeBase = Math.Max(1, baseSeconds);
        int safeMax = Math.Max(safeBase, maxSeconds);
        int backoffPow = Math.Min(Math.Max(fruitlessConnectionCount - 1, 0), 6);
        int delaySeconds = Math.Min(safeMax, safeBase * (1 << backoffPow));
        int safeJitterMs = Math.Max(0, jitterMs);
        int jitter = 0;
        if (safeJitterMs > 0)
        {
            jitter = jitterPicker != null
                ? jitterPicker(safeJitterMs + 1)
                : Random.Shared.Next(0, safeJitterMs + 1);
        }

        return TimeSpan.FromSeconds(delaySeconds) + TimeSpan.FromMilliseconds(jitter);
    }
}
