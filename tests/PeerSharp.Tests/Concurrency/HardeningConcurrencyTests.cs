using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Internals.Dht;
using PeerSharp.PiecePicking;
using System.Net;

namespace PeerSharp.Tests.Concurrency;

/// <summary>
/// Systematic concurrency tests for the engine hardening work.
///
/// <para>
/// These target invariants no single-threaded test can establish, because they are properties of how
/// operations interleave rather than of any one of them. Each is scoped to a small in-memory unit,
/// matching the rest of the Coyote suite: the exploration is only worth as much as the fraction of
/// the schedule Coyote actually controls, and real file or socket I/O is concurrency it cannot see.
/// </para>
///
/// <para>
/// <b>Read these for what they currently are.</b> Under an ordinary <c>dotnet test</c> the assembly
/// is not rewritten, so they run as repeated stress rather than systematic exploration. Even
/// rewritten, Coyote 1.7.11 does not recognise <see cref="System.Threading.Lock"/>, so it places no
/// scheduling point inside a critical section guarded by one - which covers most of this engine.
/// <c>LifetimeByteTotals</c> is the exception, and deliberately: it uses a <c>Monitor</c> lock, so
/// with both assemblies rewritten the first test below genuinely fails against a broken
/// implementation - which is how the race it describes was found in the first place. The two
/// rate-limiter tests and the picker test do not have that property yet and should not be read as
/// proof of their invariants. <c>INVESTIGATION_NOTES.md</c> has the measurements behind all of this.
/// </para>
/// </summary>
[Collection("Concurrency")]
public class HardeningConcurrencyTests
{
    private readonly ITestOutputHelper _output;

    public HardeningConcurrencyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private void RunConcurrencyStress(Action scenario, uint iterations = 200)
        => ConcurrencyStress.Run(scenario, iterations, _output);

    /// <summary>
    /// A tiny lock-guarded set standing in for the torrent registry.
    ///
    /// <para>
    /// Deliberately not a <see cref="ConcurrentDictionary{TKey, TValue}"/>. Coyote cannot control the
    /// internals of the concurrent collections, so once the assembly is rewritten their internal
    /// synchronisation reads to the deadlock monitor as a hang and every test using one fails for a
    /// reason that has nothing to do with the code under test. A plain collection behind a
    /// <see cref="Lock"/> is fully controlled, which is what makes the exploration mean anything.
    /// </para>
    /// </summary>
    private sealed class LiveSet
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<int, (long Downloaded, long Uploaded)> _items = [];

        public void Add(int key, long downloaded, long uploaded)
        {
            lock (_lock)
            {
                _items[key] = (downloaded, uploaded);
            }
        }

        public bool Remove(int key)
        {
            lock (_lock)
            {
                return _items.Remove(key);
            }
        }

        public List<(long Downloaded, long Uploaded)> Snapshot()
        {
            lock (_lock)
            {
                return [.. _items.Values];
            }
        }
    }

    #region Lifetime byte totals

    /// <summary>
    /// The counter must not go backwards while a torrent is being removed.
    ///
    /// <para>
    /// This is the interleaving the single-threaded monotonicity test cannot reach. A torrent's bytes
    /// live in two places - the registered set and the retired running total - and a reader arriving
    /// after the removal but before the retirement sees them in neither. The engine did exactly that
    /// until this test was written: it removed from the registry and retired the totals as two
    /// separate steps.
    /// </para>
    /// </summary>
    [Fact]
    public void LifetimeTotals_ReadDuringRemoval_NeverGoBackwards()
    {
        RunConcurrencyStress(() =>
        {
            var live = new LiveSet();
            for (int i = 0; i < 3; i++)
            {
                live.Add(i, 1000, 500);
            }

            var totals = new LifetimeByteTotals(live.Snapshot);
            long lowestObserved = long.MaxValue;

            var removals = new List<Task>();
            for (int i = 0; i < 3; i++)
            {
                int index = i;
                removals.Add(Task.Run(() =>
                    totals.RemoveAndRetire(() => live.Remove(index), 1000, 500)));
            }

            // Every read must see exactly 3000. Retiring a torrent moves its bytes from the live set
            // to the retired total, so the sum of the two is invariant - which is a far sharper test
            // than "never decreases". A monotonicity check only fires when a high read happens to
            // precede a low one, and passes happily on the buggy code whenever it does not; the
            // constant-sum check fails on the very first read that lands inside the window.
            var reader = Task.Run(() =>
            {
                for (int i = 0; i < 8; i++)
                {
                    long downloaded = totals.Read().Downloaded;
                    lowestObserved = Math.Min(lowestObserved, downloaded);
                }
            });

            Task.WaitAll([.. removals, reader]);

            Assert.True(
                lowestObserved == 3000,
                "A lifetime byte total was observed mid-removal: a torrent's bytes were in neither the live set nor the retired figure.");

            var settled = totals.Read();
            Assert.True(
                settled.Downloaded == 3000 && settled.Uploaded == 1500,
                "Expected 3000/1500 once every torrent had been retired.");
        });
    }

    /// <summary>
    /// Two callers racing to remove the same torrent must not both retire its bytes. The removal
    /// delegate's return value is the only thing between that and double counting.
    /// </summary>
    [Fact]
    public void LifetimeTotals_ConcurrentRemovalOfTheSameTorrent_CountsItOnce()
    {
        RunConcurrencyStress(() =>
        {
            var live = new LiveSet();
            live.Add(0, 1000, 500);
            var totals = new LifetimeByteTotals(live.Snapshot);

            int succeeded = 0;
            var racers = new List<Task>();
            for (int i = 0; i < 3; i++)
            {
                racers.Add(Task.Run(() =>
                {
                    if (totals.RemoveAndRetire(() => live.Remove(0), 1000, 500))
                    {
                        Interlocked.Increment(ref succeeded);
                    }
                }));
            }

            Task.WaitAll([.. racers]);

            Assert.True(succeeded == 1, "More than one caller removed the same torrent.");

            var settled = totals.Read();
            Assert.True(
                settled.Downloaded == 1000 && settled.Uploaded == 500,
                "The torrent's bytes were counted more than once.");
        });
    }

    #endregion

    #region DHT query rate limiter

    /// <summary>
    /// The per-source budget has to hold under concurrent queries, because that is the only way it is
    /// ever exercised - a DHT node handles inbound datagrams as they arrive. A limiter that leaks
    /// under contention is a limiter that is absent exactly when it is needed.
    /// </summary>
    [Fact]
    public void RateLimiter_ConcurrentQueriesFromOneSource_NeverExceedTheBudget()
    {
        RunConcurrencyStress(() =>
        {
            const int budget = 5;
            var limiter = new DhtQueryRateLimiter(
                new FakeTimeProvider(),
                NullLoggerFactory.Instance,
                queriesPerAddress: budget,
                window: TimeSpan.FromMinutes(1),
                maxTrackedAddresses: 100,
                fallbackQueriesPerWindow: 100);

            var source = IPAddress.Parse("198.51.100.1");
            int allowed = 0;

            var callers = new List<Task>();
            for (int t = 0; t < 4; t++)
            {
                callers.Add(Task.Run(() =>
                {
                    for (int i = 0; i < 5; i++)
                    {
                        if (limiter.IsQueryAllowed(source))
                        {
                            Interlocked.Increment(ref allowed);
                        }
                    }
                }));
            }

            Task.WaitAll([.. callers]);

            // The clock never moves, so every query falls inside one window and the budget is the ceiling.
            Assert.True(allowed <= budget, "More queries were allowed from one source than its budget.");
        });
    }

    /// <summary>
    /// The shared allowance covering untracked sources is a single counter read and written by every
    /// thread that finds the table full, so it needs the same guarantee as the per-source budget.
    /// </summary>
    [Fact]
    public void RateLimiter_ConcurrentUntrackedQueries_NeverExceedTheSharedAllowance()
    {
        RunConcurrencyStress(() =>
        {
            const int fallback = 4;
            var limiter = new DhtQueryRateLimiter(
                new FakeTimeProvider(),
                NullLoggerFactory.Instance,
                queriesPerAddress: 1,
                window: TimeSpan.FromMinutes(1),
                maxTrackedAddresses: 2,
                fallbackQueriesPerWindow: fallback);

            // Fill the table, so every source below has to draw on the shared allowance.
            limiter.IsQueryAllowed(IPAddress.Parse("198.51.100.1"));
            limiter.IsQueryAllowed(IPAddress.Parse("198.51.100.2"));

            int allowed = 0;
            var callers = new List<Task>();
            for (int t = 0; t < 4; t++)
            {
                int id = t;
                callers.Add(Task.Run(() =>
                {
                    for (int i = 0; i < 3; i++)
                    {
                        var address = new IPAddress(new byte[] { 203, 0, 113, (byte)(id * 10 + i + 1) });
                        if (limiter.IsQueryAllowed(address))
                        {
                            Interlocked.Increment(ref allowed);
                        }
                    }
                }));
            }

            Task.WaitAll([.. callers]);

            Assert.True(allowed <= fallback, "More untracked queries were allowed than the shared allowance.");
        });
    }

    #endregion

    #region Sequential piece picker cursor

    /// <summary>A context whose completed set changes under the picker, as a real one does.</summary>
    private sealed class ConcurrentPickerContext : IPiecePickerContext
    {
        private readonly Lock _lock = new();
        private readonly HashSet<int> _completed = [];

        public required int PieceCount { get; init; }

        public void Complete(int pieceIndex)
        {
            lock (_lock)
            {
                _completed.Add(pieceIndex);
            }
        }

        public int CompletedPieceCount
        {
            get { lock (_lock) { return _completed.Count; } }
        }

        public DownloadStrategy DownloadStrategy => DownloadStrategy.Sequential;

        public bool HasPiece(int pieceIndex)
        {
            lock (_lock) { return _completed.Contains(pieceIndex); }
        }

        public bool IsPieceActive(int pieceIndex) => false;
        public IReadOnlyList<FileSelection>? GetFileSelectionSnapshot() => null;
        public bool IsPieceNeeded(int pieceIndex, IReadOnlyList<FileSelection>? selection) => true;
        public Priority GetPiecePriority(int pieceIndex, IReadOnlyList<FileSelection>? selection) => Priority.Normal;
        IReadOnlyList<int>? IPiecePickerContext.StreamingPriorityPieces => null;
    }

    private sealed class CursorPeer : IPeerPieceInfo
    {
        public required int PieceCount { get; init; }
        public int Count => PieceCount;
        public bool IsChoking => false;
        public bool IsSnubbed => false;
        public bool HasPiece(int pieceIndex) => true;
        public bool IsAllowedFast(int pieceIndex) => true;
        public IEnumerable<int> GetSuggestedPieces() => [];
    }

    /// <summary>
    /// The sequential cursor is shared mutable state that every peer asking for work both reads and
    /// advances, while pieces land underneath it. The risk the optimisation introduces is that the
    /// cursor runs ahead of what is actually finished and a needed piece is never offered again.
    ///
    /// <para>
    /// Note what is <em>not</em> asserted: that a returned piece is still un-held by the time the
    /// caller looks. It cannot be. <c>CanPick</c> re-checks under the picker, but a piece may
    /// complete in the instant between the check and the caller acting on it, so an assertion on the
    /// returned index racing the completer tests the test rather than the picker. What is checkable
    /// is the index being in range, and the ordering contract still holding once everything settles.
    /// </para>
    /// </summary>
    [Fact]
    public void SequentialCursor_UnderConcurrentPicksAndCompletions_StillOffersTheLowestNeededPiece()
    {
        RunConcurrencyStress(() =>
        {
            const int pieceCount = 12;
            var ctx = new ConcurrentPickerContext { PieceCount = pieceCount };
            for (int i = 0; i < 4; i++)
            {
                ctx.Complete(i);
            }

            using var picker = new PiecePicker(ctx, new FakeTimeProvider(), new Random(7));
            var peer = new CursorPeer { PieceCount = pieceCount };
            bool outOfRange = false;

            var pickers = new List<Task>();
            for (int t = 0; t < 3; t++)
            {
                pickers.Add(Task.Run(() =>
                {
                    for (int i = 0; i < 4; i++)
                    {
                        if (picker.PickNextPiece(peer, out int index) && (index < 0 || index >= pieceCount))
                        {
                            outOfRange = true;
                        }
                    }
                }));
            }

            // Pieces landing while the picks run, which is what moves the cursor.
            var completer = Task.Run(() =>
            {
                for (int i = 4; i < 8; i++)
                {
                    ctx.Complete(i);
                }
            });

            Task.WaitAll([.. pickers, completer]);

            Assert.True(!outOfRange, "The sequential picker returned a piece index outside the torrent.");

            // Everything has settled: pieces 0-7 are held, so the next offer must be piece 8. A
            // cursor that had run ahead under contention would skip it and offer 9 or nothing.
            bool picked = picker.PickNextPiece(peer, out int next);
            Assert.True(picked, "The picker offered nothing while pieces 8-11 were still needed.");
            Assert.True(next == 8, "The sequential picker skipped past a piece the torrent still needed.");
        });
    }

    #endregion
}
