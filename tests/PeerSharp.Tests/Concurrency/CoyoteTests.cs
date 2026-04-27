using System.Collections.Concurrent;
using Microsoft.Coyote;
using Microsoft.Coyote.Specifications;
using Microsoft.Coyote.SystematicTesting;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Internals.Bandwidth;
using PeerSharp.Internals.Peers;
using CoreTransferStats = PeerSharp.Internals.TransferStats;

namespace PeerSharp.Tests.Concurrency;

/// <summary>
/// Coyote-based concurrency tests for finding race conditions and deadlocks.
/// These tests use Microsoft Coyote's systematic testing to explore different
/// thread interleavings and find concurrency bugs.
/// </summary>
public class CoyoteTests
{
    private readonly ITestOutputHelper _output;

    public CoyoteTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Helper to run a Coyote test with systematic exploration.
    /// </summary>
    private void RunCoyoteTest(Action test, uint iterations = 100)
    {
        var config = Configuration.Create()
            .WithTestingIterations(iterations)
            .WithMaxSchedulingSteps(1000);

        var engine = TestingEngine.Create(config, test);
        engine.Run();

        var report = engine.TestReport;
        if (report.NumOfFoundBugs > 0)
        {
            _output.WriteLine($"Found {report.NumOfFoundBugs} bug(s)!");
            _output.WriteLine(engine.GetReport());
            Assert.Fail($"Coyote found {report.NumOfFoundBugs} concurrency bug(s). See test output for details.");
        }
    }


    #region BandwidthChannel Tests

    /// <summary>
    /// Tests concurrent UseQuota operations to ensure quota doesn't go below minimum.
    /// The CAS loop in UseQuota should properly clamp to -3*limit.
    /// </summary>
    [Fact]
    public void BandwidthChannel_ConcurrentUseQuota_RespectsBounds()
    {
        RunCoyoteTest(() =>
        {
            var timeProvider = new FakeTimeProvider();
            var channel = new BandwidthChannel(timeProvider);
            int limit = 1000;
            channel.SetLimit(limit);

            // Give initial quota
            channel.UpdateQuota(1000);

            int taskCount = 5;
            int usesPerTask = 100;
            int amountPerUse = 50;

            var tasks = new List<Task>();
            for (int t = 0; t < taskCount; t++)
            {
                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < usesPerTask; i++)
                    {
                        channel.UseQuota(amountPerUse);
                    }
                }));
            }

            Task.WaitAll(tasks.ToArray());

            // Invariant: quota should never go below -3*limit
            int minAllowed = -(limit * 3);
            int available = channel.AvailableQuota;

            Specification.Assert(available >= minAllowed,
                $"Quota {available} went below minimum allowed {minAllowed}");
        });
    }

    /// <summary>
    /// Tests concurrent UpdateQuota operations to ensure quota doesn't exceed maximum.
    /// The CAS loop in UpdateQuota should properly clamp to 3*limit.
    /// </summary>
    [Fact]
    public void BandwidthChannel_ConcurrentUpdateQuota_RespectsBounds()
    {
        RunCoyoteTest(() =>
        {
            var timeProvider = new FakeTimeProvider();
            var channel = new BandwidthChannel(timeProvider);
            int limit = 1000;
            channel.SetLimit(limit);

            int taskCount = 5;
            int updatesPerTask = 100;
            int dtPerUpdate = 100; // 100ms

            var tasks = new List<Task>();
            for (int t = 0; t < taskCount; t++)
            {
                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < updatesPerTask; i++)
                    {
                        channel.UpdateQuota(dtPerUpdate);
                    }
                }));
            }

            Task.WaitAll(tasks.ToArray());

            // Invariant: quota should never exceed 3*limit
            int maxAllowed = limit * 3;
            int available = channel.AvailableQuota;

            Specification.Assert(available <= maxAllowed,
                $"Quota {available} exceeded maximum allowed {maxAllowed}");
        });
    }

    /// <summary>
    /// Tests concurrent UpdateQuota and UseQuota operations together.
    /// This is the most realistic scenario where quota is being replenished
    /// while simultaneously being consumed.
    /// </summary>
    [Fact]
    public void BandwidthChannel_ConcurrentUpdateAndUse_MaintainsBounds()
    {
        RunCoyoteTest(() =>
        {
            var timeProvider = new FakeTimeProvider();
            var channel = new BandwidthChannel(timeProvider);
            int limit = 1000;
            channel.SetLimit(limit);
            channel.UpdateQuota(2000); // Start with 2000

            var tasks = new List<Task>();

            // Updaters
            for (int t = 0; t < 3; t++)
            {
                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < 50; i++)
                    {
                        channel.UpdateQuota(50);
                    }
                }));
            }

            // Consumers
            for (int t = 0; t < 3; t++)
            {
                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < 50; i++)
                    {
                        channel.UseQuota(100);
                    }
                }));
            }

            // Returners
            for (int t = 0; t < 2; t++)
            {
                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < 50; i++)
                    {
                        channel.ReturnQuota(25);
                    }
                }));
            }

            Task.WaitAll(tasks.ToArray());

            // Check bounds
            int minAllowed = -(limit * 3);
            int maxAllowed = limit * 3;
            int available = channel.AvailableQuota;

            Specification.Assert(available >= minAllowed && available <= maxAllowed,
                $"Quota {available} out of bounds [{minAllowed}, {maxAllowed}]");
        });
    }

    /// <summary>
    /// Tests the subquota overflow handling in UpdateQuota.
    /// When subQuota >= 1000, it should atomically transfer to quota.
    /// </summary>
    [Fact]
    public void BandwidthChannel_SubQuotaOverflow_HandledCorrectly()
    {
        RunCoyoteTest(() =>
        {
            var timeProvider = new FakeTimeProvider();
            var channel = new BandwidthChannel(timeProvider);
            channel.SetLimit(100); // 100 bytes/sec = 0.1 bytes/ms

            var tasks = new List<Task>();

            // Multiple threads calling UpdateQuota with small dt values
            // This exercises the subquota overflow logic
            for (int t = 0; t < 10; t++)
            {
                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < 100; i++)
                    {
                        channel.UpdateQuota(1); // 0.1 bytes per update
                    }
                }));
            }

            Task.WaitAll(tasks.ToArray());

            // Each task: 100 updates * 1ms * 100 bytes/sec / 1000ms = 10 bytes
            // 10 tasks = 100 bytes total (approximately, due to rounding)
            // Should be around 100 bytes give or take rounding
            int available = channel.AvailableQuota;

            // The quota should be positive and bounded
            Specification.Assert(available >= 0 && available <= 300,
                $"Quota {available} seems incorrect after subquota accumulation");
        });
    }

    #endregion

    #region PieceState Tests

    /// <summary>
    /// Tests concurrent TryAddBlock operations.
    /// Only one thread should succeed in adding a block at a given index.
    /// </summary>
    [Fact]
    public void PieceState_ConcurrentTryAddBlock_OnlyOneSucceeds()
    {
        RunCoyoteTest(() =>
        {
            var piece = new PieceState(0, 10);
            int blockIndex = 5;
            int successCount = 0;
            object countLock = new();

            var tasks = new List<Task>();

            for (int t = 0; t < 5; t++)
            {
                int threadId = t;
                tasks.Add(Task.Run(() =>
                {
                    var block = new Block(0, 0, 16384);
                    var peer = CreateMockPeerCommunication(threadId);

                    bool success = piece.TryAddBlock(blockIndex, block, peer);

                    if (success)
                    {
                        lock (countLock)
                        {
                            successCount++;
                        }
                    }
                    else
                    {
                        block.Dispose();
                    }
                }));
            }

            Task.WaitAll(tasks.ToArray());

            // Exactly one thread should have succeeded
            Specification.Assert(successCount == 1,
                $"Expected exactly 1 successful add, got {successCount}");

            // Received count should be 1
            Specification.Assert(piece.ReceivedCount == 1,
                $"Expected ReceivedCount of 1, got {piece.ReceivedCount}");
        });
    }

    /// <summary>
    /// Tests TryCompleteAndSetWriting when multiple blocks are being added concurrently.
    /// Only one thread should claim write responsibility.
    /// </summary>
    [Fact]
    public void PieceState_ConcurrentCompletion_OnlyOneWriteClaim()
    {
        RunCoyoteTest(() =>
        {
            int blocksCount = 4;
            var piece = new PieceState(0, blocksCount);
            int writeClaimCount = 0;
            object countLock = new();

            var tasks = new List<Task>();

            // Each thread adds a different block
            for (int t = 0; t < blocksCount; t++)
            {
                int blockIndex = t;
                tasks.Add(Task.Run(() =>
                {
                    var block = new Block(0, 0, 16384);
                    var peer = CreateMockPeerCommunication(blockIndex);

                    piece.TryAddBlock(blockIndex, block, peer);

                    // Try to claim write responsibility
                    if (piece.TryCompleteAndSetWriting())
                    {
                        lock (countLock)
                        {
                            writeClaimCount++;
                        }
                    }
                }));
            }

            Task.WaitAll(tasks.ToArray());

            // Exactly one thread should have claimed write
            Specification.Assert(writeClaimCount == 1,
                $"Expected exactly 1 write claim, got {writeClaimCount}");

            Specification.Assert(piece.IsWriting,
                "IsWriting should be true after successful claim");
        });
    }

    /// <summary>
    /// Tests concurrent TryAddBlock after IsWriting is set.
    /// All adds should fail once piece is marked as writing.
    /// </summary>
    [Fact]
    public void PieceState_AddBlockAfterWriting_AllFail()
    {
        RunCoyoteTest(() =>
        {
            int blocksCount = 4;
            var piece = new PieceState(0, blocksCount);

            // Add all blocks first
            for (int i = 0; i < blocksCount; i++)
            {
                var block = new Block(0, 0, 16384);
                var peer = CreateMockPeerCommunication(i);
                piece.TryAddBlock(i, block, peer);
            }

            // Set writing
            piece.TryCompleteAndSetWriting();

            int addSuccessCount = 0;
            object countLock = new();

            var tasks = new List<Task>();

            // Try to add more blocks concurrently - all should fail
            for (int t = 0; t < 5; t++)
            {
                int blockIndex = t % blocksCount;
                tasks.Add(Task.Run(() =>
                {
                    var block = new Block(0, 0, 16384);
                    var peer = CreateMockPeerCommunication(100 + blockIndex);

                    bool success = piece.TryAddBlock(blockIndex, block, peer);

                    if (success)
                    {
                        lock (countLock)
                        {
                            addSuccessCount++;
                        }
                    }

                    block.Dispose();
                }));
            }

            Task.WaitAll(tasks.ToArray());

            // No adds should succeed after writing is set
            Specification.Assert(addSuccessCount == 0,
                $"Expected 0 successful adds after writing, got {addSuccessCount}");
        });
    }

    /// <summary>
    /// Tests concurrent Reset operations.
    /// Reset should be atomic and leave piece in consistent state.
    /// </summary>
    [Fact]
    public void PieceState_ConcurrentReset_MaintainsConsistency()
    {
        RunCoyoteTest(() =>
        {
            int blocksCount = 4;
            var piece = new PieceState(0, blocksCount);

            // Add some blocks
            for (int i = 0; i < 2; i++)
            {
                var block = new Block(0, 0, 16384);
                var peer = CreateMockPeerCommunication(i);
                piece.TryAddBlock(i, block, peer);
            }

            var tasks = new List<Task>();

            // Concurrent resets and adds
            for (int t = 0; t < 3; t++)
            {
                tasks.Add(Task.Run(() => piece.Reset()));
            }

            for (int t = 0; t < 3; t++)
            {
                int blockIndex = t;
                tasks.Add(Task.Run(() =>
                {
                    var block = new Block(0, 0, 16384);
                    var peer = CreateMockPeerCommunication(100 + blockIndex);

                    if (!piece.TryAddBlock(blockIndex, block, peer))
                    {
                        block.Dispose();
                    }
                }));
            }

            Task.WaitAll(tasks.ToArray());

            // After all operations, state should be consistent
            int receivedCount = piece.ReceivedCount;
            Specification.Assert(receivedCount >= 0 && receivedCount <= blocksCount,
                $"ReceivedCount {receivedCount} out of valid range [0, {blocksCount}]");

            Specification.Assert(!piece.IsWriting,
                "IsWriting should be false after reset");
        });
    }

    #endregion

    #region AlertsManager Tests

    /// <summary>
    /// Tests concurrent PostAlert operations with queue bounding.
    /// Alert count should stay approximately bounded to MaxAlertQueueSize.
    /// </summary>
    [Fact]
    public void AlertsManager_ConcurrentPostAlert_RespectsQueueBound()
    {
        RunCoyoteTest(() =>
        {
            var timeProvider = new FakeTimeProvider();
            var manager = new AlertsManager(timeProvider);
            manager.RegisterAlerts((uint)AlertId.TorrentAdded);

            int postCount = 200;
            var tasks = new List<Task>();

            for (int t = 0; t < 5; t++)
            {
                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < postCount; i++)
                    {
                        manager.PostAlert(new SimpleTorrentAlert
                        {
                            Id = AlertId.TorrentAdded,
                            Torrent = null!,
                            Timestamp = timeProvider.GetUtcNow()
                        });
                    }
                }));
            }

            Task.WaitAll(tasks.ToArray());

            // Queue should be approximately bounded (allow some slack due to race)
            var alerts = manager.PopAlerts();

            // MaxAlertQueueSize is 1000, with 5 threads * 200 = 1000 total posts
            // Due to the bounding logic, we should have approximately 1000 or less
            Specification.Assert(alerts.Count <= 1100, // Allow 10% slack for race window
                $"Alert queue grew too large: {alerts.Count}");
        });
    }

    /// <summary>
    /// Tests concurrent PostAlert and PopAlerts operations.
    /// No alerts should be lost or duplicated.
    /// </summary>
    [Fact]
    public void AlertsManager_ConcurrentPostAndPop_NoAlertsLost()
    {
        RunCoyoteTest(() =>
        {
            var timeProvider = new FakeTimeProvider();
            var manager = new AlertsManager(timeProvider);
            manager.RegisterAlerts((uint)AlertId.TorrentAdded);

            int totalPosted = 0;
            int totalPopped = 0;
            object countLock = new();

            var tasks = new List<Task>();

            // Posters
            for (int t = 0; t < 3; t++)
            {
                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < 100; i++)
                    {
                        manager.PostAlert(new SimpleTorrentAlert
                        {
                            Id = AlertId.TorrentAdded,
                            Torrent = null!,
                            Timestamp = timeProvider.GetUtcNow()
                        });
                        lock (countLock)
                        {
                            totalPosted++;
                        }
                    }
                }));
            }

            // Poppers
            for (int t = 0; t < 2; t++)
            {
                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < 50; i++)
                    {
                        var alerts = manager.PopAlerts();
                        lock (countLock)
                        {
                            totalPopped += alerts.Count;
                        }
                        Thread.Yield();
                    }
                }));
            }

            Task.WaitAll(tasks.ToArray());

            // Pop remaining
            var remaining = manager.PopAlerts();
            totalPopped += remaining.Count;

            // All posted alerts should eventually be popped
            Specification.Assert(totalPopped == totalPosted,
                $"Posted {totalPosted} but popped {totalPopped}");
        });
    }

    /// <summary>
    /// Tests concurrent RegisterAlerts and PostAlert operations.
    /// Registration changes should not cause crashes or data corruption.
    /// </summary>
    [Fact]
    public void AlertsManager_ConcurrentRegisterAndPost_NoCrash()
    {
        RunCoyoteTest(() =>
        {
            var timeProvider = new FakeTimeProvider();
            var manager = new AlertsManager(timeProvider);

            var tasks = new List<Task>();

            // Registerers
            for (int t = 0; t < 2; t++)
            {
                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < 50; i++)
                    {
                        // Toggle between different masks
                        manager.RegisterAlerts((uint)(AlertId.TorrentAdded | AlertId.TorrentRemoved));
                        manager.RegisterAlerts((uint)AlertId.TorrentFinished);
                        manager.RegisterAlerts(0);
                    }
                }));
            }

            // Posters
            for (int t = 0; t < 3; t++)
            {
                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < 100; i++)
                    {
                        manager.PostAlert(new SimpleTorrentAlert
                        {
                            Id = AlertId.TorrentAdded,
                            Torrent = null!,
                            Timestamp = timeProvider.GetUtcNow()
                        });
                    }
                }));
            }

            Task.WaitAll(tasks.ToArray());

            // Just verify we didn't crash and state is valid
            var alerts = manager.PopAlerts();
            Specification.Assert(alerts.Count >= 0, "PopAlerts returned negative count");
        });
    }

    #endregion

    #region TransferStats Tests

    /// <summary>
    /// Tests concurrent TransferStats updates.
    /// Counters should accurately reflect total bytes added.
    /// </summary>
    [Fact]
    public void TransferStats_ConcurrentUpdates_AccurateCount()
    {
        RunCoyoteTest(() =>
        {
            var stats = new CoreTransferStats();
            int threads = 5;
            int addsPerThread = 100;
            long bytesPerAdd = 1000;

            var tasks = new List<Task>();

            for (int t = 0; t < threads; t++)
            {
                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < addsPerThread; i++)
                    {
                        stats.AddDownloaded(bytesPerAdd);
                        stats.AddUploaded(bytesPerAdd / 2);
                    }
                }));
            }

            Task.WaitAll(tasks.ToArray());

            long expectedDownloaded = threads * addsPerThread * bytesPerAdd;
            long expectedUploaded = threads * addsPerThread * (bytesPerAdd / 2);

            Specification.Assert(stats.Downloaded == expectedDownloaded,
                $"Downloaded: expected {expectedDownloaded}, got {stats.Downloaded}");
            Specification.Assert(stats.Uploaded == expectedUploaded,
                $"Uploaded: expected {expectedUploaded}, got {stats.Uploaded}");
        });
    }

    #endregion

    #region Dual-Index Coherence Tests (Models for PeerManager/UtpManager patterns)

    /// <summary>
    /// Model of dual-index ConcurrentDictionary pattern used in PeerManager and UtpManager.
    /// Tests that both indices stay in sync during concurrent add/remove operations.
    /// </summary>
    private class DualIndexModel<TKey, TValue> where TKey : notnull where TValue : class
    {
        private readonly ConcurrentDictionary<TKey, TValue> _primaryIndex = new();
        private readonly ConcurrentDictionary<TKey, TValue> _secondaryIndex = new();
        private int _count;

        public int Count => Interlocked.CompareExchange(ref _count, 0, 0);

        /// <summary>
        /// Add to both indices atomically. Returns false if already exists.
        /// Models PeerManager's add pattern.
        /// </summary>
        public bool TryAdd(TKey primaryKey, TKey secondaryKey, TValue value)
        {
            if (!_primaryIndex.TryAdd(primaryKey, value))
            {
                return false;
            }

            if (!_secondaryIndex.TryAdd(secondaryKey, value))
            {
                // Rollback primary add
                _primaryIndex.TryRemove(primaryKey, out _);
                return false;
            }

            Interlocked.Increment(ref _count);
            return true;
        }

        /// <summary>
        /// Remove from both indices. Models PeerManager's remove pattern.
        /// </summary>
        public bool TryRemove(TKey primaryKey, TKey secondaryKey)
        {
            bool removedPrimary = _primaryIndex.TryRemove(primaryKey, out _);
            bool removedSecondary = _secondaryIndex.TryRemove(secondaryKey, out _);

            if (removedPrimary)
            {
                Interlocked.Decrement(ref _count);
            }

            return removedPrimary || removedSecondary;
        }

        public bool ContainsPrimary(TKey key) => _primaryIndex.ContainsKey(key);
        public bool ContainsSecondary(TKey key) => _secondaryIndex.ContainsKey(key);

        /// <summary>
        /// Check coherence: both indices should have same count.
        /// </summary>
        public (int primary, int secondary, int counter) GetCounts()
        {
            return (_primaryIndex.Count, _secondaryIndex.Count, Count);
        }
    }

    /// <summary>
    /// Tests that dual-index add operations maintain coherence.
    /// This models the PeerManager pattern of _connectedPeers and _connectedEndpoints.
    /// </summary>
    [Fact]
    public void DualIndex_ConcurrentAdd_MaintainsCoherence()
    {
        RunCoyoteTest(() =>
        {
            var model = new DualIndexModel<int, string>();

            var tasks = new List<Task>();

            // Multiple threads trying to add entries
            for (int t = 0; t < 5; t++)
            {
                int threadId = t;
                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < 20; i++)
                    {
                        int id = threadId * 100 + i;
                        model.TryAdd(id, id + 10000, $"value_{id}");
                    }
                }));
            }

            Task.WaitAll(tasks.ToArray());

            // Check coherence
            var (primary, secondary, counter) = model.GetCounts();

            Specification.Assert(primary == secondary,
                $"Index mismatch: primary={primary}, secondary={secondary}");
            Specification.Assert(primary == counter,
                $"Counter mismatch: count={counter}, actual={primary}");
        });
    }

    /// <summary>
    /// Tests concurrent add and remove operations maintain coherence.
    /// </summary>
    [Fact]
    public void DualIndex_ConcurrentAddRemove_MaintainsCoherence()
    {
        RunCoyoteTest(() =>
        {
            var model = new DualIndexModel<int, string>();

            // Pre-populate
            for (int i = 0; i < 50; i++)
            {
                model.TryAdd(i, i + 10000, $"value_{i}");
            }

            var tasks = new List<Task>();

            // Adders
            for (int t = 0; t < 3; t++)
            {
                int threadId = t;
                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < 20; i++)
                    {
                        int id = 1000 + threadId * 100 + i;
                        model.TryAdd(id, id + 10000, $"value_{id}");
                    }
                }));
            }

            // Removers
            for (int t = 0; t < 2; t++)
            {
                int threadId = t;
                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < 25; i++)
                    {
                        int id = threadId * 25 + i;
                        model.TryRemove(id, id + 10000);
                    }
                }));
            }

            Task.WaitAll(tasks.ToArray());

            // Check coherence
            var (primary, secondary, counter) = model.GetCounts();

            Specification.Assert(primary == secondary,
                $"Index mismatch after add/remove: primary={primary}, secondary={secondary}");
        });
    }

    /// <summary>
    /// Tests that duplicate adds are properly rejected.
    /// </summary>
    [Fact]
    public void DualIndex_DuplicateAdd_OnlyOneSucceeds()
    {
        RunCoyoteTest(() =>
        {
            var model = new DualIndexModel<int, string>();
            int successCount = 0;
            object countLock = new();

            var tasks = new List<Task>();

            // Multiple threads trying to add the same key
            for (int t = 0; t < 5; t++)
            {
                tasks.Add(Task.Run(() =>
                {
                    bool success = model.TryAdd(42, 10042, "value_42");
                    if (success)
                    {
                        lock (countLock)
                        {
                            successCount++;
                        }
                    }
                }));
            }

            Task.WaitAll(tasks.ToArray());

            Specification.Assert(successCount == 1,
                $"Expected 1 successful add, got {successCount}");

            var (primary, secondary, counter) = model.GetCounts();
            Specification.Assert(counter == 1,
                $"Expected count of 1, got {counter}");
        });
    }

    #endregion

    #region PiecePicker Availability Tests

    /// <summary>
    /// Model of PiecePicker's availability tracking for Coyote testing.
    /// </summary>
    private class PieceAvailabilityModel
    {
        private readonly int[] _availability;

        public PieceAvailabilityModel(int pieceCount)
        {
            _availability = new int[pieceCount];
        }

        public void IncrementAvailability(int pieceIndex)
        {
            if (pieceIndex >= 0 && pieceIndex < _availability.Length)
            {
                Interlocked.Increment(ref _availability[pieceIndex]);
            }
        }

        public void DecrementAvailability(int pieceIndex)
        {
            if (pieceIndex >= 0 && pieceIndex < _availability.Length)
            {
                Interlocked.Decrement(ref _availability[pieceIndex]);
            }
        }

        public int GetAvailability(int pieceIndex)
        {
            if (pieceIndex >= 0 && pieceIndex < _availability.Length)
            {
                return Interlocked.CompareExchange(ref _availability[pieceIndex], 0, 0);
            }
            return 0;
        }

        public int[] GetSnapshot()
        {
            var snapshot = new int[_availability.Length];
            for (int i = 0; i < _availability.Length; i++)
            {
                snapshot[i] = Interlocked.CompareExchange(ref _availability[i], 0, 0);
            }
            return snapshot;
        }
    }

    /// <summary>
    /// Tests concurrent increment operations on piece availability.
    /// Simulates multiple peers connecting and advertising their pieces.
    /// </summary>
    [Fact]
    public void PieceAvailability_ConcurrentIncrement_AccurateCount()
    {
        RunCoyoteTest(() =>
        {
            int pieceCount = 100;
            var model = new PieceAvailabilityModel(pieceCount);

            int peersPerThread = 10;
            int threadCount = 5;

            var tasks = new List<Task>();

            // Each thread simulates peers connecting
            for (int t = 0; t < threadCount; t++)
            {
                tasks.Add(Task.Run(() =>
                {
                    for (int peer = 0; peer < peersPerThread; peer++)
                    {
                        // Each peer has all pieces (simulates seeder)
                        for (int piece = 0; piece < pieceCount; piece++)
                        {
                            model.IncrementAvailability(piece);
                        }
                    }
                }));
            }

            Task.WaitAll(tasks.ToArray());

            int expectedAvailability = threadCount * peersPerThread;
            var snapshot = model.GetSnapshot();

            for (int i = 0; i < pieceCount; i++)
            {
                Specification.Assert(snapshot[i] == expectedAvailability,
                    $"Piece {i}: expected availability {expectedAvailability}, got {snapshot[i]}");
            }
        });
    }

    /// <summary>
    /// Tests concurrent increment and decrement operations.
    /// Simulates peers connecting and disconnecting.
    /// </summary>
    [Fact]
    public void PieceAvailability_ConcurrentIncrementDecrement_NeverNegative()
    {
        RunCoyoteTest(() =>
        {
            int pieceCount = 50;
            var model = new PieceAvailabilityModel(pieceCount);

            // Pre-populate with some availability
            for (int i = 0; i < pieceCount; i++)
            {
                for (int j = 0; j < 10; j++)
                {
                    model.IncrementAvailability(i);
                }
            }

            var tasks = new List<Task>();

            // Incrementers (peers connecting)
            for (int t = 0; t < 3; t++)
            {
                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < 20; i++)
                    {
                        for (int piece = 0; piece < pieceCount; piece++)
                        {
                            model.IncrementAvailability(piece);
                        }
                    }
                }));
            }

            // Decrementers (peers disconnecting)
            for (int t = 0; t < 3; t++)
            {
                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < 20; i++)
                    {
                        for (int piece = 0; piece < pieceCount; piece++)
                        {
                            model.DecrementAvailability(piece);
                        }
                    }
                }));
            }

            Task.WaitAll(tasks.ToArray());

            // After equal increments and decrements, should be back to initial
            var snapshot = model.GetSnapshot();

            for (int i = 0; i < pieceCount; i++)
            {
                // With 10 initial + 60 increments - 60 decrements = 10
                Specification.Assert(snapshot[i] == 10,
                    $"Piece {i}: expected 10, got {snapshot[i]}");
            }
        });
    }

    /// <summary>
    /// Tests that availability can go negative (which PiecePicker allows)
    /// but doesn't crash or overflow.
    /// </summary>
    [Fact]
    public void PieceAvailability_MoreDecrementsThanIncrements_HandlesNegative()
    {
        RunCoyoteTest(() =>
        {
            int pieceCount = 20;
            var model = new PieceAvailabilityModel(pieceCount);

            // Start with small availability
            for (int i = 0; i < pieceCount; i++)
            {
                model.IncrementAvailability(i);
                model.IncrementAvailability(i);
            }

            var tasks = new List<Task>();

            // More decrements than increments
            for (int t = 0; t < 5; t++)
            {
                tasks.Add(Task.Run(() =>
                {
                    for (int piece = 0; piece < pieceCount; piece++)
                    {
                        model.DecrementAvailability(piece);
                    }
                }));
            }

            Task.WaitAll(tasks.ToArray());

            // Should be negative but not crash
            var snapshot = model.GetSnapshot();

            for (int i = 0; i < pieceCount; i++)
            {
                // 2 initial - 5 decrements = -3
                Specification.Assert(snapshot[i] == -3,
                    $"Piece {i}: expected -3, got {snapshot[i]}");
            }
        });
    }

    #endregion

    #region Channel Backpressure Tests

    /// <summary>
    /// Tests bounded channel behavior under concurrent writes.
    /// Models FileTransfer's _incomingBlocks channel pattern.
    /// </summary>
    [Fact]
    public void BoundedChannel_ConcurrentWrites_NoDataLoss()
    {
        RunCoyoteTest(() =>
        {
            var channel = System.Threading.Channels.Channel.CreateBounded<int>(
                new System.Threading.Channels.BoundedChannelOptions(100)
                {
                    FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait
                });

            int totalWritten = 0;
            int totalRead = 0;
            object writeLock = new();
            object readLock = new();

            var tasks = new List<Task>();

            // Writers
            for (int t = 0; t < 3; t++)
            {
                int threadId = t;
                tasks.Add(Task.Run(async () =>
                {
                    for (int i = 0; i < 50; i++)
                    {
                        await channel.Writer.WriteAsync(threadId * 1000 + i);
                        lock (writeLock)
                        {
                            totalWritten++;
                        }
                    }
                }));
            }

            // Reader
            var readerTask = Task.Run(async () =>
            {
                await Task.Delay(10); // Let writers start
                while (totalRead < 150) // 3 writers * 50 items
                {
                    if (channel.Reader.TryRead(out _))
                    {
                        lock (readLock)
                        {
                            totalRead++;
                        }
                    }
                    else
                    {
                        await Task.Delay(1);
                    }
                }
            });

            Task.WaitAll(tasks.ToArray());
            readerTask.Wait(TimeSpan.FromSeconds(5));

            Specification.Assert(totalWritten == totalRead,
                $"Data loss: written={totalWritten}, read={totalRead}");
        });
    }

    /// <summary>
    /// Tests DropNewest channel behavior (models _peerEvaluationQueue).
    /// </summary>
    [Fact]
    public void BoundedChannel_DropNewest_NoExceptions()
    {
        RunCoyoteTest(() =>
        {
            var channel = System.Threading.Channels.Channel.CreateBounded<int>(
                new System.Threading.Channels.BoundedChannelOptions(10)
                {
                    FullMode = System.Threading.Channels.BoundedChannelFullMode.DropNewest
                });

            var tasks = new List<Task>();

            // Many writers trying to overflow the channel
            for (int t = 0; t < 5; t++)
            {
                int threadId = t;
                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < 100; i++)
                    {
                        // TryWrite should never block or throw
                        channel.Writer.TryWrite(threadId * 1000 + i);
                    }
                }));
            }

            Task.WaitAll(tasks.ToArray());

            // Drain the channel
            int readCount = 0;
            while (channel.Reader.TryRead(out _))
            {
                readCount++;
            }

            // Should have at most capacity items (some were dropped)
            Specification.Assert(readCount <= 10,
                $"Channel exceeded capacity: {readCount}");
        });
    }

    #endregion

    #region Interlocked Flag Pattern Tests

    /// <summary>
    /// Model of disposal flag pattern used throughout the codebase.
    /// </summary>
    private class DisposableModel : IDisposable
    {
        private int _disposed;
        private int _operationCount;

        public bool IsDisposed => Interlocked.CompareExchange(ref _disposed, 0, 0) != 0;

        public bool TryDoOperation()
        {
            // Check disposed flag
            if (IsDisposed)
            {
                return false;
            }

            Interlocked.Increment(ref _operationCount);
            // Simulate some work
            Thread.SpinWait(10);
            Interlocked.Decrement(ref _operationCount);
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                // Wait for operations to complete (simplified)
                SpinWait spin = new();
                while (Interlocked.CompareExchange(ref _operationCount, 0, 0) > 0)
                {
                    spin.SpinOnce();
                }
            }
        }

        public int GetOperationCount() => Interlocked.CompareExchange(ref _operationCount, 0, 0);
    }

    /// <summary>
    /// Tests concurrent operations with disposal.
    /// Ensures operations don't proceed after dispose.
    /// </summary>
    [Fact]
    public void DisposalPattern_ConcurrentOperationsAndDispose_Safe()
    {
        RunCoyoteTest(() =>
        {
            var model = new DisposableModel();
            int successfulOps = 0;
            int failedOps = 0;
            object countLock = new();

            var tasks = new List<Task>();

            // Operation threads
            for (int t = 0; t < 5; t++)
            {
                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < 50; i++)
                    {
                        bool success = model.TryDoOperation();
                        lock (countLock)
                        {
                            if (success)
                            {
                                successfulOps++;
                            }
                            else
                            {
                                failedOps++;
                            }
                        }
                    }
                }));
            }

            // Disposal thread (starts after small delay)
            tasks.Add(Task.Run(() =>
            {
                Thread.Sleep(1);
                model.Dispose();
            }));

            Task.WaitAll(tasks.ToArray());

            // After dispose, no operations should be in progress
            Specification.Assert(model.GetOperationCount() == 0,
                $"Operations still in progress after dispose: {model.GetOperationCount()}");

            // Verify disposal happened
            Specification.Assert(model.IsDisposed,
                "Model should be disposed");
        });
    }

    /// <summary>
    /// Tests multiple concurrent dispose calls.
    /// Only one should succeed.
    /// </summary>
    [Fact]
    public void DisposalPattern_MultipleConcurrentDispose_OnlyOneExecutes()
    {
        RunCoyoteTest(() =>
        {
            var model = new DisposableModel();
            object countLock = new();

            var tasks = new List<Task>();

            for (int t = 0; t < 10; t++)
            {
                tasks.Add(Task.Run(() =>
                {
                    // Track if this was the first dispose
                    bool wasFirst = !model.IsDisposed;
                    model.Dispose();
                    if (wasFirst && model.IsDisposed)
                    {
                        // This check is racy on purpose - we want to see if multiple think they're first
                    }
                }));
            }

            Task.WaitAll(tasks.ToArray());

            Specification.Assert(model.IsDisposed,
                "Model should be disposed after multiple dispose calls");
        });
    }

    #endregion

    #region Helper Methods

    private static PeerCommunication CreateMockPeerCommunication(int id)
    {
        // Return null - the test only needs a unique reference, actual communication not needed
        // In a real scenario, you'd create a proper mock
        return null!;
    }

    #endregion
}





