using System.Net;
using System.Net.Sockets;

namespace PeerSharp.Internals.Framework;

/// <summary>
/// Connects a socket and reports the outcome instead of throwing it.
/// </summary>
/// <remarks>
/// <para>
/// Most addresses a swarm hands out are not answering: the peer has gone, the port is closed, or a
/// NAT drops the SYN. That is the ordinary case, not a fault, and <see cref="Socket"/>'s task-based
/// connect has no way to say so except by throwing. At the rate the engine dials strangers that
/// produced roughly nine first-chance exceptions a second during connection churn - two per failed
/// attempt, since one is raised where it is thrown and another as it crosses the await.
/// </para>
/// <para>
/// The cost is not throughput; measured, it is about a thousandth of a percent of a core. It is that
/// every first-chance exception is a round trip to an attached debugger, so a consumer stepping
/// through their own application pays for how often this engine dials a dead peer, and pays it as
/// visible sluggishness.
/// </para>
/// <para>
/// <see cref="SocketAsyncEventArgs"/> reports the same failures through
/// <see cref="SocketAsyncEventArgs.SocketError"/>, which is what libtorrent gets for free from
/// asio's error_code overloads. Genuine misuse - a disposed socket, a bad argument - still throws,
/// because that is a fault rather than an outcome.
/// </para>
/// </remarks>
internal static class SocketConnect
{
    /// <summary>
    /// Connects to <paramref name="remote"/>, returning the socket-level result.
    /// </summary>
    /// <returns>
    /// <see cref="SocketError.Success"/> when connected, <see cref="SocketError.OperationAborted"/>
    /// when <paramref name="cancellationToken"/> fired, otherwise the reason the attempt failed.
    /// </returns>
    public static async ValueTask<SocketError> ConnectAsync(
        Socket socket, IPEndPoint remote, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(remote);

        if (cancellationToken.IsCancellationRequested)
        {
            return SocketError.OperationAborted;
        }

        var args = new ConnectEventArgs { RemoteEndPoint = remote };
        try
        {
            // False means it finished before returning, in which case no completion is raised and
            // the result is already on the args.
            if (!socket.ConnectAsync(args))
            {
                return args.SocketError;
            }

            // Cancelling a pending connect is what closes the window between the timeout firing and
            // the OS giving up on its own, which for an unanswered SYN is tens of seconds.
            using var registration = cancellationToken.Register(
                static state => Socket.CancelConnectAsync((SocketAsyncEventArgs)state!), args);

            return await args.Finished.ConfigureAwait(false);
        }
        finally
        {
            args.Dispose();
        }
    }

    private sealed class ConnectEventArgs : SocketAsyncEventArgs
    {
        private readonly TaskCompletionSource<SocketError> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SocketError> Finished => _completion.Task;

        protected override void OnCompleted(SocketAsyncEventArgs e)
        {
            _completion.TrySetResult(e.SocketError);
        }
    }
}
