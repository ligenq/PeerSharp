using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace PeerSharp.Tests.Interop;

/// <summary>
/// Writes every log entry to a file in order, with a millisecond timestamp.
///
/// <para>
/// <see cref="CapturingLoggerProvider"/> collapses messages into templates and counts, which is the
/// right shape for "what happened over a whole soak" but the wrong one for "what happened at the
/// moment it stalled". This keeps the sequence and the clock so a run can be laid alongside another
/// client's log and read together.
/// </para>
/// </summary>
internal sealed class TimestampedFileLoggerProvider : ILoggerProvider
{
    private readonly BlockingCollection<string> _lines = new(new ConcurrentQueue<string>());
    private readonly StreamWriter _writer;
    private readonly Task _pump;

    public TimestampedFileLoggerProvider(string path)
    {
        _writer = new StreamWriter(path, append: false) { AutoFlush = false };
        _pump = Task.Run(() =>
        {
            foreach (var line in _lines.GetConsumingEnumerable())
            {
                _writer.WriteLine(line);
            }
        });
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, Shorten(categoryName));

    public void Dispose()
    {
        _lines.CompleteAdding();

        try
        {
            _pump.Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception)
        {
        }

        _writer.Flush();
        _writer.Dispose();
        _lines.Dispose();
    }

    private void Write(string line)
    {
        if (!_lines.IsAddingCompleted)
        {
            _lines.Add(line);
        }
    }

    private static string Shorten(string category)
    {
        int lastDot = category.LastIndexOf('.');
        return lastDot >= 0 && lastDot < category.Length - 1 ? category[(lastDot + 1)..] : category;
    }

    private sealed class FileLogger : ILogger
    {
        private readonly TimestampedFileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(TimestampedFileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} {logLevel,-11} {_category,-22} {formatter(state, exception)}";
            if (exception is not null)
            {
                line += $" || {exception.GetType().Name}: {exception.Message}";
            }

            _provider.Write(line);
        }
    }
}
