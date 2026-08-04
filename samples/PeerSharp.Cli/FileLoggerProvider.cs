using Microsoft.Extensions.Logging;

namespace PeerSharp.Cli;

/// <summary>
/// Writes the engine's log to a file, so a run can be diagnosed after the fact.
///
/// <para>
/// The console cannot do this job. At the detail worth capturing the engine produces tens of thousands
/// of lines in a few minutes, which is far more than a console buffer holds - a report of an unstable
/// first few minutes arrived with only the last ten seconds still in the window, by which point the
/// problem had resolved itself. What is worth watching live and what is worth keeping are different
/// things, so they go to different places.
/// </para>
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly Lock _gate = new();
    private bool _disposed;

    public FileLoggerProvider(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Not buffered by us. A run that is killed with Ctrl+C or that hangs is exactly the run whose
        // log matters, and a buffer that never flushed would lose the end of it - which is the part
        // being looked for.
        _writer = new StreamWriter(path, append: false) { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer.Flush();
            _writer.Dispose();
        }
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _writer.Write(DateTime.Now.ToString("HH:mm:ss.fff"));
            _writer.Write(' ');
            _writer.Write(Abbreviate(level));
            _writer.Write(": ");
            _writer.Write(ShortCategory(category));
            _writer.Write(" - ");
            _writer.WriteLine(message);

            if (exception is not null)
            {
                _writer.WriteLine(exception.ToString());
            }
        }
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => "none"
    };

    /// <summary>The last segment of the category. The namespace is the same on every line and only
    /// makes each one harder to scan.</summary>
    private static string ShortCategory(string category)
    {
        int lastDot = category.LastIndexOf('.');
        return lastDot >= 0 ? category[(lastDot + 1)..] : category;
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                owner.Write(category, logLevel, formatter(state, exception), exception);
            }
        }
    }
}
