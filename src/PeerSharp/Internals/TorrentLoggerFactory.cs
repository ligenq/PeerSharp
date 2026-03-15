using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PeerSharp.Internals;

/// <summary>
/// Internal static factory for creating loggers throughout the library.
/// Configured once at startup via ClientEngine creation.
/// </summary>
/// <remarks>
/// This is a pragmatic approach to avoid passing ILoggerFactory through every constructor.
/// If no factory is configured, NullLogger instances are returned (no-op logging).
/// </remarks>
internal static class TorrentLoggerFactory
{
    private static ILoggerFactory _sharedInstance = NullLoggerFactory.Instance;

    /// <summary>
    /// Creates a logger for the specified type.
    /// </summary>
    public static ILogger<T> CreateLogger<T>()
    {
        try
        {
            return _sharedInstance.CreateLogger<T>();
        }
        catch (ObjectDisposedException)
        {
            _sharedInstance = NullLoggerFactory.Instance;
            return _sharedInstance.CreateLogger<T>();
        }
    }

    /// <summary>
    /// Creates a logger with the specified category name.
    /// </summary>
    public static ILogger CreateLogger(string categoryName)
    {
        try
        {
            return _sharedInstance.CreateLogger(categoryName);
        }
        catch (ObjectDisposedException)
        {
            _sharedInstance = NullLoggerFactory.Instance;
            return _sharedInstance.CreateLogger(categoryName);
        }
    }

    /// <summary>
    /// Configures the logger factory. Should only be called once during ClientEngine creation.
    /// </summary>
    internal static void Configure(ILoggerFactory factory)
    {
        _sharedInstance = factory ?? NullLoggerFactory.Instance;
    }

    /// <summary>
    /// Resets to NullLoggerFactory. Used for testing.
    /// </summary>
    internal static void Reset()
    {
        _sharedInstance = NullLoggerFactory.Instance;
    }
}
