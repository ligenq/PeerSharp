using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace PeerSharp.Tests.Interop;

/// <summary>
/// Collects engine log messages during a soak run, so a run that measures an odd result can also say
/// what the engine was doing at the time.
///
/// <para>
/// The observer can only see the peer list, which shows symptoms rather than causes: it can report that
/// a peer never became interested, but not that we tore the connection down for a protocol violation
/// three seconds earlier. Messages are aggregated by template rather than kept verbatim, because a
/// swarm produces thousands and the useful signal is which ones recur.
/// </para>
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, Counter> _counts = new();
    private readonly LogLevel _minimum;
    private int _total;

    public CapturingLoggerProvider(LogLevel minimum = LogLevel.Warning)
    {
        _minimum = minimum;
    }

    /// <summary>
    /// Every message recorded, before collapsing into templates. This is the number a consumer actually
    /// pays for - it is what gets formatted and written to their log sink.
    /// </summary>
    public int Total => Volatile.Read(ref _total);

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName, _minimum);

    public void Dispose()
    {
    }

    private void Record(string category, LogLevel level, string message, Exception? exception)
    {
        Interlocked.Increment(ref _total);

        // Endpoints and byte counts vary per peer; collapsing them is what turns thousands of lines
        // into a handful of distinct findings.
        string key = $"{level} {ShortCategory(category)}: {Collapse(message)}";
        if (exception is not null)
        {
            key += $" [{exception.GetType().Name}]";
        }

        _counts.GetOrAdd(key, static _ => new Counter()).Increment();
    }

    private static string ShortCategory(string category)
    {
        int lastDot = category.LastIndexOf('.');
        return lastDot >= 0 ? category[(lastDot + 1)..] : category;
    }

    private static string Collapse(string message)
    {
        var collapsed = new System.Text.StringBuilder(message.Length);
        bool inNumber = false;

        foreach (char c in message)
        {
            if (char.IsAsciiDigit(c) || c == ':' && inNumber)
            {
                if (!inNumber)
                {
                    collapsed.Append('#');
                    inNumber = true;
                }

                continue;
            }

            inNumber = false;
            collapsed.Append(c);
        }

        return collapsed.ToString();
    }

    /// <summary>
    /// How many messages were recorded at one level. Lets a run capture everything while still
    /// measuring what a consumer sees at the level they actually enable.
    /// </summary>
    public int CountAtLevel(LogLevel level)
    {
        string prefix = level + " ";
        return _counts
            .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Sum(static pair => pair.Value.Count);
    }

    /// <summary>Total recorded for messages whose collapsed text contains <paramref name="fragment"/>.</summary>
    public int CountMatching(string fragment)
    {
        return _counts
            .Where(pair => pair.Key.Contains(fragment, StringComparison.Ordinal))
            .Sum(static pair => pair.Value.Count);
    }

    /// <summary>The distinct messages seen, most frequent first.</summary>
    public IReadOnlyList<(string Message, int Count)> Summarise(int limit = 25)
    {
        return
        [
            .. _counts
                .Select(static pair => (Message: pair.Key, pair.Value.Count))
                .OrderByDescending(static entry => entry.Count)
                .Take(limit)
        ];
    }

    /// <summary>
    /// Warnings and errors, reported separately from the frequency ranking. A protocol failure that
    /// happens twice matters more than a debug line that happens two thousand times, and would never
    /// survive a top-N-by-count list.
    /// </summary>
    public IReadOnlyList<(string Message, int Count)> SummariseProblems(int limit = 30)
    {
        return
        [
            .. _counts
                .Where(static pair =>
                    pair.Key.StartsWith("Warning ", StringComparison.Ordinal) ||
                    pair.Key.StartsWith("Error ", StringComparison.Ordinal) ||
                    pair.Key.StartsWith("Critical ", StringComparison.Ordinal))
                .Select(static pair => (Message: pair.Key, pair.Value.Count))
                .OrderByDescending(static entry => entry.Count)
                .Take(limit)
        ];
    }

    private sealed class Counter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner, string category, LogLevel minimum) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                owner.Record(category, logLevel, formatter(state, exception), exception);
            }
        }
    }
}
