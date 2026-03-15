using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PeerSharp.Config;

/// <summary>
/// Configuration options for creating a TorrentClient.
/// </summary>
public sealed class TorrentClientOptions
{
    /// <summary>
    /// Gets or sets the logger factory for creating category-specific loggers.
    /// If null, no logging occurs (default behavior).
    /// </summary>
    /// <remarks>
    /// The library does not include any logging implementation.
    /// To enable logging, provide an ILoggerFactory from Microsoft.Extensions.Logging
    /// or any compatible logging framework (Serilog, NLog, etc.).
    /// </remarks>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>
    /// Optional custom implementation of session persistence.
    /// If provided, this overrides the default file-based persistence even if Session.Enabled is true.
    /// </summary>
    public ISessionPersistence? SessionPersistence { get; init; }

    /// <summary>
    /// Gets or sets the client settings.
    /// If null, default settings are used.
    /// </summary>
    public Settings? Settings { get; init; }

    /// <summary>
    /// Gets the effective logger factory, returning NullLoggerFactory if none was specified.
    /// </summary>
    internal ILoggerFactory EffectiveLoggerFactory => LoggerFactory ?? NullLoggerFactory.Instance;
}

