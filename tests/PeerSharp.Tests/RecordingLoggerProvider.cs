using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace PeerSharp.Tests;

/// <summary>
/// Keeps every log entry verbatim - level, formatted message and exception - so a test can assert
/// how something was reported: at which level, and whether it carried a stack trace.
///
/// <para>
/// Unlike <see cref="Interop.CapturingLoggerProvider"/>, which collapses a soak run's messages into
/// templates and counts them, nothing is aggregated or filtered here; the logger factory's minimum
/// level decides what arrives.
/// </para>
/// </summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(Entries);

    public void Dispose()
    {
    }

    private sealed class RecordingLogger(
        ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => entries.Enqueue((logLevel, formatter(state, exception), exception));
    }
}
