using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;

namespace PeerSharp.Tests.Interop;

/// <summary>
/// Counts every exception thrown in the process, whether or not anything catches it, and attributes it
/// to the code that threw.
///
/// <para>
/// Exceptions used as ordinary control flow are invisible in normal operation - they are caught, the
/// program carries on, and nothing looks wrong. They are not free even so: each one captures a stack,
/// and with a debugger attached each one is a round trip to the debugger, which is enough on its own to
/// turn a working application into an unresponsive one. "Is this too many?" is not answerable by
/// watching a log scroll past, so this turns it into a rate.
/// </para>
/// </summary>
internal sealed class FirstChanceExceptionCounter : IDisposable
{
    private readonly ConcurrentDictionary<string, Counter> _counts = new();
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;

    /// <summary>
    /// Exception instances already counted once, so re-throws can be told apart from new failures.
    ///
    /// <para>
    /// An exception crossing an <c>await</c> is re-thrown by the state machine via
    /// <c>ExceptionDispatchInfo.Throw</c>, and that raises a fresh first-chance notification carrying the
    /// same object. A single failure at the bottom of a deep stream stack therefore costs one debugger
    /// round trip per layer it passes through, which is the difference between a rate that reflects how
    /// often things go wrong and one that reflects how deeply they are wrapped.
    /// </para>
    ///
    /// <para>
    /// Weak so that counting exceptions does not keep them, and everything they captured, alive.
    /// </para>
    /// </summary>
    private readonly ConditionalWeakTable<Exception, object> _alreadySeen = [];

    private int _total;
    private int _distinct;
    private bool _subscribed;

    public FirstChanceExceptionCounter()
    {
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        _subscribed = true;
    }

    public int Total => Volatile.Read(ref _total);

    public TimeSpan Elapsed => DateTimeOffset.UtcNow - _started;

    public double PerSecond => Elapsed.TotalSeconds <= 0 ? 0 : Total / Elapsed.TotalSeconds;

    /// <summary>Failures that actually happened, counting a re-thrown exception once.</summary>
    public int Distinct => Volatile.Read(ref _distinct);

    public double DistinctPerSecond => Elapsed.TotalSeconds <= 0 ? 0 : Distinct / Elapsed.TotalSeconds;

    /// <summary>
    /// Notifications per actual failure. This is the multiplier a consumer running under a debugger
    /// pays, and it is set by how many await boundaries an exception crosses, not by how often things
    /// go wrong.
    /// </summary>
    public double Amplification => Distinct == 0 ? 0 : (double)Total / Distinct;

    private void OnFirstChance(object? sender, FirstChanceExceptionEventArgs e)
    {
        Interlocked.Increment(ref _total);

        bool isRethrow = _alreadySeen.TryGetValue(e.Exception, out _);
        if (!isRethrow)
        {
            _alreadySeen.AddOrUpdate(e.Exception, this);
            Interlocked.Increment(ref _distinct);
        }

        // Attribute to the throwing method rather than the type alone. Ten thousand
        // OperationCanceledExceptions from one loop and ten from ten places are the same line in a log
        // and completely different problems.
        string site = DescribeSite(e.Exception);
        string suffix = isRethrow ? " [rethrow]" : string.Empty;
        _counts.GetOrAdd($"{e.Exception.GetType().Name} at {site}{suffix}", static _ => new Counter()).Increment();
    }

    private static string DescribeSite(Exception exception)
    {
        try
        {
            // Deliberately the live thread stack rather than exception.StackTrace. At first-chance time
            // the exception's own trace holds only the throw site, because a stack trace is built up as
            // the exception propagates - which hides the one thing worth knowing, namely which of our
            // methods asked for the operation that threw. We are running on the throwing thread here, so
            // walking the current stack recovers that.
            var trace = new System.Diagnostics.StackTrace(fNeedFileInfo: false);

            for (int i = 0; i < trace.FrameCount; i++)
            {
                var method = trace.GetFrame(i)?.GetMethod();
                var declaring = method?.DeclaringType;
                var name = declaring?.FullName;

                if (name is null
                    || !name.StartsWith("PeerSharp", StringComparison.Ordinal)
                    || name.StartsWith("PeerSharp.Tests", StringComparison.Ordinal))
                {
                    continue;
                }

                // Async state machines are nested types named after the method; unwrap to read cleanly.
                var owner = declaring!.DeclaringType ?? declaring;
                return $"{owner.Name}.{Clean(declaring.Name, method!.Name)}";
            }

            // Nothing of ours on the physical stack. That is the normal case for a throw completing an
            // async operation - the continuation runs on a pool thread with no trace of who awaited it -
            // so name the thrower and accept that the caller is not recoverable here.
            var site = exception.TargetSite;
            return site is null
                ? "(unattributed)"
                : $"(async) {site.DeclaringType?.Name}.{site.Name}";
        }
        catch
        {
            return "(stack unavailable)";
        }
    }

    /// <summary>
    /// Turns a compiler-generated state machine name such as <c>&lt;SendLoopAsync&gt;d__42</c> back into
    /// the method the author wrote.
    /// </summary>
    private static string Clean(string declaringName, string methodName)
    {
        int start = declaringName.IndexOf('<');
        int end = declaringName.IndexOf('>');
        if (start == 0 && end > 1)
        {
            return declaringName[1..end];
        }

        return methodName;
    }

    public string BuildReport(int limit = 20)
    {
        var report = new StringBuilder();
        report.AppendLine();
        report.AppendLine("=== first chance exceptions ===");
        report.AppendLine($"notifications  : {Total:N0} over {Elapsed.TotalSeconds:F0}s ({PerSecond:F1}/s)");
        report.AppendLine($"distinct       : {Distinct:N0} ({DistinctPerSecond:F1}/s)");
        report.AppendLine($"amplification  : {Amplification:F1}x  (notifications per actual failure)");
        report.AppendLine();

        foreach (var (site, count) in _counts
                     .Select(static pair => (pair.Key, pair.Value.Count))
                     .OrderByDescending(static entry => entry.Count)
                     .Take(limit))
        {
            report.AppendLine($"  {count,8:N0}  {site}");
        }

        return report.ToString();
    }

    public void Dispose()
    {
        if (_subscribed)
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
            _subscribed = false;
        }
    }

    private sealed class Counter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);
    }
}
