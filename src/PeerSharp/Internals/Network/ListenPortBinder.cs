using Microsoft.Extensions.Logging;
using System.Net.Sockets;

namespace PeerSharp.Internals.Network;

/// <summary>
/// Binds a listen socket to a configured port, and keeps the engine running when that port cannot
/// be had.
/// </summary>
/// <remarks>
/// <para>
/// A configured port is a preference, not a precondition. It can be taken by another process, by a
/// second instance of the caller's own application, or - on Windows - by nobody at all: the OS
/// reserves blocks of the dynamic range (49152-65535) for Hyper-V, WSL and Docker, and a bind
/// inside a reserved block fails with <see cref="SocketError.AccessDenied"/> even though nothing
/// is listening. Those reservations move between reboots, so a port that worked yesterday can be
/// unbindable today.
/// </para>
/// <para>
/// The response is libtorrent's: try the next few ports, then let the OS assign one. An engine on
/// an unexpected port still finds peers, because the bound port is written back to the settings and
/// announced to trackers and the DHT from there. An engine that refuses to start finds nothing.
/// </para>
/// </remarks>
internal static class ListenPortBinder
{
    /// <summary>
    /// How many consecutive ports to try after the configured one. Matches libtorrent's
    /// <c>max_retry_port_bind</c> default.
    /// </summary>
    internal const int MaxRetries = 10;

    /// <summary>
    /// Binds using <paramref name="bind"/>, falling back through the following ports and finally to
    /// an OS-assigned one.
    /// </summary>
    /// <param name="port">The configured port, or 0 to let the OS assign one immediately.</param>
    /// <param name="bind">
    /// Creates and binds the socket for a candidate port. It must not leak a socket when it throws.
    /// </param>
    /// <param name="logger">Receives a warning whenever the configured port is not the one bound.</param>
    /// <param name="what">The listener's name, for the log message.</param>
    public static T Bind<T>(int port, Func<int, T> bind, ILogger logger, string what)
    {
        if (port == 0)
        {
            return bind(0);
        }

        SocketException? failure = null;
        for (int offset = 0; offset <= MaxRetries; offset++)
        {
            int candidate = port + offset;
            if (candidate > ushort.MaxValue)
            {
                break;
            }

            try
            {
                T bound = bind(candidate);
                if (offset > 0)
                {
                    logger.LogWarning(
                        "{What} port {Requested} was unavailable ({Error}); bound to {Actual} instead",
                        what, port, failure?.SocketErrorCode, candidate);
                }

                return bound;
            }
            catch (SocketException ex) when (IsPortUnavailable(ex))
            {
                failure = ex;
            }
        }

        logger.LogWarning(
            "{What} ports {First}-{Last} were all unavailable ({Error}); letting the OS assign one "
                + "instead. Any port forwarding configured for the requested port will not reach "
                + "this session.",
            what, port, Math.Min(port + MaxRetries, ushort.MaxValue), failure?.SocketErrorCode);

        return bind(0);
    }

    /// <summary>
    /// Whether the port itself is the problem, rather than the request.
    /// </summary>
    /// <remarks>
    /// <see cref="SocketError.AccessDenied"/> belongs here: on Windows a bind inside an OS-reserved
    /// range reports it, and it is indistinguishable from a genuine permission problem, where
    /// moving to another port is still the better answer.
    /// </remarks>
    private static bool IsPortUnavailable(SocketException ex)
    {
        return ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied;
    }
}
