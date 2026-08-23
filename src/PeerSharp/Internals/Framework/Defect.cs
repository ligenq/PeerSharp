using Microsoft.Extensions.Logging;
using System.Reflection;

namespace PeerSharp.Internals.Framework;

/// <summary>
/// Notified when the engine catches an exception that was its own fault.
/// </summary>
/// <remarks>
/// A callback interface rather than an event, per this repository's convention: an event hides who
/// is listening and for how long, and this one is registered by test infrastructure that has to be
/// able to unregister precisely.
/// </remarks>
internal interface IDefectObserver
{
    void DefectCaught(Exception exception, string context);
}

/// <summary>
/// Tells an exception this library caused from one the network handed it, and makes the first kind
/// impossible to lose.
/// </summary>
/// <remarks>
/// <para>
/// A BitTorrent engine fails constantly and by design - peers vanish, sockets reset, torrents are
/// malformed - so its loops are written to survive anything and carry on. Measured, that means 183 of
/// 358 catch sites in this library catch <see cref="Exception"/>, and 176 of those log and continue.
/// A null dereference lands in exactly the same place as a peer hanging up.
/// </para>
/// <para>
/// The cost was measured too, by throwing a <see cref="NullReferenceException"/> from the peer
/// manager's maintenance loop and running the suite: fifty-nine integration tests ran real transfers
/// with it firing repeatedly and every one of them passed. Only the unit test calling the broken
/// method directly noticed. A library that throws to expose its own defects gains nothing while its
/// own catch blocks are the thing that hides them.
/// </para>
/// <para>
/// So the loops keep catching - a defect should not take an engine down mid-transfer - but a defect
/// no longer looks like weather. It is logged as an error with its stack, and handed to any
/// registered <see cref="IDefectObserver"/>, which is how the test suite fails the test that
/// provoked one instead of reading it as ordinary swarm noise.
/// </para>
/// </remarks>
internal static class Defect
{
    private static readonly ObserverList Observers = new();

    /// <summary>
    /// Registers an observer until the returned handle is disposed.
    /// </summary>
    public static IDisposable Observe(IDefectObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return Observers.Add(observer);
    }

    /// <summary>
    /// Whether this exception can only have come from a mistake in this library rather than from the
    /// network, the disk, or a peer.
    /// </summary>
    /// <remarks>
    /// Deliberately a short list. Anything reachable from data a stranger sent belongs on the other
    /// side of the line: <see cref="FormatException"/> and <see cref="InvalidDataException"/> are how
    /// a malformed torrent or a bad packet arrives, <see cref="ObjectDisposedException"/> is ordinary
    /// in a shutdown race between loops, and <see cref="NotSupportedException"/> is how the engine
    /// reports a configuration it will not act on. None of those mean the code is wrong.
    /// </remarks>
    public static bool IsDefect(this Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return Unwrap(exception) is
            NullReferenceException or
            IndexOutOfRangeException or
            InvalidCastException or
            ArgumentException or
            KeyNotFoundException or
            DivideByZeroException or
            NotImplementedException or
            ArrayTypeMismatchException;
    }

    /// <summary>
    /// Records that a caught exception was a defect, if it was. Does nothing otherwise, so a catch
    /// site can call it unconditionally.
    /// </summary>
    /// <param name="exception">The exception the catch block received.</param>
    /// <param name="context">Where it was caught, for the message.</param>
    /// <param name="logger">Logs the defect as an error, with its stack.</param>
    public static void ReportIfDefect(Exception exception, string context, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(logger);

        if (!exception.IsDefect())
        {
            return;
        }

        // Error with the stack, unlike the expected failures around it, which are logged at Debug
        // without one precisely so that a log full of dead peers stays readable.
        logger.LogError(exception, "Defect in {Context}: this is a bug in PeerSharp, not a network failure", context);

        Observers.Notify(exception, context);
    }

    /// <summary>
    /// Looks through the wrappers a defect arrives inside when it crosses a task or a reflection
    /// boundary.
    /// </summary>
    private static Exception Unwrap(Exception exception)
    {
        return exception switch
        {
            AggregateException aggregate when aggregate.InnerExceptions.Count == 1 => Unwrap(aggregate.InnerExceptions[0]),
            TargetInvocationException { InnerException: { } inner } => Unwrap(inner),
            _ => exception
        };
    }

    /// <summary>
    /// The registered observers. A readonly field holding a mutable list, so the registry does not
    /// become the mutable static state this repository bans.
    /// </summary>
    private sealed class ObserverList
    {
        private readonly Lock _lock = new();
        private readonly List<IDefectObserver> _observers = [];

        public IDisposable Add(IDefectObserver observer)
        {
            lock (_lock)
            {
                _observers.Add(observer);
            }

            return new Registration(this, observer);
        }

        public void Notify(Exception exception, string context)
        {
            IDefectObserver[] snapshot;
            lock (_lock)
            {
                if (_observers.Count == 0)
                {
                    return;
                }

                snapshot = [.. _observers];
            }

            foreach (var observer in snapshot)
            {
                observer.DefectCaught(exception, context);
            }
        }

        private void Remove(IDefectObserver observer)
        {
            lock (_lock)
            {
                _observers.Remove(observer);
            }
        }

        private sealed class Registration(ObserverList owner, IDefectObserver observer) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    owner.Remove(observer);
                }
            }
        }
    }
}
